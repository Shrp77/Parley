using Parley.Configuration;
﻿using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// The broker's retention pass, asked over the wire as an operator client does.
/// </summary>
/// <remarks>
/// What the pruning itself decides is tested in <c>Parley.Store.Tests</c>. What is here is that a
/// real broker process reads its own policy from its own environment and applies that, never the
/// asker's.
/// </remarks>
public sealed class PruneCommandTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Request =
        """{"ask":"Where does price live, and is it gross or net of tax?"}""";

    private const string Result =
        """{"outcomes":[{"id":"request","outcome":"answered","text":"net"}]}""";

    private const string Model = "some-model-9";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-prune-" + Guid.NewGuid().ToString("N"));

    public PruneCommandTests() => Directory.CreateDirectory(_directory);

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort. A pooled connection can still hold the file, and the alternative --
            // SqliteConnection.ClearAllPools() -- is process-global and disposes handles
            // that other test classes are using. See BannedSymbols.txt.
        }

        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the process ----

    /// <summary>Builds a real Parley database with one message in it.</summary>
    private async Task<string> ProjectDatabase()
    {
        var path = BrokerProcess.StoreFor(_directory);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var connection = ParleyDatabase.OpenAndMigrate(path))
        {
            var agents = new AgentRepository(connection);

            foreach (var name in new[] { Orders, Billing })
            {
                await agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), Ct);
            }

            var broker = new Broker.Broker(connection, BrokerOptions.Default);
            await broker.StartAsync(Orders, T0, Ct);
            Assert.NotNull((await broker.RequestAsync(Orders, Billing, Model, Request, T0, null, Ct)).Exchange);
        }

        return path;
    }

    /// <summary>Asks a broker in another process, as a client of it rather than through the tool.</summary>
    /// <typeparam name="T">What the operation answers with.</typeparam>
    /// <param name="broker">The running broker.</param>
    /// <param name="ask">The operation.</param>
    /// <returns>Its answer.</returns>
    /// <remarks>
    /// <para>
    /// <b>These checks were driven through <c>parley prune</c> and their subject was never the
    /// command.</b> What they pin is the broker's own retention arithmetic — which floor it holds
    /// to, and which numbers it read when it started — and the tool was only the thing that asked.
    /// The verb moved to <c>parley terminal</c>, so the question is asked the way every other
    /// surface asks it: over the wire, as an operator client.
    /// </para>
    /// <para>
    /// A channel per call rather than one per class. The tests here start and stop brokers inside a
    /// single method, and a channel outliving the broker it was opened against is a teardown that
    /// waits rather than a test that fails.
    /// </para>
    /// </remarks>
    private static async Task<T> AskAsync<T>(BrokerProcess broker, Func<BrokerClient, Task<T>> ask)
    {
        using var channel = GrpcChannel.ForAddress(broker.Address);

        return await ask(new BrokerClient(new Wire.Operators.OperatorsClient(channel)));
    }

    private async Task<long> MessageCount(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(Ct);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_retention_variable_it_cannot_read_stops_prune_and_changes_nothing()
    {
        // Refused rather than defaulted, and refused before anything is deleted. An operator who
        // wrote a value meant something by it, and falling back silently would prune on numbers
        // they did not choose — for this command, that means the evidence is already gone by the
        // time anybody checks.
        // Read by the broker now, not by the operator's shell — so an unreadable value stops
        // `parley serve` rather than `parley prune`. That is a better place for it: the value
        // decides what a background sweep deletes on a schedule, and refusing at the one moment
        // somebody is watching beats refusing whenever they next happen to run a command.
        // A root with one project in it, because `serve` takes no store and no port. What this
        // probes is unchanged: an unreadable retention value stops the broker rather than the
        // operator's shell, at the one moment somebody is watching.
        // **A project rather than a store and a port.** `serve` takes neither; a rig names a root and
        // writes a project into it, and the store this then checks is that project's own.
        var path = await ProjectDatabase();

        ProjectManifest.Write(
            _directory,
            [new ProjectSettings(
                ProjectName.Create(BrokerProcess.ProjectName),
                path,
                "http://127.0.0.1:0",
                null)]);

        var (exitCode, error) = ToolProcess.Run(
            [(RetentionPolicyEnvironment.IdleVariable, "soon")],
            // `--project` for the reason `ForeignDatabaseTests` gives at its own `serve`: a root
            // alone watches now, and a watching broker reports rather than exits. Naming the
            // project is the "serve one alone" path, which still refuses.
            "serve", "--config", _directory, "--project", BrokerProcess.ProjectName);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(RetentionPolicyEnvironment.IdleVariable, error, StringComparison.Ordinal);
        Assert.Contains("soon", error, StringComparison.Ordinal);

        Assert.Equal(1, await MessageCount(path));
    }

    /// <summary>
    /// Builds a real Parley database holding one discussion that finished four hours ago.
    /// </summary>
    /// <remarks>
    /// Dated against the wall clock rather than a fixture instant, because the process reads its
    /// own <c>DateTimeOffset.UtcNow</c> and nothing can hand it another one. Four hours is chosen
    /// to sit between the default idle period and an hour: old enough that a tight policy
    /// releases it, young enough that the default does not.
    /// </remarks>
    private async Task<string> RecentlySettledDatabase()
    {
        // The project's own store, so the broker this rig starts opens the one this fills.
        var path = BrokerProcess.StoreFor(_directory);
        var then = DateTimeOffset.UtcNow.AddHours(-4);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var connection = ParleyDatabase.OpenAndMigrate(path))
        {
            var agents = new AgentRepository(connection);

            foreach (var name in new[] { Orders, Billing })
            {
                await agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), Ct);
            }

            var broker = new Broker.Broker(connection, BrokerOptions.Default);
            await broker.StartAsync(Orders, then, Ct);

            var sent = await broker.RequestAsync(Orders, Billing, Model, Request, then, null, Ct);
            Assert.True(sent.Exchange is not null, $"expected admission, got {sent.Admission.Rejection}");

            await broker.DrainInboxAsync(Billing, then.AddMinutes(1), cancellationToken: Ct);

            Assert.True((await broker.ResultAsync(
                sent.Exchange.Id, Billing, Model, Result, then.AddMinutes(2), cancellationToken: Ct)).Succeeded);

            await broker.DrainInboxAsync(Orders, then.AddMinutes(3), cancellationToken: Ct);
        }

        return path;
    }

    [Fact]
    public async Task The_configured_policy_is_the_one_the_process_applies()
    {
        // Against a real broker, and the same file twice. The variables belong to the broker now,
        // so the way to see that they are read at all is to start two brokers over one store with
        // different settings and watch them reach different answers — which is also the
        // demonstration that the operator's shell no longer decides anything.
        //
        // **This drove two binaries until `prune` left the command line.** It still starts the
        // process whose settings are the subject; what changed is that the asking is done by a
        // client in this process rather than by a second tool, which is what every surface that
        // can still prune does.
        var path = await RecentlySettledDatabase();

        using (var relaxed = BrokerProcess.Start(_directory))
        {
            var unconfigured = await AskAsync(relaxed, client => client.PruneAsync(Ct));

            Assert.Equal(0, unconfigured.Removed);
            Assert.Equal(2, await MessageCount(path));

            // And it says which numbers it applied (SPEC.md §9.4): the defaults, here.
            Assert.Equal(RetentionPolicy.Default, unconfigured.Applied);
        }

        using (var strict = BrokerProcess.Start(
            _directory,
            (RetentionPolicyEnvironment.IdleVariable, "1"),
            (RetentionPolicyEnvironment.MaximumAgeVariable, "1")))
        {
            var configured = await AskAsync(strict, client => client.PruneAsync(Ct));

            Assert.Equal(2, configured.Removed);
            Assert.Equal(0, await MessageCount(path));
            Assert.Equal(new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1)), configured.Applied);
        }
    }

    [Fact]
    public async Task No_policy_deletes_an_exchange_that_has_not_closed()
    {
        // The floor, proven through the process rather than argued from the query. An hour of
        // retention against a question minutes old must leave it exactly where it is: §14 may
        // still replay it, and the question text is what there would be to replay.
        var path = await ProjectDatabase();

        using var broker = BrokerProcess.Start(
            _directory,
            (RetentionPolicyEnvironment.IdleVariable, "1"),
            (RetentionPolicyEnvironment.MaximumAgeVariable, "1"));

        var pruned = await AskAsync(broker, client => client.PruneAsync(Ct));

        Assert.Equal(0, pruned.Removed);
        Assert.Equal(1, await MessageCount(path));
    }
}
