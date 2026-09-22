using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The order enrolment does its three things in, and what a failure at each point leaves.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.3. Adding an agent was two file writes; 006 made it three
/// steps, the third of which starts a process. Three steps have three places to fail and an order
/// that matters, and neither was true of two.
/// </para>
/// <para>
/// The order is the argument: the files first, the start last. A start that came first would ask a
/// broker to launch a role its catalogue does not yet name, which fails — and would fail in a way
/// that looks like the broker refusing rather than like the surface asking too early.
/// </para>
/// </remarks>
public sealed class EnrolmentOrderTests
{
    [Fact]
    public void The_description_names_all_three_things_before_any_of_them_happens()
    {
        // A process appearing on somebody's machine is not something a surface may do without its
        // description covering it. Two paths and a sentence, and the sentence is the new one.
        var described = Adding.Describe("C:/repo", "C:/store/node.json", "alpha");

        Assert.Contains(".mcp.json", described, StringComparison.Ordinal);
        Assert.Contains("node.json", described, StringComparison.Ordinal);
        Assert.Contains(Adding.Starts, described, StringComparison.Ordinal);
    }

    [Fact]
    public void The_start_is_described_as_running_something_rather_than_as_a_formality()
    {
        // Wording, and it is load-bearing: "started once" alone reads as bookkeeping. What a
        // person needs to know is that their machine will run the repository's own server.
        Assert.Contains("runs", Adding.Starts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("this machine", Adding.Starts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void What_is_said_when_the_start_does_not_happen_names_only_ways_in_that_work()
    {
        // The honest fallback rather than a failure. The agent is catalogued either way, so the
        // sentence has to leave somebody able to act rather than merely informed that something
        // did not work.
        Assert.Contains("opening its", Adding.NotStarted, StringComparison.OrdinalIgnoreCase);

        // **This test used to require the opposite, and that is the finding.** The sentence said the
        // next question would start it, and a question cannot: §14 counts a name nothing has
        // registered as unroutable, and both `Ask` and `Notify` refuse an unroutable recipient
        // before any wake — pinned by `NeverRegisteredTests`. So the check that was meant to keep
        // the message useful was holding a remedy that does not work in place.
        Assert.DoesNotContain("question", Adding.NotStarted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_standing_a_newly_added_agent_gets_does_not_depend_on_the_start_succeeding()
    {
        // The property 005 could not promise and 006 does. It follows from the catalogue naming the
        // role, not from the start having worked -- which is why a broker that did not answer still
        // leaves a startable agent, and why this is asserted against the derivation rather than
        // against a screen.
        var startable = Standing.Join(
            registry: [],
            catalogued: new HashSet<string>(["orders"]),
            seatAdapter: "parley-console",
            machine: Parley.Core.MachineName.Current);

        var row = Assert.Single(startable);

        Assert.True(row.StartableHere);
        Assert.False(row.EverRun);
        Assert.Equal(Standing.NeverRun, row.Standing);
    }

    [Fact]
    public void The_catalogue_alone_is_what_makes_a_role_startable()
    {
        // Stated as its own assertion because it is the whole of plan.md Q1 in one line, and
        // because the opposite -- requiring a registration -- is the reading 005 acted on.
        var withRegistration = Standing.Join(
            registry: [],
            catalogued: new HashSet<string>(["orders"]),
            seatAdapter: "parley-console",
            machine: Parley.Core.MachineName.Current);

        var withoutCatalogue = Standing.Join(
            registry: [],
            catalogued: new HashSet<string>(),
            seatAdapter: "parley-console",
            machine: Parley.Core.MachineName.Current);

        Assert.True(Assert.Single(withRegistration).StartableHere);
        Assert.Empty(withoutCatalogue);
    }
}
