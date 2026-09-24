using Parley.Configuration;
﻿using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley prune</c>: SPEC.md §10.2's last operation, and the one that deletes.
/// </summary>
/// <remarks>
/// <para>
/// What the pruning itself decides is tested in <c>Parley.Store.Tests</c>. What is here is the
/// part that belongs to a command rather than to a query: that it says which policy it applied
/// before it says what it removed.
/// </para>
/// <para>
/// That ordering is the whole design of this command. The numbers come from the environment, and
/// an operator's shell is not the environment their MCP client launched the agents' servers
/// with — so a <c>prune</c> that reported only a count could quietly remove a month of log from
/// a deployment configured to keep a year, and the evidence would be the thing it deleted.
/// </para>
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

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
    private readonly StubClock _clock;
    private readonly LoopbackOperator _broker;
    private readonly OperatorCommands _commands;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-prune-" + Guid.NewGuid().ToString("N"));

    public PruneCommandTests()
    {
        Directory.CreateDirectory(_directory);

        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _clock = new StubClock(T0);
        _broker = LoopbackOperator.Start(_connection, RetentionPolicy.Default, _clock);
        _commands = new OperatorCommands(_broker.Operators, _output);
    }

    public async ValueTask DisposeAsync()
    {
        // The broker first: it borrowed this connection and its shutdown writes to it.
        await _broker.DisposeAsync();

        _connection.Dispose();
        _output.Dispose();

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
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Printed => _output.ToString();

    /// <summary>Requests, collects and resolves, so the exchange ends and its floor lifts.</summary>
    private async Task SettledExchange()
    {
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var sent = await broker.RequestAsync(Orders, Billing, Model, Request, T0, "pricing", Ct);
        Assert.True(sent.Exchange is not null, $"expected admission, got {sent.Admission.Rejection}");

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.True((await broker.ResultAsync(
            sent.Exchange.Id, Billing, Model, Result, T0.AddMinutes(2), cancellationToken: Ct)).Succeeded);

        await broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: Ct);
    }

    [Fact]
    public async Task It_says_where_the_policy_came_from_before_it_says_what_it_removed()
    {
        // This used to assert that the two hour figures were printed, because the policy was
        // read from the operator's own shell and a command that deletes text on a configured
        // schedule had to say which schedule it used.
        //
        // The premise moved rather than the requirement. There is one process holding the store,
        // it was started with one policy, and this command cannot apply a different one however
        // the shell is configured -- so printing the shell's numbers would now be printing
        // something that had no effect on what was removed, which is worse than printing
        // nothing. What is still owed is the broker's numbers, and the honest place to get them
        // is the reply.
        await SettledExchange();

        _clock.Advance(TimeSpan.FromDays(10));

        Assert.Equal(0, await _commands.PruneAsync(Ct));

        var policyLine = Printed.Split('\n')[0];

        Assert.Contains("the broker's", policyLine, StringComparison.Ordinal);
        Assert.Contains(RetentionPolicyEnvironment.IdleVariable, policyLine, StringComparison.Ordinal);
        Assert.Contains(RetentionPolicyEnvironment.MaximumAgeVariable, policyLine, StringComparison.Ordinal);

        Assert.Contains("Removed 2 message(s).", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_nothing_is_still_a_success()
    {
        // Unlike retire and reinstate, which exit non-zero when the registry did not change.
        // Here "nothing was old enough" is the steady state of a healthy deployment, and a cron
        // job that failed most nights would train whoever reads it to ignore the one night it
        // meant something.
        await SettledExchange();

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await _commands.PruneAsync(Ct));
        Assert.Contains("Removed 0 message(s).", Printed, StringComparison.Ordinal);

        // And it does not volunteer the floor when the floor is holding nothing, because an
        // explanation printed every time is one nobody reads on the run that matters.
        Assert.DoesNotContain("were old enough and were kept", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_floor_holds_is_reported_and_explained()
    {
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        // Sent and never collected: sixty days old, and held anyway.
        Assert.NotNull((await broker.RequestAsync(Orders, Billing, Model, Request, T0, null, Ct)).Exchange);

        _clock.Advance(TimeSpan.FromDays(60));

        Assert.Equal(0, await _commands.PruneAsync(Ct));

        Assert.Contains("Removed 0 message(s).", Printed, StringComparison.Ordinal);
        Assert.Contains("1 more were old enough and were kept", Printed, StringComparison.Ordinal);
        Assert.Contains("not configurable", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_policy_is_not_the_operators_to_supply()
    {
        // This asserted that a null policy threw, back when the caller passed one. It cannot
        // now, and the replacement pins why that is an improvement rather than a lost check: an
        // operator's shell is not the environment the broker was started with, so a policy
        // passed from here could have deleted text to a floor the deployment had not chosen.
        Assert.DoesNotContain(
            typeof(OperatorCommands).GetMethods(),
            m => m.Name == nameof(OperatorCommands.PruneAsync)
                 && m.GetParameters().Any(p => p.ParameterType == typeof(RetentionPolicy)));
    }

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
            "serve", "--config", _directory);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(RetentionPolicyEnvironment.IdleVariable, error, StringComparison.Ordinal);
        Assert.Contains("soon", error, StringComparison.Ordinal);

        Assert.Equal(1, await MessageCount(path));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("force-conclude")]
    public async Task A_blank_conversation_identifier_is_refused_rather_than_thrown(string verb)
    {
        // `parley export ""` used to reach the repository, hit an ArgumentException on the way
        // in, and print a .NET stack trace with exit code 127 — the operator's own input coming
        // back at them as a crash. Found by typing it, on the side of the trust boundary where
        // nobody was looking.
        var path = await ProjectDatabase();

        using var broker = BrokerProcess.Start(_directory);

        var (exitCode, error) = ToolProcess.Run("--broker", broker.Address, verb, "");

        Assert.Equal(64, exitCode);
        Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        Assert.Contains("identifier is required", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_takes_no_operand()
    {
        // `parley prune pricing` reads as "prune this conversation", and there is no such
        // operation — §9.4 has no per-conversation exemption in either direction. Refusing is
        // better than the alternatives, both of which are wrong in a way that looks like success.
        var path = await ProjectDatabase();

        using var broker = BrokerProcess.Start(_directory);

        var (exitCode, _) = ToolProcess.Run("--broker", broker.Address, "prune", "pricing");

        Assert.Equal(64, exitCode);
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
        var path = Path.Combine(_directory, "settled.db");
        var then = DateTimeOffset.UtcNow.AddHours(-4);

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
        // End to end through two real binaries, and the same file twice. The variables belong
        // to the broker now, so the way to see that they are read at all is to start two brokers
        // over one store with different settings and watch them reach different answers — which
        // is also the demonstration that the operator's shell no longer decides anything.
        var path = await RecentlySettledDatabase();

        using (var relaxed = BrokerProcess.Start(_directory))
        {
            var (unconfigured, _) = ToolProcess.Run("--broker", relaxed.Address, "prune");

            Assert.Equal(0, unconfigured);
            Assert.Equal(2, await MessageCount(path));
        }

        using (var strict = BrokerProcess.Start(
            path,
            (RetentionPolicyEnvironment.IdleVariable, "1"),
            (RetentionPolicyEnvironment.MaximumAgeVariable, "1")))
        {
            var (configured, error) = ToolProcess.Run("--broker", strict.Address, "prune");

            Assert.Equal(0, configured);
            Assert.Equal("", error);
            Assert.Equal(0, await MessageCount(path));
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
            path,
            (RetentionPolicyEnvironment.IdleVariable, "1"),
            (RetentionPolicyEnvironment.MaximumAgeVariable, "1"));

        var (exitCode, _) = ToolProcess.Run("--broker", broker.Address, "prune");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, await MessageCount(path));
    }
}
