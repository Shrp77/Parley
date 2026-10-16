using Grpc.Net.Client;
using Parley.Core;
using Parley.Client.Rendering;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What the agents page claims about an agent, and that two projects never become one.
/// </summary>
/// <remarks>
/// <para>
/// Three claims, each of which is wrong in a way that would look right on the page. A retired
/// agent that still holds a connection shown as running; an agent that never said what it owns
/// shown as owning something; and two deployments that both have an <c>orders</c> merged into one
/// roster, which is the failure that makes every other number on the page a lie.
/// </para>
/// <para>
/// SPEC.md §4.1 gives the protocol no concept of a project at all, so nothing on the wire can
/// keep two apart. The separation is entirely this machine's manifest choosing an endpoint, which
/// is why the last of these is a test about the directory rather than about a page.
/// </para>
/// </remarks>
public sealed class RosterTests
{
    private static AgentRecord Agent(
        string name,
        AgentStatus status = AgentStatus.Active,
        AgentPresence presence = AgentPresence.Running,
        string? owns = "the orders ledger") =>
        new(AgentName.Create(name), "claude-code", AgentTier.Full, null, null, null,
            presence == AgentPresence.Running, status, owns, presence);

    [Fact]
    public void A_retired_agent_that_is_still_connected_is_shown_as_retired()
    {
        // The case that makes this a judgement rather than a formatting choice. Retiring does not
        // disconnect anything -- §10.2's retire stops routing and listing -- so an agent can be
        // retired and running at the same instant, and only one of those is what the operator
        // needs to see.
        var agent = Agent("orders@alpha", AgentStatus.Retired, AgentPresence.Running);

        Assert.Equal("retired", Roster.State(agent));
    }

    [Fact]
    public void An_active_agent_is_shown_by_its_presence()
    {
        Assert.Equal("running", Roster.State(Agent("orders@alpha")));
        Assert.Equal(
            "stranded",
            Roster.State(Agent("orders@alpha", presence: AgentPresence.Stranded)));
        Assert.Equal(
            "startable",
            Roster.State(Agent("orders@alpha", presence: AgentPresence.Startable)));
    }

    [Fact]
    public void An_agent_that_never_said_what_it_owns_is_not_given_a_claim()
    {
        // Never substituted, from the workspace, from the name, from anything. The owns line is
        // the agent's own words about its scope, and inventing one puts words in its mouth in the
        // one column a reader would take as the agent's.
        Assert.False(Roster.HasStatedWhatItOwns(Agent("orders@alpha", owns: null)));
    }

    [Fact]
    public void A_blank_owns_line_counts_as_never_having_said_one()
    {
        // An agent that registered with whitespace has said nothing. Rendering the blank would
        // read as a column that failed rather than as a claim never made.
        Assert.False(Roster.HasStatedWhatItOwns(Agent("orders@alpha", owns: "   ")));
        Assert.True(Roster.HasStatedWhatItOwns(Agent("orders@alpha")));
    }

    [Fact]
    public void Two_projects_with_an_agent_of_the_same_name_are_reached_through_different_brokers()
    {
        // The failure this prevents is silent: two deployments each with an orders, merged, and
        // every conversation attributed to whichever the console happened to reach. Nothing on
        // the wire keeps them apart -- §4.1 gives the protocol no concept of a project -- so the
        // separation is the manifest choosing an endpoint, and it has to hold here.
        var asked = new List<string>();

        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(
                    ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:5301", null),
                new ProjectSettings(
                    ProjectName.Create("beta"), "b.db", "http://127.0.0.1:5302", null)
            ],
            endpoint =>
            {
                asked.Add(endpoint);
                return GrpcChannel.ForAddress(endpoint);
            });

        var alpha = directory.Find("alpha");
        var beta = directory.Find("beta");

        Assert.NotNull(alpha);
        Assert.NotNull(beta);

        directory.ClientFor(alpha!);
        directory.ClientFor(beta!);

        Assert.Equal(["http://127.0.0.1:5301", "http://127.0.0.1:5302"], asked);
    }

    [Fact]
    public void A_project_this_machine_does_not_have_resolves_to_nothing()
    {
        // Rather than to the first project, or to a default. A name the manifest does not carry
        // is the wrong address, and answering it with somebody else's roster is the merge above
        // arriving by a different route.
        using var directory = new BrokerDirectory(
            [new ProjectSettings(
                ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:5301", null)],
            _ => throw new InvalidOperationException("must not connect"));

        Assert.Null(directory.Find("beta"));
        Assert.Null(directory.Find("Alpha"));
        Assert.Null(directory.Find(null));
    }

    [Fact]
    public void A_line_a_person_accepted_is_marked_until_peers_read_it()
    {
        // **In the one place both surfaces read it from.** A person who accepts a line and then
        // sees it in a roster will read the roster as the deployment -- so what is shown stays the
        // *live* line and this says a newer one is waiting.
        //
        // Must fail against showing the accepted line plainly, which would tell somebody peers route
        // by a sentence they have never read.
        var pending = Agent("orders@alpha", owns: "the orders ledger") with
        {
            OwnsAccepted = "Orders and invoicing. Not stock.",
        };

        Assert.NotEmpty(Roster.PendingMark(pending));
        Assert.Contains("peers do not read it yet", Roster.PendingMark(pending), StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_whose_line_peers_already_read_carries_no_mark()
    {
        // The mark has to go away on its own when the agent comes back carrying the line, because
        // nothing clears it: it is a comparison. A mark that stayed would be a surface insisting a
        // line is pending after peers had begun reading it.
        var settled = Agent("orders@alpha", owns: "Orders and invoicing.") with
        {
            OwnsAccepted = "Orders and invoicing.",
        };

        Assert.Empty(Roster.PendingMark(settled));
        Assert.Empty(Roster.PendingMark(Agent("orders@alpha")));
    }

    [Fact]
    public void The_mark_reads_correctly_beside_an_agent_that_has_stated_nothing()
    {
        // The commonest way this state is reached -- an agent enrolled without a line, asked, and
        // answered. A first draft said "a newer line is accepted", which is nonsense here: there is no
        // older line for it to be newer than.
        var pending = Agent("orders@alpha", owns: null) with
        {
            OwnsAccepted = "Orders and invoicing.",
        };

        Assert.DoesNotContain("newer", Roster.PendingMark(pending), StringComparison.Ordinal);
        Assert.NotEmpty(Roster.PendingMark(pending));
    }
}
