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

    /// <summary>Registers an agent, and tells this machine how to start it.</summary>
    /// <param name="name">The agent.</param>
    /// <param name="invokeCommand">How to start it, or null if this machine does not know.</param>
    /// <param name="connected">Whether its adapter is attached.</param>
    /// <param name="status">Its registry status.</param>
    /// <remarks>
    /// Two writes to two places, which is the change these tests exist to describe. The
    /// registration goes in the store; how to start the agent goes in the node's own catalogue
    /// and never leaves this machine (SPEC.md §4). An agent registered without a catalogue entry
    /// is one the fleet declines to start, which used to be expressed as withheld consent and is
    /// now simply a machine that has not been told about it.
    /// </remarks>
    private void Register(
        string name,
        string? invokeCommand = null,
        bool connected = false,
        string status = "active")
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agents (name, adapter, tier, workspace, status, connected)
            VALUES (@Name, 'test', 'headless', @Workspace, @Status, @Connected);
            """;

        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Workspace", Workspace(name));
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@Connected", connected ? 1 : 0);
        command.ExecuteNonQuery();

        if (invokeCommand is not null)
        {
            _catalogue[AgentName.Create(name).Role] = invokeCommand;
        }
    }

    /// <summary>What this machine has been told it can start, by role.</summary>
    private readonly Dictionary<string, string> _catalogue = new(StringComparer.Ordinal);

    /// <summary>Builds a catalogue file for the current fixture and reads it back.</summary>
    /// <returns>The catalogue the fleet consults.</returns>
    private NodeCatalogue Catalogue()
    {
        var agents = string.Join(",", _catalogue.Select(e =>
            $"\"{e.Key}\": {{ \"command\": {System.Text.Json.JsonSerializer.Serialize(e.Value)} }}"));

        var path = Path.Combine(_root, "node.json");
        File.WriteAllText(path, $"{{ \"agents\": {{ {agents} }} }}");

        return NodeCatalogue.Read(Path.Combine(_root, "parley.db"));
    }

    /// <summary>The 64-character lower-case hex the schema requires of a content hash.</summary>
    /// <param name="text">Whatever distinguishes this row from its neighbours.</param>
    /// <returns>A hash the CHECK constraint accepts.</returns>
    private static string Hash(string text) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    /// <summary>Writes a message nobody has collected, leaving no debt behind it.</summary>
    /// <param name="from">Who sent it.</param>
    /// <param name="to">Who has not collected it.</param>
    /// <param name="type">The message type to write.</param>
    private void SendUncollected(string from, string to, string type)
    {
        // A message with no exchange behind it, which is what an answer, a ghost and a notify
        // all leave once the exchange they belonged to has closed -- or, for a notify, never
        // had one. Written directly because the point is the row, not the path that made it.
        // Only a notify may stand alone: the schema enforces
        // (type = 'notify') = (exchange_id IS NULL). An answer and a ghost belong to an
        // exchange that has since closed, which is exactly the shape that leaves a message
        // waiting and no debt behind it -- so the closed exchange is written too rather than
        // worked around.
        long? exchange = null;

        if (type != "notify")
        {
            using var closing = _connection.CreateCommand();
            closing.CommandText =
                """
                INSERT INTO exchanges
                    (pair_key, asker, answerer, owed_by, state, delivery_window_ms,
                     working_window_ms, grace_period_ms, opened_utc, expires_utc,
                     question_hash, closed_utc, resolution)
                VALUES (@Pair, @To, @From, @From, 'CLOSED', 60000, 60000, 60000,
                        @Now, @Now, @Hash, @Now, 'answered')
                RETURNING id;
                """;
            closing.Parameters.AddWithValue("@Pair", string.CompareOrdinal(from, to) < 0
                ? from + Separator + to
                : to + Separator + from);
            closing.Parameters.AddWithValue("@To", to);
            closing.Parameters.AddWithValue("@From", from);
            closing.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("o"));
            closing.Parameters.AddWithValue("@Hash", Hash($"{from}-{to}-{type}-q"));
            exchange = (long)closing.ExecuteScalar()!;
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, created_utc)
            VALUES (@Exchange, @Type, @From, @To, @Responder, '{"text":"x"}', @Hash, @Now);
            """;

        // A ghost is the broker's own notice and the schema refuses any other attribution --
        // the same trigger that stops an agent forging one. §14.
        command.Parameters.AddWithValue(
            "@Responder", type == "ghost" ? "parley:broker" : "some-model-9");
        command.Parameters.AddWithValue("@Exchange", (object?)exchange ?? DBNull.Value);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@From", from);
        command.Parameters.AddWithValue("@To", to);
        command.Parameters.AddWithValue("@Hash", Hash($"{from}-{to}-{type}"));
        command.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("o"));
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

    /// <summary>The fleet under test, consulting this machine's catalogue.</summary>
    /// <returns>A fleet that can start whatever Register was given a command for.</returns>
    private AgentFleet Fleet() => new(_connection, TimeProvider.System, Catalogue());

    [Fact]
    public async Task An_agent_that_owes_an_answer_is_started_and_given_a_turn()
    {
        // The feature in one assertion: nobody took a turn, and an agent ran anyway.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        var actions = await fleet.TickAsync(Ct);

        var action = Assert.Single(actions);

        Assert.Equal("billing@test", action.Agent.Value);
        Assert.True(action.Started);
        Assert.Null(action.Skipped);
        Assert.True(action.Turn?.Succeeded, action.Turn?.Describe());
        Assert.Contains("billing@test", fleet.Running, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_second_pass_reuses_the_agent_it_already_started()
    {
        // The whole point of residency. Starting a fresh process per pass would be the one-shot
        // design wearing a supervisor's clothes, and the agent would remember nothing.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        var first = await fleet.TickAsync(Ct);
        var session = fleet.SessionOf(AgentName.Create("billing@test"));

        Owe("orders@test", "billing@test");

        var second = await fleet.TickAsync(Ct);

        Assert.True(Assert.Single(first).Started);
        Assert.False(Assert.Single(second).Started, "the fleet started a second process");
        Assert.Equal(session, fleet.SessionOf(AgentName.Create("billing@test")));
    }

    [Fact]
    public async Task An_agent_this_machine_cannot_start_is_reported_not_started()
    {
        // Consent comes from the invoked agent's own launch configuration and nowhere else.
        // Starting a repository nobody offered is spending somebody's tokens on their machine.
        Register("orders@test");
        Register("billing@test");
        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        // Reported rather than silent. An agent this machine has no command for is a
        // configuration gap somebody has to see: under the old design it was withheld consent
        // and produced nothing at all, which looked identical to a healthy quiet pass.
        var skipped = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(skipped.Started);
        Assert.Contains("no launch command", skipped.Skipped, StringComparison.Ordinal);
        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task An_agent_somebody_is_already_running_is_left_alone()
    {
        // Two processes behind one identity would drain one inbox, and the exchange's turn check
        // refuses the loser -- after both have spent tokens getting there. §14's lease is the
        // only evidence available that a person has that repository open.
        Register("orders@test");
        Register(
            "billing@test",
            StubRunner.Answering(Workspace("billing@test")),
            connected: true);

        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        var action = Assert.Single(await fleet.TickAsync(Ct));

        Assert.Contains("attended", action.Skipped, StringComparison.Ordinal);
        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task An_agent_with_no_connection_is_treated_as_unattended()
    {
        Register("orders@test");
        Register(
            "billing@test",
            StubRunner.Answering(Workspace("billing@test")),
            connected: false);

        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        Assert.True(Assert.Single(await fleet.TickAsync(Ct)).Started);
    }

    [Fact]
    public async Task A_retired_agent_is_never_started()
    {
        // Retirement is a person's judgement that the agent will not come back. §14 refuses to
        // route to one; starting a process against it would overrule the same decision.
        Register("orders@test");
        Register(
            "billing@test", StubRunner.Answering(Workspace("billing@test")), status: "retired");

        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        Assert.Empty(await fleet.TickAsync(Ct));
    }

    [Fact]
    public async Task Retiring_a_running_agent_tells_it_to_quit()
    {
        // The control has to reach a process that is already running, or it is the shape of
        // control §14 warns about: the register says one thing and the deployment does another.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        await fleet.TickAsync(Ct);

        Assert.Contains("billing@test", fleet.Running, StringComparer.Ordinal);

        using (var retire = _connection.CreateCommand())
        {
            retire.CommandText = "UPDATE agents SET status = 'retired' WHERE name = 'billing@test';";
            retire.ExecuteNonQuery();
        }

        var actions = await fleet.TickAsync(Ct);

        Assert.Contains(
            actions, a => a.Skipped is { } why && why.Contains("withdrawn", StringComparison.Ordinal));

        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task Delisting_a_running_agent_tells_it_to_quit()
    {
        // Withdrawal used to be removing PARLEY_INVOKE_COMMAND and opening the repository once,
        // so the registry learned about it and the fleet acted on a column. It is now removing
        // the agent from this machine's own catalogue, which is a smaller and more direct thing:
        // the party that starts agents is the party that stops being told to.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();

        await fleet.TickAsync(Ct);
        Assert.Contains("billing@test", fleet.Running);

        // Delisted. The fleet holds the catalogue it was constructed with, so this reaches the
        // next pass the way an operator editing node.json and restarting the supervisor would.
        _catalogue.Remove("billing");

        await using var delisted = Fleet();

        await delisted.TickAsync(Ct);

        Assert.Empty(delisted.Running);
    }

    [Fact]
    public void An_agent_on_another_machine_is_never_started_from_here()
    {
        // The check that stops a catalogue meaning more than it says. It describes one machine,
        // so an entry for `billing` here means billing@thisMachine and nothing else — quietly
        // matching billing@server-b would have this node start a process for a repository that is
        // not on it, under a name somebody else holds.
        _catalogue["billing"] = "runner";

        var catalogue = Catalogue();

        Assert.NotNull(catalogue.CommandFor(AgentName.Create("billing@test")));
        Assert.Null(catalogue.CommandFor(AgentName.Create("billing@server-b")));
    }

    [Fact]
    public void A_machine_told_about_nothing_starts_nothing_rather_than_failing()
    {
        // The ordinary state of a machine somebody has only registered agents on. It must read
        // as "starts nothing" rather than as an error, or a supervisor could not run anywhere it
        // had not been configured first — and it still has reaping and reporting to do.
        Assert.Null(NodeCatalogue.Empty.CommandFor(AgentName.Create("billing@test")));
        Assert.Empty(NodeCatalogue.Empty.Roles);
    }

    [Fact]
    public async Task A_turn_is_recorded_with_what_it_cost()
    {
        // §17 question 1 settled a comparable question by measuring rather than guessing, and a
        // resident fleet costs considerably more than a notification.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        await using var fleet = Fleet();
        await fleet.TickAsync(Ct);

        using var query = _connection.CreateCommand();
        query.CommandText =
            "SELECT agent, waiting_before, cost_usd, model_turns, timed_out FROM invocations;";

        using var reader = query.ExecuteReader();

        Assert.True(reader.Read(), "the turn was not recorded");
        Assert.Equal("billing@test", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0.25, reader.GetDouble(2), 3);
        Assert.Equal(2L, reader.GetInt64(3));
        Assert.Equal(0L, reader.GetInt64(4));
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("ghost")]
    [InlineData("notify")]
    public async Task An_uncollected_message_starts_an_agent_that_owes_nothing(string type)
    {
        // The hole this closes. Only an OPEN exchange used to start anybody, and three of the
        // five message types leave none: an answer and a ghost close the exchange they belong
        // to, and a notify never had one. So the fleet could answer a question autonomously and
        // never start the agent that asked it to read the reply -- the one participant with the
        // strongest reason to be woken was the only one excluded.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test");
        SendUncollected("billing@test", "orders@test", type);

        await using var fleet = Fleet();
        var actions = await fleet.TickAsync(Ct);

        var started = Assert.Single(actions);

        Assert.Equal("orders@test", started.Agent.Value);
        Assert.True(started.Started, started.Skipped ?? "not started");
        Assert.Equal(1, started.WaitingBefore);
    }

    [Fact]
    public async Task A_debt_and_an_uncollected_message_are_counted_once_each()
    {
        // Counted as distinct items rather than summed from two overlapping queries: an OPEN
        // exchange and a message that has not been collected are two things to deal with, and
        // an agent holding both should sort above one holding either.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");
        SendUncollected("orders@test", "billing@test", "notify");

        await using var fleet = Fleet();
        var actions = await fleet.TickAsync(Ct);

        Assert.Equal(2, Assert.Single(actions).WaitingBefore);
    }

    [Fact]
    public async Task A_turn_that_collected_nothing_is_not_repeated_until_something_arrives()
    {
        // The runaway this guards. An owed exchange ages out through §14 whether or not the
        // agent cooperates, so a debt it refuses eventually stops being a reason to start it.
        // An uncollected message has no such schedule -- §9.4's floor holds it against
        // retention precisely so nothing is lost -- so without a guard, one unread notification
        // starts a real session every pass, for as long as the fleet runs, on somebody's
        // account. The stub answers its turn and touches no database, which is exactly the
        // shape of an agent that is started and collects nothing.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test");
        SendUncollected("billing@test", "orders@test", "notify");

        await using var fleet = Fleet();

        Assert.True(Assert.Single(await fleet.TickAsync(Ct)).Started);

        var second = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(second.Started);
        Assert.Contains("collected nothing", second.Skipped!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_message_re_arms_an_agent_that_was_stuck()
    {
        // The bound is one wasted turn per new message, not one wasted turn ever. A second
        // message raises the count above what was still waiting after the last turn, which is
        // the evidence that there is something the previous turn cannot have seen.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test");
        SendUncollected("billing@test", "orders@test", "notify");

        await using var fleet = Fleet();
        await fleet.TickAsync(Ct);
        Assert.False(Assert.Single(await fleet.TickAsync(Ct)).Started);

        SendUncollected("billing@test", "orders@test", "answer");

        var third = Assert.Single(await fleet.TickAsync(Ct));

        Assert.Null(third.Skipped);
        Assert.Equal(2, third.WaitingBefore);
    }

    [Fact]
    public async Task An_agent_whose_workspace_is_gone_is_retired()
    {
        // §14's new half. A missing directory is an observation the broker can make by looking,
        // rather than an inference from silence, so it is allowed to conclude from it. The cost
        // is recorded where the code is: a repository that comes back needs `parley reinstate`.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE agents SET workspace = @Gone WHERE name = 'billing@test';";
            command.Parameters.AddWithValue(
                "@Gone", Path.Combine(Path.GetTempPath(), "parley-no-such-workspace-9e3a1"));
            command.ExecuteNonQuery();
        }

        await using var fleet = Fleet();
        var action = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(action.Started);
        Assert.Contains("workspace is gone", action.Skipped!, StringComparison.Ordinal);

        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT status FROM agents WHERE name = 'billing@test';";

        Assert.Equal("retired", query.ExecuteScalar());
    }

    [Fact]
    public async Task An_absent_mount_is_not_mistaken_for_a_deleted_repository()
    {
        // The hazard the parent check exists for. Directory.Exists is false for a repository
        // somebody deleted and for a drive that is briefly unmounted, and only the first is a
        // judgement worth making -- every agent on that volume would otherwise be retired in one
        // pass, each needing a person to reinstate it, because a share was down for a minute.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Owe("orders@test", "billing@test");

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "UPDATE agents SET workspace = @Gone WHERE name = 'billing@test';";
            command.Parameters.AddWithValue(
                "@Gone",
                Path.Combine(Path.GetTempPath(), "parley-absent-volume-4c1f", "billing@test"));
            command.ExecuteNonQuery();
        }

        await using var fleet = Fleet();
        var action = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(action.Started);
        Assert.Contains("mount rather than a deletion", action.Skipped!, StringComparison.Ordinal);

        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT status FROM agents WHERE name = 'billing@test';";

        Assert.Equal("active", query.ExecuteScalar());
    }

    [Fact]
    public async Task An_agent_with_nothing_waiting_is_still_left_alone()
    {
        // The widening must not become "start everything that consented". A turn given to an
        // agent with no reason to take one is tokens spent on nothing, which is the first of
        // the four conditions FindWaitingAsync exists to enforce.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));

        await using var fleet = Fleet();

        Assert.Empty(await fleet.TickAsync(Ct));
    }

    [Fact]
    public async Task An_agent_whose_runner_will_not_start_is_reported_and_the_pass_continues()
    {
        // One misconfigured agent must not end the pass that serves all of them, and it must not
        // be silent either -- the exchange simply stays owed and nobody learns why.
        Register("orders@test");
        Register("billing@test", "parley-no-such-runner --input-format stream-json");
        Register("shipping@test", StubRunner.Answering(Workspace("shipping@test")));

        Owe("orders@test", "billing@test");
        Owe("orders@test", "shipping@test");

        await using var fleet = Fleet();

        var actions = await fleet.TickAsync(Ct);

        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, a => a.Agent.Value == "billing@test" && a.Skipped is not null);
        Assert.Contains(actions, a => a.Agent.Value == "shipping@test" && a.Turn is not null);
    }

    [Fact]
    public async Task Stopping_the_fleet_stops_every_agent()
    {
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Register("shipping@test", StubRunner.Answering(Workspace("shipping@test")));

        Owe("orders@test", "billing@test");
        Owe("orders@test", "shipping@test");

        var fleet = Fleet();

        await fleet.TickAsync(Ct);

        Assert.Equal(2, fleet.Running.Count);

        var stopped = await fleet.StopAllAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(2, stopped);
        Assert.Empty(fleet.Running);

        await fleet.DisposeAsync();
    }
}
