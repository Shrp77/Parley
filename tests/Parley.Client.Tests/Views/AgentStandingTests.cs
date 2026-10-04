using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That an agent's row says which of two things is true, in words nobody needs the spec for.
/// </summary>
/// <remarks>
/// <para>
/// The wording it replaces was <c>catalogued, not yet registered</c> and <c>not in this
/// catalogue</c>. The owner read both on screen and could not say what either meant, which is a
/// fair verdict: they name two files a person did not write, rather than the facts those files
/// decide.
/// </para>
/// <para>
/// So the assertions are about the facts, not about particular words: every combination is covered,
/// each says whether the agent has ever run and whether anything here can start it, and none of
/// them uses the jargon. A future rewording that keeps those properties should keep these passing.
/// </para>
/// </remarks>
public sealed class AgentStandingTests
{
    /// <summary>The machine these fixtures put their agents on.</summary>
    private const string Machine = "test";

    // **These fed `catalogued` full `role@machine` names, which no caller produces.** A node
    // catalogue is keyed by role -- `NodeCatalogue.WorkspaceFor` looks up `agent.Role` and
    // `AgentsRegion` passes `catalogue.Roles` -- so every fixture here described a shape that cannot
    // occur, and the two comparisons under test happened to agree with it. That is why
    // `StartableHere` being false for every registered agent, and every catalogued role being listed
    // twice, survived a suite that covers this method closely. Corrected 2026-09-10, from a report of
    // the symptom: the roster printed a bare role the composer then refused as not a name.

    private const string SeatAdapter = "parley-console";

    private static AgentRecord Registered(
        string name,
        AgentPresence presence = AgentPresence.Startable,
        AgentStatus status = AgentStatus.Active,
        string adapter = "test") =>
        new(
            AgentName.Create(name),
            adapter,
            AgentTier.Full,
            "/src/thing",
            null,
            null,
            presence == AgentPresence.Running,
            status,
            "the thing",
            presence);

    [Fact]
    public void An_agent_that_has_never_run_but_can_be_started_says_both()
    {
        var rows = Standing.Join([], new HashSet<string>(["orders"]), SeatAdapter, Machine);

        var row = Assert.Single(rows);

        Assert.False(row.EverRun);
        Assert.True(row.StartableHere);
        Assert.Equal(Standing.NeverRun, row.Standing);
    }

    [Fact]
    public void An_agent_the_registry_knows_and_the_catalogue_does_not_says_nothing_here_starts_it()
    {
        // The case `not in this catalogue` was trying to name. It is not the same as "not
        // startable" in the registry's sense: presence may say a node is connected, and the node
        // still will not start a role its catalogue does not name.
        var rows = Standing.Join(
            [Registered("orders@test", AgentPresence.Startable)],
            new HashSet<string>(),
            SeatAdapter, Machine);

        var row = Assert.Single(rows);

        Assert.True(row.EverRun);
        Assert.False(row.StartableHere);
        Assert.Equal(Standing.OnlyWhenOpened, row.Standing);
    }

    [Fact]
    public void An_agent_that_is_attached_says_running_whatever_the_catalogue_says()
    {
        foreach (var catalogue in new[] { new HashSet<string>(), new HashSet<string>(["orders"]) })
        {
            var rows = Standing.Join(
                [Registered("orders@test", AgentPresence.Running)], catalogue, SeatAdapter, Machine);

            Assert.Equal(Standing.Running, Assert.Single(rows).Standing);
        }
    }

    [Fact]
    public void An_agent_whose_machine_is_gone_says_so_rather_than_offering_a_start()
    {
        var rows = Standing.Join(
            [Registered("orders@test", AgentPresence.Stranded)],
            new HashSet<string>(["orders"]),
            SeatAdapter, Machine);

        Assert.Equal(Standing.NoMachine, Assert.Single(rows).Standing);
    }

    [Fact]
    public void A_retired_agent_says_only_that()
    {
        // Retirement is a decision rather than an observation and outranks every other fact:
        // nothing is routed to it whether or not a node could start it.
        var rows = Standing.Join(
            [Registered("orders@test", AgentPresence.Startable, AgentStatus.Retired)],
            new HashSet<string>(["orders"]),
            SeatAdapter, Machine);

        var row = Assert.Single(rows);

        Assert.True(row.Retired);
        Assert.Equal(Standing.Retired, row.Standing);
    }

    [Theory]
    [InlineData(AgentPresence.Here, Standing.Here)]
    [InlineData(AgentPresence.Away, Standing.Away)]
    public void The_person_is_marked_as_a_seat_and_reads_here_or_away(AgentPresence presence, string reads)
    {
        // A seat is never offered for retiring or purging, and since 023 it says the one thing a peer asking the person
        // needs (SPEC.md §14): whether anybody is at a screen. Never an agent's state.
        var rows = Standing.Join(
            [Registered("human-user@test", presence, adapter: SeatAdapter)],
            new HashSet<string>(),
            SeatAdapter, Machine);

        var row = Assert.Single(rows);

        Assert.True(row.Seat);
        Assert.Equal(reads, row.Standing);
    }

    [Fact]
    public void Neither_phrase_the_owner_could_not_read_appears_anywhere()
    {
        // The whole point of the change, asserted against every phrase this can produce rather
        // than against the ones a particular fixture happens to reach.
        string[] phrases =
        [
            Standing.Here, Standing.Away, Standing.Retired, Standing.Running, Standing.Idle,
            Standing.OnlyWhenOpened, Standing.NoMachine, Standing.NeverRun, Standing.NeverRunStuck,
        ];

        foreach (var phrase in phrases)
        {
            Assert.DoesNotContain("catalogue", phrase, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("registered", phrase, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("registry", phrase, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_phrase_says_whether_it_is_running()
    {
        // A person scanning the column should not have to infer the most basic fact from the
        // absence of a word.
        string[] agents =
        [
            Standing.Running, Standing.Idle, Standing.OnlyWhenOpened,
            Standing.NoMachine, Standing.NeverRun, Standing.NeverRunStuck,
        ];

        foreach (var phrase in agents)
        {
            Assert.True(
                phrase.Contains("running", StringComparison.Ordinal)
                || phrase.Contains("never run", StringComparison.Ordinal),
                $"'{phrase}' does not say whether the agent is running.");
        }
    }

    [Fact]
    public void A_name_in_both_the_registry_and_the_catalogue_produces_one_row()
    {
        var rows = Standing.Join(
            [Registered("orders@test")], new HashSet<string>(["orders"]), SeatAdapter, Machine);

        Assert.Single(rows);
        Assert.Equal(Standing.Idle, rows[0].Standing);
    }

    [Fact]
    public void Rows_are_ordered_by_name_whichever_source_they_came_from()
    {
        var rows = Standing.Join(
            [Registered("orders@test"), Registered("alpha@test")],
            new HashSet<string>(["billing"]),
            SeatAdapter, Machine);

        // **Two spellings in one list, and this records it rather than hiding it.** A registration
        // is `role@machine` and a catalogue key is a role, so rows from the two sources are spelled
        // differently — which is what the owner met when the composer refused the bare name this
        // list had just printed. The composer accepts either now; settling the display on one
        // spelling is a separate change, because a catalogue key may not be a role at all and a row
        // has to show it as it stands.
        Assert.Equal(["alpha@test", "billing", "orders@test"], rows.Select(r => r.Name));
    }

    [Fact]
    public void A_role_on_two_machines_keeps_both_rows_apart()
    {
        // Names collide freely across projects and may collide across machines within one. Whatever
        // a row is spelled, two agents must not become one.
        var rows = Standing.Join(
            [Registered("orders@test"), Registered("orders@elsewhere"), Registered("billing@test")],
            new HashSet<string>(),
            SeatAdapter,
            Machine);

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(row => row.Name.StartsWith("orders", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_agent_registered_elsewhere_is_not_startable_here()
    {
        // The comparison this whole change is about, in one line: a role in *this* machine's
        // catalogue does not make a namesake on another box startable here.
        var rows = Standing.Join(
            [Registered("orders@elsewhere")],
            new HashSet<string>(["orders"]),
            SeatAdapter,
            Machine);

        var elsewhere = Assert.Single(rows, row => row.EverRun);

        Assert.False(elsewhere.StartableHere);
    }

    [Fact]
    public void An_unreadable_registry_still_shows_what_the_catalogue_names()
    {
        // A broker that did not answer must not make a project look like it has no agents: the
        // catalogue is a local file and is still true.
        var rows = Standing.Join(null, new HashSet<string>(["orders"]), SeatAdapter, Machine);

        Assert.Equal(Standing.NeverRun, Assert.Single(rows).Standing);
    }
}
