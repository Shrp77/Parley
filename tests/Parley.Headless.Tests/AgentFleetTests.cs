using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// Supervising a set of agents: who gets started, who does not, and who is told to quit.
/// </summary>
/// <remarks>
/// The behaviour worth pinning here is all refusal. Starting an agent that owes an answer is the
/// easy half and it is one assertion; the rest of this file is the cases where a fleet must
/// decline to start something, because each of those is somebody's tokens, somebody's machine, or
/// an operator's decision being quietly overruled.
/// </remarks>
public sealed class AgentFleetTests : IDisposable
{
    /// <summary>The unit separator the schema joins a pair key with.</summary>
    private const string Separator = "\u001F";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-fleet-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnection _connection;

    public AgentFleetTests()
    {
        Directory.CreateDirectory(_root);
        _connection = ParleyDatabase.OpenAndMigrate(Path.Combine(_root, "parley.db"));
    }

    public void Dispose()
    {
        _connection.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Workspace(string agent)
    {
        var path = Path.Combine(_root, agent);
        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>Registers an agent, optionally consenting to being run.</summary>
    /// <param name="name">The agent.</param>
    /// <param name="invokeCommand">How to start it, or null for no consent.</param>
    /// <param name="lastSeen">When it was last heard from.</param>
    /// <param name="status">Its registry status.</param>
    private void Register(
        string name,
        string? invokeCommand = null,
        DateTimeOffset? lastSeen = null,
        string status = "live")
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agents
                (name, adapter, tier, workspace, status, last_seen_utc,
                 invoke_when_idle, invoke_command)
            VALUES (@Name, 'test', 'headless', @Workspace, @Status, @Seen, @Consent, @Command);
            """;

        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Workspace", Workspace(name));
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue(
            "@Seen",
            lastSeen is { } seen ? ExchangeRow.FormatTimestamp(seen) : DBNull.Value);
        command.Parameters.AddWithValue("@Consent", invokeCommand is null ? 0 : 1);
        command.Parameters.AddWithValue("@Command", (object?)invokeCommand ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>Opens an exchange that leaves one agent owing an answer.</summary>
    /// <param name="asker">Who asked.</param>
    /// <param name="answerer">Who owes.</param>
    private void Owe(string asker, string answerer)
    {
        var now = DateTimeOffset.UtcNow;

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, delivery_window_ms,
                 working_window_ms, grace_period_ms, opened_utc, expires_utc, question_hash)
            VALUES (@Pair, @Asker, @Answerer, @Answerer, 'OPEN', 60000, 60000, 60000,
                    @Now, @Expires, @Hash);
            """;

        // Built the way the schema's CHECK builds it: ordered, and joined by a unit separator
        // rather than any character an agent name could contain. Getting this wrong is how the
        // constraint proves it is doing something.
        var pair = string.CompareOrdinal(asker, answerer) < 0
            ? asker + Separator + answerer
            : answerer + Separator + asker;

        command.Parameters.AddWithValue("@Pair", pair);
        command.Parameters.AddWithValue("@Asker", asker);
        command.Parameters.AddWithValue("@Answerer", answerer);
        command.Parameters.AddWithValue("@Now", ExchangeRow.FormatTimestamp(now));
        command.Parameters.AddWithValue(
            "@Expires", ExchangeRow.FormatTimestamp(now.AddHours(1)));
        command.Parameters.AddWithValue("@Hash", Guid.NewGuid().ToString("N"));

        command.ExecuteNonQuery();
    }

    private AgentFleet Fleet() => new(_connection, TimeProvider.System, Lease);

    [Fact]
    public async Task An_agent_that_owes_an_answer_is_started_and_given_a_turn()
    {
        // The feature in one assertion: nobody took a turn, and an agent ran anyway.
        Register("orders");
        Register("billing", StubRunner.Answering(Workspace("billing")));
        Owe("orders", "billing");

        await using var fleet = Fleet();

        var actions = await fleet.TickAsync(Ct);

        var action = Assert.Single(actions);

        Assert.Equal("billing", action.Agent.Value);
        Assert.True(action.Started);
        Assert.Null(action.Skipped);
        Assert.True(action.Turn?.Succeeded, action.Turn?.Describe());
        Assert.Contains("billing", fleet.Running, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_second_pass_reuses_the_agent_it_already_started()
    {
        // The whole point of residency. Starting a fresh process per pass would be the one-shot
        // design wearing a supervisor's clothes, and the agent would remember nothing.
        Register("orders");
        Register("billing", StubRunner.Answering(Workspace("billing")));
        Owe("orders", "billing");

        await using var fleet = Fleet();

        var first = await fleet.TickAsync(Ct);
        var session = fleet.SessionOf(AgentName.Create("billing"));

        Owe("orders", "billing");

        var second = await fleet.TickAsync(Ct);

        Assert.True(Assert.Single(first).Started);
        Assert.False(Assert.Single(second).Started, "the fleet started a second process");
        Assert.Equal(session, fleet.SessionOf(AgentName.Create("billing")));
    }

    [Fact]
    public async Task An_agent_that_has_not_consented_is_never_started()
    {
        // Consent comes from the invoked agent's own launch configuration and nowhere else.
        // Starting a repository nobody offered is spending somebody's tokens on their machine.
        Register("orders");
        Register("billing");
        Owe("orders", "billing");

        await using var fleet = Fleet();

        Assert.Empty(await fleet.TickAsync(Ct));
        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task An_agent_somebody_is_already_running_is_left_alone()
    {
        // Two processes behind one identity would drain one inbox, and the exchange's turn check
        // refuses the loser -- after both have spent tokens getting there. §14's lease is the
        // only evidence available that a person has that repository open.
        Register("orders");
        Register(
            "billing",
            StubRunner.Answering(Workspace("billing")),
            lastSeen: DateTimeOffset.UtcNow);

        Owe("orders", "billing");

        await using var fleet = Fleet();

        var action = Assert.Single(await fleet.TickAsync(Ct));

        Assert.Contains("attended", action.Skipped, StringComparison.Ordinal);
        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task An_agent_last_seen_before_the_lease_is_treated_as_unattended()
    {
        Register("orders");
        Register(
            "billing",
            StubRunner.Answering(Workspace("billing")),
            lastSeen: DateTimeOffset.UtcNow - Lease - TimeSpan.FromMinutes(1));

        Owe("orders", "billing");

        await using var fleet = Fleet();

        Assert.True(Assert.Single(await fleet.TickAsync(Ct)).Started);
    }

    [Fact]
    public async Task A_retired_agent_is_never_started()
    {
        // Retirement is a person's judgement that the agent will not come back. §14 refuses to
        // route to one; starting a process against it would overrule the same decision.
        Register("orders");
        Register(
            "billing", StubRunner.Answering(Workspace("billing")), status: "retired");

        Owe("orders", "billing");

        await using var fleet = Fleet();

        Assert.Empty(await fleet.TickAsync(Ct));
    }

    [Fact]
    public async Task Retiring_a_running_agent_tells_it_to_quit()
    {
        // The control has to reach a process that is already running, or it is the shape of
        // control §14 warns about: the register says one thing and the deployment does another.
        Register("orders");
        Register("billing", StubRunner.Answering(Workspace("billing")));
        Owe("orders", "billing");

        await using var fleet = Fleet();

        await fleet.TickAsync(Ct);

        Assert.Contains("billing", fleet.Running, StringComparer.Ordinal);

        using (var retire = _connection.CreateCommand())
        {
            retire.CommandText = "UPDATE agents SET status = 'retired' WHERE name = 'billing';";
            retire.ExecuteNonQuery();
        }

        var actions = await fleet.TickAsync(Ct);

        Assert.Contains(
            actions, a => a.Skipped is { } why && why.Contains("withdrawn", StringComparison.Ordinal));

        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task Withdrawing_consent_from_a_running_agent_tells_it_to_quit()
    {
        // The other half of withdrawal: an operator removing PARLEY_INVOKE_COMMAND and opening
        // the repository once. It must reach a process that is already resident.
        Register("orders");
        Register("billing", StubRunner.Answering(Workspace("billing")));
        Owe("orders", "billing");

        await using var fleet = Fleet();

        await fleet.TickAsync(Ct);

        var agents = new AgentRepository(_connection);
        await agents.RefreshInvocationConsentAsync(AgentName.Create("billing"), null, Ct);

        await fleet.TickAsync(Ct);

        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task Consent_without_a_command_is_refused_by_storage()
    {
        // A row consenting to be run with nothing to run is a candidate that fails on every pass,
        // silently, since a missing command is not an error anywhere else. §9.3's argument
        // applies: the server is one writer among the ones a future adapter might add.
        Register("orders");

        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE agents SET invoke_when_idle = 1, invoke_command = NULL WHERE name = 'orders';";

        var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        Assert.Contains("how to start it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_is_recorded_with_what_it_cost()
    {
        // §17 question 1 settled a comparable question by measuring rather than guessing, and a
        // resident fleet costs considerably more than a notification.
        Register("orders");
        Register("billing", StubRunner.Answering(Workspace("billing")));
        Owe("orders", "billing");

        await using var fleet = Fleet();
        await fleet.TickAsync(Ct);

        using var query = _connection.CreateCommand();
        query.CommandText =
            "SELECT agent, owed_before, cost_usd, model_turns, timed_out FROM invocations;";

        using var reader = query.ExecuteReader();

        Assert.True(reader.Read(), "the turn was not recorded");
        Assert.Equal("billing", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0.25, reader.GetDouble(2), 3);
        Assert.Equal(2L, reader.GetInt64(3));
        Assert.Equal(0L, reader.GetInt64(4));
    }

    [Fact]
    public async Task An_agent_whose_runner_will_not_start_is_reported_and_the_pass_continues()
    {
        // One misconfigured agent must not end the pass that serves all of them, and it must not
        // be silent either -- the exchange simply stays owed and nobody learns why.
        Register("orders");
        Register("billing", "parley-no-such-runner --input-format stream-json");
        Register("shipping", StubRunner.Answering(Workspace("shipping")));

        Owe("orders", "billing");
        Owe("orders", "shipping");

        await using var fleet = Fleet();

        var actions = await fleet.TickAsync(Ct);

        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, a => a.Agent.Value == "billing" && a.Skipped is not null);
        Assert.Contains(actions, a => a.Agent.Value == "shipping" && a.Turn is not null);
    }

    [Fact]
    public async Task Stopping_the_fleet_stops_every_agent()
    {
        Register("orders");
        Register("billing", StubRunner.Answering(Workspace("billing")));
        Register("shipping", StubRunner.Answering(Workspace("shipping")));

        Owe("orders", "billing");
        Owe("orders", "shipping");

        var fleet = Fleet();

        await fleet.TickAsync(Ct);

        Assert.Equal(2, fleet.Running.Count);

        var stopped = await fleet.StopAllAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(2, stopped);
        Assert.Empty(fleet.Running);

        await fleet.DisposeAsync();
    }
}
