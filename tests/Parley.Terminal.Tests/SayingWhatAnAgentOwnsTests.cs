using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Recording what an agent owns, from the agents pane.
/// </summary>
/// <remarks>
/// <para>
/// Two properties, and they fail against different mistakes. The first is that the action <b>exists
/// on the screen at all</b> — this surface's rule is that no action exists only as a keystroke, and
/// CLAUDE.md records four separate occasions when a property the suite held to was never shown.
/// </para>
/// <para>
/// The second is the <b>wording</b>, which is load-bearing here rather than cosmetic. The length
/// bound came off at 0.33.0, so the only thing left telling anybody to be brief is what this screen
/// says — and a person who is not told that an accepted line is not yet what peers read will believe
/// the deployment changed when it did not.
/// </para>
/// </remarks>
[Integration]
public sealed class SayingWhatAnAgentOwnsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static AgentRecord Agent(string name) =>
        new(
            AgentName.Create(name),
            "test",
            AgentTier.Full,
            "/src/thing",
            null,
            null,
            false,
            AgentStatus.Active,
            "the thing",
            AgentPresence.Startable);

    [Fact]
    public async Task The_agents_pane_offers_it_for_the_agent_under_the_cursor()
    {
        // Must fail against the action being reachable only by pressing a key nothing advertises,
        // which is how `Escape` was the only way out of a project view for a whole release.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        shell.Focus(2);

        Assert.Contains(shell.Actions(), a => a.Label == Responsibility.Title);
    }

    [Fact]
    public void What_the_person_reads_first_says_what_the_line_costs()
    {
        // The bound is gone, so this text is the whole of what argues for brevity. Must fail against
        // a screen that asks for a line and says nothing about who reads it or how often.
        var described = Ownership.Described(Orders);

        Assert.Contains(Ownership.TypeIt, described, StringComparison.Ordinal);
        Assert.Contains("roster", described, StringComparison.Ordinal);
        Assert.Contains("read far", described, StringComparison.Ordinal);
        Assert.Contains("what it does not", described, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_person_reads_first_says_it_records_their_own_words()
    {
        // This screen has no ask, so what it writes is `composed` and it must not look like an
        // agent's proposal. A reader has to be able to tell the two apart.
        Assert.Contains("your words as yours", Ownership.Described(Orders), StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_person_reads_first_says_when_it_takes_effect()
    {
        Assert.Contains("next starts", Ownership.Described(Orders), StringComparison.Ordinal);
    }

    [Fact]
    public void A_recorded_line_says_peers_have_not_read_it_yet()
    {
        // FR-020. Must fail against reporting only that it was recorded, which is true about the
        // record and false about the deployment.
        var said = Ownership.Said(
            new OwnershipRecord(Recorded: true, Problem: null, "Invoicing only.") { Pending = true });

        Assert.Contains("Invoicing only.", said, StringComparison.Ordinal);
        Assert.Contains("until it next starts", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recorded_line_that_peers_already_read_does_not_claim_to_be_pending()
    {
        var said = Ownership.Said(
            new OwnershipRecord(Recorded: true, Problem: null, "Invoicing only."));

        Assert.DoesNotContain("until it next starts", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_with_no_line_yet_is_said_in_words_rather_than_as_an_empty_quote()
    {
        // A blank pair of quotes reads as a bug. 0.25.1 records a dangling colon from an empty
        // refusal reason for the same reason.
        var said = Ownership.Said(
            new OwnershipRecord(Recorded: true, Problem: null, LiveStatement: null) { Pending = true });

        Assert.Contains("no line for this agent", said, StringComparison.Ordinal);
        Assert.DoesNotContain("''", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_is_repeated_rather_than_replaced_with_a_shrug()
    {
        var said = Ownership.Said(
            new OwnershipRecord(Recorded: false, "it is not a statement of ownership.", null));

        Assert.Contains("it is not a statement of ownership.", said, StringComparison.Ordinal);
    }
}
