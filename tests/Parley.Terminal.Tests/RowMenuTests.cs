using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That Enter on an agents-list row opens what can be done with that row, and nothing that withdraws anybody (specs/023
/// User Story 6).
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 0.36.0</b>: "it felt more natural to be in the agents section and hit enter on my user in
/// order to start a conversation." Enter on a seat row used to take that seat, which went with the typed name.
/// </para>
/// <para>
/// <b>Every test presses the key.</b> <c>Lists</c> records the rule and 0.30.4 what ignoring it cost: a key tested only
/// through its seam is a key nobody has pressed. Each was watched to fail against 0.36.0's Enter, which did nothing on
/// these rows once the seat code went.
/// </para>
/// </remarks>
public sealed class RowMenuTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enter_on_the_persons_own_row_offers_what_a_person_does()
    {
        await using var seated = await OnAgentsAsync();

        var offered = Open(seated, seated.Shell.Held!.Seat.Name.Value);

        Assert.Equal(
            [Participation.StartTitle, Shell.YourConversationsTitle],
            offered);
    }

    [Fact]
    public async Task Enter_on_an_agents_row_offers_its_everyday_actions_and_never_a_withdrawal()
    {
        await using var seated = await OnAgentsAsync();

        var orders = Seated.Orders.Value;
        var offered = Open(seated, orders);

        Assert.Contains(Shell.StartWithTitle + orders, offered);
        Assert.Contains(Shell.ConversationsOfTitle(orders), offered);
        Assert.Contains(Responsibility.Title, offered);
        Assert.DoesNotContain(offered, label => label.Contains("nstruct", StringComparison.Ordinal));
        Assert.DoesNotContain(offered, label => label.StartsWith(Ownership.AskTitle, StringComparison.Ordinal));
        Assert.DoesNotContain(offered, label => label.StartsWith(Ownership.Title, StringComparison.Ordinal));

        // FR-019, the owner's call: retire, reinstate and purge stay behind the typed name, never one arrow away from
        // the most ordinary key there is.
        Assert.DoesNotContain(Withdrawing.Title, offered);
        Assert.DoesNotContain(Withdrawing.ReinstateTitle, offered);
        Assert.DoesNotContain(offered, label => label.Contains("etire", StringComparison.Ordinal));
        Assert.DoesNotContain(offered, label => label.Contains("urge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choosing_an_agents_conversations_filters_the_list_says_so_and_Esc_clears_it()
    {
        await using var seated = await OnAgentsAsync();

        // `billing` is in no conversation in the fixture and `orders` is in `room`, so the filter's two answers differ.
        Choose(seated, Seated.Billing.Value, Shell.ConversationsOfTitle(Seated.Billing.Value));
        await seated.Shell.Settled;

        Assert.Equal(Seated.Billing, seated.Shell.ConversationsOf);
        Assert.DoesNotContain(seated.Shell.Conversations.Conversations, row => row.Id == Seated.Conversation);
        Assert.Contains(
            Drawn.Rows(seated.Shell.Conversations.List),
            row => string.Equals(row.Trim(), ConversationsRegion.FilteredTo(Seated.Billing).Trim(), StringComparison.Ordinal));

        seated.Session.Escape();
        await seated.Shell.Settled;

        Assert.Null(seated.Shell.ConversationsOf);
        Assert.Contains(seated.Shell.Conversations.Conversations, row => row.Id == Seated.Conversation);

        // And Esc cleared the filter rather than leaving the project: the list is still this project's.
        Assert.Equal(Viewing.Project, seated.Shell.Place.Viewing);
    }

    [Fact]
    public async Task Starting_a_conversation_from_an_agents_row_does_not_ask_which_agent_again()
    {
        await using var seated = await OnAgentsAsync();

        Choose(seated, Seated.Orders.Value, Shell.StartWithTitle + Seated.Orders.Value);
        await seated.Shell.Settled;

        Assert.False(seated.Session.Has(Participation.WhichPeer), "the peer picker opened for a peer already chosen");
        Assert.True(seated.Session.Has(Participation.NameTitle), "the naming dialog did not open");
    }

    [Fact]
    public async Task Enter_is_named_on_the_agents_pane_where_it_acts()
    {
        // A key the surface takes and does not name is one nobody finds (T066).
        await using var seated = await OnAgentsAsync();

        Assert.Contains(seated.Shell.Actions(), action => action.Label == Shell.RowMenuTitle);
    }

    /// <summary>The seated fixture, on the project view with the agents pane focused.</summary>
    private static async Task<Seated> OnAgentsAsync()
    {
        var seated = await Seated.OpenAsync(Ct);

        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();

        return seated;
    }

    /// <summary>Puts the cursor on a row, presses Enter, and reads what the menu offers.</summary>
    private static IReadOnlyList<string> Open(Seated seated, string row)
    {
        seated.Session.Row(seated.Shell.Agents.List, row);
        seated.Session.Enter();

        var menu = (ChoiceDialog)seated.Session.Find(row);

        return [.. menu.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty)];
    }

    /// <summary>Opens a row's menu and chooses one entry by pressing Enter on it.</summary>
    private static void Choose(Seated seated, string row, string entry)
    {
        Open(seated, row);

        var menu = (ChoiceDialog)seated.Session.Find(row);

        seated.Session.Row(menu.Choices, entry);
        seated.Session.Enter();
    }
}
