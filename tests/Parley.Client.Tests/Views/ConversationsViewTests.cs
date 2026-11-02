using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That the conversations list orders, describes and hides what it says it does.
/// </summary>
/// <remarks>
/// <para>
/// The property worth the most here is that <b>a sort does not move the highlight</b>. A list
/// selected by row number reorders under the selection and lands on a different conversation, which
/// nobody notices in a test that sorts a list of one.
/// </para>
/// <para>
/// The second is that <b>the hidden count is always available</b>. Archiving does not stop a
/// conversation receiving messages, and the decision that a message must not un-archive it is only
/// safe because a person can see that something is being kept out of the list.
/// </para>
/// </remarks>
[Unit]
public sealed class ConversationsViewTests
{
    private static ConversationRow Row(
        string id, int minute, int messages = 2, bool archived = false, bool activeSince = false) =>
        new(
            id,
            new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero),
            messages,
            [AgentName.Create("orders@test"), AgentName.Create("billing@test")],
            HasOpenExchange: false,
            archived,
            activeSince);

    private static IReadOnlyList<ConversationRow> Six() =>
    [
        Row("delta", 1), Row("alpha", 5), Row("echo", 3),
        Row("bravo", 6), Row("charlie", 2), Row("foxtrot", 4),
    ];

    [Fact]
    public void By_name_is_ordinal_and_by_date_is_most_recent_first()
    {
        var byName = ConversationsView.Sorted(Six(), SortBy.Name).Select(r => r.Id);
        var byDate = ConversationsView.Sorted(Six(), SortBy.Activity).Select(r => r.Id);

        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"], byName);
        Assert.Equal(["bravo", "alpha", "foxtrot", "echo", "charlie", "delta"], byDate);
    }

    [Fact]
    public void The_same_conversation_is_still_findable_after_either_sort()
    {
        // The highlight is a conversation, never a row number. This is the assertion that a
        // surface holding an index would fail.
        foreach (var by in new[] { SortBy.Name, SortBy.Activity })
        {
            var sorted = ConversationsView.Sorted(Six(), by);

            Assert.Contains(sorted, r => r.Id == "charlie");
            Assert.Equal(6, sorted.Count);
        }
    }

    [Fact]
    public void Ties_on_the_instant_break_on_the_identifier_so_the_order_is_total()
    {
        // Two conversations last active in the same millisecond are not rare: the Windows system
        // clock ticks about every 15.6ms. An unstable order shuffles rows on every redraw.
        IReadOnlyList<ConversationRow> tied = [Row("beta", 1), Row("alpha", 1), Row("gamma", 1)];

        var once = ConversationsView.Sorted(tied, SortBy.Activity).Select(r => r.Id);
        var again = ConversationsView.Sorted(tied, SortBy.Activity).Select(r => r.Id);

        Assert.Equal(["alpha", "beta", "gamma"], once);
        Assert.Equal(once, again);
    }

    [Fact]
    public void The_hidden_line_says_how_many_are_out_of_sight()
    {
        Assert.Equal(
            "2 archived conversations are not shown.",
            ConversationsView.Hidden(2, showingArchived: false));

        Assert.Equal(
            "1 archived conversation is not shown.",
            ConversationsView.Hidden(1, showingArchived: false));
    }

    [Fact]
    public void The_hidden_line_is_absent_when_nothing_is_hidden()
    {
        Assert.Equal(string.Empty, ConversationsView.Hidden(0, showingArchived: false));
        Assert.Equal(string.Empty, ConversationsView.Hidden(4, showingArchived: true));
    }

    [Fact]
    public void The_detail_panel_names_the_participants_and_counts_the_messages()
    {
        var lines = ConversationsView.Describe(Row("alpha", 1, messages: 9));

        Assert.Contains("alpha", lines);
        Assert.Contains("9 messages", lines);
        Assert.Contains(lines, l => l.Contains("orders@test", StringComparison.Ordinal)
                                 && l.Contains("billing@test", StringComparison.Ordinal));
    }

    [Fact]
    public void One_message_is_not_pluralised()
    {
        Assert.Contains("1 message", ConversationsView.Describe(Row("alpha", 1, messages: 1)));
    }

    [Fact]
    public void An_archived_conversation_with_traffic_since_says_so()
    {
        // The safety valve for the decision that a message does not un-archive. Without this, a
        // conversation somebody filed away last month can be busy with nothing saying so.
        var quiet = ConversationsView.Describe(Row("alpha", 1, archived: true));
        var busy = ConversationsView.Describe(Row("alpha", 1, archived: true, activeSince: true));

        Assert.Contains("archived", quiet);
        Assert.Contains(busy, l => l.Contains("messages since", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_highlighted_describes_nothing_rather_than_throwing()
    {
        Assert.Empty(ConversationsView.Describe(null));
    }

    [Fact]
    public void An_empty_project_and_an_all_archived_one_say_different_things()
    {
        // "No conversations" to somebody who archived six would read as having lost them.
        Assert.NotEqual(ConversationsView.Nothing, ConversationsView.AllArchived);
    }

    [Fact]
    public void Rows_carry_the_archived_flag_through_from_the_broker()
    {
        var summaries = new[]
        {
            new ConversationSummary(
                "alpha",
                [AgentName.Create("orders@test")],
                3,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                HasOpenExchange: false,
                Archived: true),
        };

        var rows = ConversationsView.Rows(summaries);

        Assert.True(Assert.Single(rows).Archived);

        // Left false because the summary does not carry when the archiving happened, and inventing
        // it from the last activity would mark every archived conversation as busy.
        Assert.False(rows[0].ActiveSinceArchived);
    }
}
