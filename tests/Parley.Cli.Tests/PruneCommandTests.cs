using Microsoft.Data.Sqlite;
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
public sealed class PruneCommandTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Question =
        """{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"If it does I read it from you; if not I snapshot my own copy and we can diverge."}""";

    private const string Model = "some-model-9";

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
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
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _commands = new OperatorCommands(new OperatorConsole(_connection), _output);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _output.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; pooled connections keep the file open.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Printed => _output.ToString();

    /// <summary>Asks, collects and answers, so the exchange ends and its floor lifts.</summary>
    private async Task SettledExchange()
    {
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var asked = await broker.AskAsync(Orders, Billing, Model, Question, T0, "pricing", Ct);
        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.True((await broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(2), cancellationToken: Ct)).Succeeded);

        await broker.DrainInboxAsync(Orders, T0.AddMinutes(3), cancellationToken: Ct);
    }

    [Fact]
    public async Task It_says_which_policy_it_applied_before_it_says_what_it_removed()
    {
        await SettledExchange();

        Assert.Equal(0, await _commands.PruneAsync(
            RetentionPolicy.Default, T0.AddDays(10), Ct));

        var policyLine = Printed.Split('\n')[0];

        // Both numbers and both variable names, on the first line, so an operator who did not
        // realise their shell was unconfigured can see it without knowing to look.
        Assert.Contains("72 hours", policyLine, StringComparison.Ordinal);
        Assert.Contains("720 hours", policyLine, StringComparison.Ordinal);
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

        Assert.Equal(0, await _commands.PruneAsync(RetentionPolicy.Default, T0.AddHours(1), Ct));
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

        // Asked and never collected: past every deadline, and held anyway.
        Assert.True((await broker.AskAsync(Orders, Billing, Model, Question, T0, null, Ct))
            .Succeeded);

        Assert.Equal(0, await _commands.PruneAsync(RetentionPolicy.Default, T0.AddDays(60), Ct));

        Assert.Contains("Removed 0 message(s).", Printed, StringComparison.Ordinal);
        Assert.Contains("1 more were old enough and were kept", Printed, StringComparison.Ordinal);
        Assert.Contains("not configurable", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_policy_is_required()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _commands.PruneAsync(null!, T0, Ct));
    }

    // ---- the process ----

    /// <summary>Builds a real Parley database with one message in it.</summary>
    private async Task<string> ProjectDatabase()
    {
        var path = Path.Combine(_directory, "parley.db");

        using (var connection = ParleyDatabase.OpenAndMigrate(path))
        {
            var agents = new AgentRepository(connection);

            foreach (var name in new[] { Orders, Billing })
            {
                await agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, null,
                        AgentStatus.Registered), Ct);
            }

            var broker = new Broker.Broker(connection, BrokerOptions.Default);
            await broker.StartAsync(Orders, T0, Ct);
            Assert.True((await broker.AskAsync(Orders, Billing, Model, Question, T0, null, Ct))
                .Succeeded);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private async Task<long> MessageCount(string path)
    {
        SqliteConnection.ClearAllPools();

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
        var path = await ProjectDatabase();

        var (exitCode, error) = ToolProcess.Run(
            [(RetentionPolicyEnvironment.IdleVariable, "soon")], "--db", path, "prune");

        Assert.Equal(78, exitCode);
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

        var (exitCode, error) = ToolProcess.Run("--db", path, verb, "");

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

        var (exitCode, _) = ToolProcess.Run("--db", path, "prune", "pricing");

        Assert.Equal(64, exitCode);
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
        var path = Path.Combine(_directory, "settled.db");
        var then = DateTimeOffset.UtcNow.AddHours(-4);

        using (var connection = ParleyDatabase.OpenAndMigrate(path))
        {
            var agents = new AgentRepository(connection);

            foreach (var name in new[] { Orders, Billing })
            {
                await agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, null,
                        AgentStatus.Registered), Ct);
            }

            var broker = new Broker.Broker(connection, BrokerOptions.Default);
            await broker.StartAsync(Orders, then, Ct);

            var asked = await broker.AskAsync(Orders, Billing, Model, Question, then, null, Ct);
            Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

            await broker.DrainInboxAsync(Billing, then.AddMinutes(1), cancellationToken: Ct);

            Assert.True((await broker.AnswerAsync(
                asked.Exchange!.Id, Billing, Model,
                """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
                then.AddMinutes(2), cancellationToken: Ct)).Succeeded);

            await broker.DrainInboxAsync(Orders, then.AddMinutes(3), cancellationToken: Ct);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public async Task The_configured_policy_is_the_one_the_process_applies()
    {
        // End to end through the real binary, and the same file twice. The variables the agents'
        // servers read are the variables this reads, which is what keeps a project's sweep and
        // its operator from applying different numbers to the same log — and the only way to see
        // that they are being read at all is for two settings to reach different answers.
        var path = await RecentlySettledDatabase();

        var (unconfigured, _) = ToolProcess.Run("--db", path, "prune");

        Assert.Equal(0, unconfigured);
        Assert.Equal(2, await MessageCount(path));

        var (configured, error) = ToolProcess.Run(
            [
                (RetentionPolicyEnvironment.IdleVariable, "1"),
                (RetentionPolicyEnvironment.MaximumAgeVariable, "1")
            ],
            "--db", path, "prune");

        Assert.Equal(0, configured);
        Assert.Equal("", error);
        Assert.Equal(0, await MessageCount(path));
    }

    [Fact]
    public async Task No_policy_deletes_an_exchange_that_has_not_closed()
    {
        // The floor, proven through the process rather than argued from the query. An hour of
        // retention against a question minutes old must leave it exactly where it is: §14 may
        // still replay it, and the question text is what there would be to replay.
        var path = await ProjectDatabase();

        var (exitCode, _) = ToolProcess.Run(
            [
                (RetentionPolicyEnvironment.IdleVariable, "1"),
                (RetentionPolicyEnvironment.MaximumAgeVariable, "1")
            ],
            "--db", path, "prune");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, await MessageCount(path));
    }
}
