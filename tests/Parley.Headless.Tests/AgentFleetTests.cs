using Microsoft.Data.Sqlite;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// Supervising a set of agents: who gets started, who does not, and who is told to quit.
/// </summary>
/// <remarks>
/// The behaviour worth pinning here is all refusal. Starting an agent that owes a Result is the
/// easy half and it is one assertion; the rest of this file is the cases where a fleet must
/// decline to start something, because each of those is somebody's tokens, somebody's machine, or
/// an operator's decision being quietly overruled.
/// </remarks>
public sealed class AgentFleetTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-fleet-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnection _connection;

    /// <summary>Counts the Requests this fixture has written, so no two are refused as the same one.</summary>
    private int _subjects;

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
    /// is one the fleet declines to start.
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
            var role = AgentName.Create(name).Role;
            var workspace = Workspace(name);
            var identity = AgentIdentity.Mint();

            // **Two files, because 018 split what they say.** The catalogue entry says how to start a
            // checkout and is keyed by an identity; what any project calls it is declared by the checkout
            // itself. A fixture that wrote only the first would describe a machine that can start something
            // nothing can name.
            File.WriteAllText(
                Path.Combine(workspace, LaunchConfiguration.FileName),
                $$"""
                {
                  "mcpServers": {
                    "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{role}}" } }
                  },
                  "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
                }
                """);

            _catalogue[role] = new Catalogued(identity, invokeCommand, workspace);
        }
    }

    /// <summary>One thing this machine has been told it can start.</summary>
    /// <param name="Identity">Which agent it is, which is what the catalogue is keyed by.</param>
    /// <param name="Command">How to start it.</param>
    /// <param name="Workspace">Where, and where its own declaration of what it is called lives.</param>
    private sealed record Catalogued(AgentIdentity Identity, string Command, string Workspace);

    /// <summary>What this machine has been told it can start, held by role so a test can delist one.</summary>
    private readonly Dictionary<string, Catalogued> _catalogue = new(StringComparer.Ordinal);

    /// <summary>Builds a catalogue file for the current fixture and reads it back.</summary>
    /// <returns>The catalogue the fleet consults.</returns>
    private NodeCatalogue Catalogue()
    {
        var agents = string.Join(",", _catalogue.Values.Select(entry =>
            $"\"{entry.Identity}\": {{ \"command\": {System.Text.Json.JsonSerializer.Serialize(entry.Command)},"
            + $" \"workspace\": {System.Text.Json.JsonSerializer.Serialize(entry.Workspace)} }}"));

        var path = Path.Combine(_root, "node.json");
        File.WriteAllText(path, $"{{ \"agents\": {{ {agents} }} }}");

        return NodeCatalogue.Read(Path.Combine(_root, "parley.db"));
    }

    /// <summary>Opens an exchange and writes its Request, the way the broker does, and marks it collected.</summary>
    /// <param name="requester">Who sends the Request.</param>
    /// <param name="receiver">Who owes its Result.</param>
    /// <returns>The open exchange.</returns>
    /// <remarks>
    /// Through the repositories rather than rows written by hand. Storage refuses a malformed Request (§9.3),
    /// so hand-written rows are a second copy of the schema that goes stale the first time it moves — which is
    /// exactly how every test in this file came to fail at once.
    /// </remarks>
    private async Task<Exchange> OpenCollectedAsync(string requester, string receiver)
    {
        var from = AgentName.Create(requester);
        var to = AgentName.Create(receiver);
        var now = DateTimeOffset.UtcNow;

        // Distinct per call, so a second Request between the same two agents is not refused as a duplicate.
        //
        // **A 0.7 Request is the ask and nothing else** (016). This wrote a 0.6 payload — `intent`, `items`
        // and `done_when` — which the schema refuses outright with "a request carries an ask", so every test
        // in this file that opened an exchange threw before it reached the fleet. Eighteen of them, since
        // 0.29.0, on a branch that was never pushed: `dotnet test` reports zero tests on this machine, so
        // nothing on it ran these, and CI never saw the branch.
        var payload =
            $$"""{"ask":"is the invoice total stated gross or net? (subject {{++_subjects}})"}""";

        var opened = await new ExchangeRepository(_connection).TryOpenAsync(
            from, to, now, MessageIdentity.ComputeRequest(from, to, payload), "fleet", null, cancellationToken: Ct);

        Assert.True(opened.Succeeded, $"the fixture's Request was refused: {opened.Rejection}");

        var messages = new MessageRepository(_connection);
        var written = await messages.AppendAsync(
            opened.Exchange!.Id, MessageType.Request, from, to, "some-model-9", payload, now, cancellationToken: Ct);

        Assert.True(await messages.MarkDeliveredAsync(written.Message.Id, now, Ct));

        return opened.Exchange;
    }

    /// <summary>Leaves one agent owing a Result on work it has already collected.</summary>
    /// <param name="requester">Who asked.</param>
    /// <param name="receiver">Who owes.</param>
    /// <returns>A task that completes when it is written.</returns>
    /// <remarks>One item waiting: the open exchange. The Request itself was collected.</remarks>
    private Task OweAsync(string requester, string receiver) => OpenCollectedAsync(requester, receiver);

    /// <summary>Writes a Result nobody has collected, on an exchange it closed, leaving no work owed behind it.</summary>
    /// <param name="from">Who sent the Result.</param>
    /// <param name="to">The requester, who has not collected it.</param>
    /// <returns>A task that completes when it is written.</returns>
    private async Task SendUncollectedResultAsync(string from, string to)
    {
        var exchange = await OpenCollectedAsync(to, from);
        var now = DateTimeOffset.UtcNow;

        _ = await new MessageRepository(_connection).AppendAsync(
            exchange.Id, MessageType.Result, exchange.Receiver, exchange.Requester, "some-model-9",
            """{"outcomes":[{"id":"request","outcome":"answered","text":"net of tax"}]}""", now, cancellationToken: Ct);

        Assert.True(exchange.Close(exchange.Receiver, now, ExchangeOutcome.Complete).Succeeded);
        Assert.True(await new ExchangeRepository(_connection).SaveAsync(exchange, cancellationToken: Ct));
    }

    /// <summary>The fleet under test, consulting this machine's catalogue.</summary>
    /// <returns>A fleet that can start whatever Register was given a command for.</returns>
    private AgentFleet Fleet() => new(_connection, TimeProvider.System, Catalogue());

    [Fact]
    public async Task An_agent_that_owes_a_result_is_started_and_given_a_turn()
    {
        // The feature in one assertion: nobody took a turn, and an agent ran anyway.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        await OweAsync("orders@test", "billing@test");

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
        await OweAsync("orders@test", "billing@test");

        await using var fleet = Fleet();

        var first = await fleet.TickAsync(Ct);
        var session = fleet.SessionOf(AgentName.Create("billing@test"));

        await OweAsync("orders@test", "billing@test");

        var second = await fleet.TickAsync(Ct);

        Assert.True(Assert.Single(first).Started);
        Assert.False(Assert.Single(second).Started, "the fleet started a second process");
        Assert.Equal(session, fleet.SessionOf(AgentName.Create("billing@test")));
    }

    [Fact]
    public async Task An_agent_this_machine_cannot_start_is_reported_not_started()
    {
        // Starting a repository nobody offered is spending somebody's tokens on their machine.
        Register("orders@test");
        Register("billing@test");
        await OweAsync("orders@test", "billing@test");

        await using var fleet = Fleet();

        // Reported rather than silent. An agent this machine has no command for is a configuration
        // gap somebody has to see, and silence looks identical to a healthy quiet pass.
        var skipped = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(skipped.Started);
        Assert.Contains("no launch command", skipped.Skipped, StringComparison.Ordinal);
        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task An_agent_somebody_is_already_running_is_left_alone()
    {
        // Two processes behind one identity would drain one inbox, and the exchange's turn check
        // refuses the loser -- after both have spent tokens getting there. A connected adapter is
        // the only evidence available that a person has that repository open.
        Register("orders@test");
        Register(
            "billing@test",
            StubRunner.Answering(Workspace("billing@test")),
            connected: true);

        await OweAsync("orders@test", "billing@test");

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

        await OweAsync("orders@test", "billing@test");

        await using var fleet = Fleet();

        Assert.True(Assert.Single(await fleet.TickAsync(Ct)).Started);
    }

    [Fact]
    public async Task A_retired_agent_is_never_started()
    {
        // Retirement is a person's judgement that the agent will not come back. §14 refuses to
        // route to one; starting a process against it would overrule the same decision. Retired after
        // the work was sent, which is the order a real deployment meets it in.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));

        await OweAsync("orders@test", "billing@test");
        Retire("billing@test");

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
        await OweAsync("orders@test", "billing@test");

        await using var fleet = Fleet();

        await fleet.TickAsync(Ct);

        Assert.Contains("billing@test", fleet.Running, StringComparer.Ordinal);

        Retire("billing@test");

        var actions = await fleet.TickAsync(Ct);

        Assert.Contains(
            actions, a => a.Skipped is { } why && why.Contains("withdrawn", StringComparison.Ordinal));

        Assert.Empty(fleet.Running);
    }

    [Fact]
    public async Task Delisting_a_running_agent_tells_it_to_quit()
    {
        // Withdrawal is removing the agent from this machine's own catalogue: the party that starts
        // agents is the party that stops being told to.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        await OweAsync("orders@test", "billing@test");

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
    public async Task An_agent_on_another_machine_is_never_started_from_here()
    {
        // **The check moved here when the catalogue stopped knowing about machines.** It used to be part
        // of the key: `billing` in that file meant billing@thisMachine and nothing else. An identity says
        // nothing about machines and a store holds agents on several, so a local checkout claiming
        // `billing` would otherwise be started for billing@server-b — a process launched here under a name
        // somebody else holds.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        Register("billing@server-b");

        await OweAsync("orders@test", "billing@server-b");

        await using var fleet = Fleet();

        var action = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(action.Started);
        Assert.Contains("another machine", action.Skipped!, StringComparison.Ordinal);
        Assert.Empty(fleet.Running);
    }

    [Fact]
    public void A_machine_told_about_nothing_starts_nothing_rather_than_failing()
    {
        // The ordinary state of a machine somebody has only registered agents on. It must read
        // as "starts nothing" rather than as an error, or a supervisor could not run anywhere it
        // had not been configured first.
        Assert.False(NodeCatalogue.Empty.TryResolve("billing", project: null, out _, out _));
        Assert.Empty(NodeCatalogue.Empty.Identities);
    }

    [Fact]
    public async Task A_turn_is_recorded_with_what_it_cost()
    {
        // §17 question 1 settled a comparable question by measuring rather than guessing, and a
        // resident fleet costs real turns.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        await OweAsync("orders@test", "billing@test");

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

    [Fact]
    public async Task An_uncollected_result_starts_the_agent_that_requested_it()
    {
        // A Result closes the exchange it answers, so an agent waiting only for one owes nothing — and
        // the fleet would do the work and never start the agent that asked for it to read the Result.
        // The one participant with the strongest reason to be woken would be the only one excluded.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test");
        await SendUncollectedResultAsync("billing@test", "orders@test");

        await using var fleet = Fleet();
        var actions = await fleet.TickAsync(Ct);

        var started = Assert.Single(actions);

        Assert.Equal("orders@test", started.Agent.Value);
        Assert.True(started.Started, started.Skipped ?? "not started");
        Assert.Equal(1, started.WaitingBefore);
    }

    [Fact]
    public async Task Work_owed_and_an_uncollected_message_are_counted_once_each()
    {
        // Counted as distinct items rather than summed from two overlapping queries: an open exchange
        // and a message that has not been collected are two things to deal with, and an agent holding
        // both should sort above one holding either.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        await OweAsync("orders@test", "billing@test");
        await SendUncollectedResultAsync("orders@test", "billing@test");

        await using var fleet = Fleet();
        var actions = await fleet.TickAsync(Ct);

        Assert.Equal(2, Assert.Single(actions).WaitingBefore);
    }

    [Fact]
    public async Task A_turn_that_collected_nothing_is_not_repeated_until_something_arrives()
    {
        // The runaway this guards. Nothing times out (§5.3), and an uncollected message is held against
        // retention by §9.4's floor precisely so nothing is lost -- so without a guard, one unread Result
        // starts a real session every pass, for as long as the fleet runs, on somebody's account. The stub
        // takes its turn and touches no database, which is exactly the shape of an agent that is started
        // and collects nothing.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test");
        await SendUncollectedResultAsync("billing@test", "orders@test");

        await using var fleet = Fleet();

        Assert.True(Assert.Single(await fleet.TickAsync(Ct)).Started);

        var second = Assert.Single(await fleet.TickAsync(Ct));

        Assert.False(second.Started);
        Assert.Contains("collected nothing", second.Skipped!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_message_re_arms_an_agent_that_was_stuck()
    {
        // The bound is one wasted turn per new message, not one wasted turn ever. A second message raises
        // the count above what was still waiting after the last turn, which is the evidence that there is
        // something the previous turn cannot have seen.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test");
        await SendUncollectedResultAsync("billing@test", "orders@test");

        await using var fleet = Fleet();
        await fleet.TickAsync(Ct);
        Assert.False(Assert.Single(await fleet.TickAsync(Ct)).Started);

        await SendUncollectedResultAsync("billing@test", "orders@test");

        var third = Assert.Single(await fleet.TickAsync(Ct));

        Assert.Null(third.Skipped);
        Assert.Equal(2, third.WaitingBefore);
    }

    [Fact]
    public async Task An_agent_whose_workspace_is_gone_is_retired()
    {
        // A missing directory is an observation the broker can make by looking, rather than an inference
        // from silence, so it is allowed to conclude from it. The cost is recorded where the code is: a
        // repository that comes back needs `parley reinstate`.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        await OweAsync("orders@test", "billing@test");

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

        Assert.Equal("retired", StatusOf("billing@test"));
    }

    [Fact]
    public async Task An_absent_mount_is_not_mistaken_for_a_deleted_repository()
    {
        // The hazard the parent check exists for. Directory.Exists is false for a repository somebody
        // deleted and for a drive that is briefly unmounted, and only the first is a judgement worth
        // making -- every agent on that volume would otherwise be retired in one pass, each needing a
        // person to reinstate it, because a share was down for a minute.
        Register("orders@test");
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));
        await OweAsync("orders@test", "billing@test");

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

        Assert.Equal("active", StatusOf("billing@test"));
    }

    [Fact]
    public async Task An_agent_with_nothing_waiting_is_still_left_alone()
    {
        // A turn given to an agent with no reason to take one is tokens spent on nothing.
        Register("orders@test", StubRunner.Answering(Workspace("orders@test")));
        Register("billing@test", StubRunner.Answering(Workspace("billing@test")));

        await using var fleet = Fleet();

        Assert.Empty(await fleet.TickAsync(Ct));
    }

    [Fact]
    public async Task An_agent_whose_runner_will_not_start_is_reported_and_the_pass_continues()
    {
        // One misconfigured agent must not end the pass that serves all of them, and it must not be
        // silent either -- the exchange simply stays owed and nobody learns why.
        Register("orders@test");
        Register("billing@test", "parley-no-such-runner --input-format stream-json");
        Register("shipping@test", StubRunner.Answering(Workspace("shipping@test")));

        await OweAsync("orders@test", "billing@test");
        await OweAsync("orders@test", "shipping@test");

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

        await OweAsync("orders@test", "billing@test");
        await OweAsync("orders@test", "shipping@test");

        var fleet = Fleet();

        await fleet.TickAsync(Ct);

        Assert.Equal(2, fleet.Running.Count);

        var stopped = await fleet.StopAllAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(2, stopped);
        Assert.Empty(fleet.Running);

        await fleet.DisposeAsync();
    }

    private void Retire(string agent)
    {
        using var retire = _connection.CreateCommand();
        retire.CommandText = "UPDATE agents SET status = 'retired' WHERE name = @Name;";
        retire.Parameters.AddWithValue("@Name", agent);
        retire.ExecuteNonQuery();
    }

    private object? StatusOf(string agent)
    {
        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT status FROM agents WHERE name = @Name;";
        query.Parameters.AddWithValue("@Name", agent);

        return query.ExecuteScalar();
    }
}
