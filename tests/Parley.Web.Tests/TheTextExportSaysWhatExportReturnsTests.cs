using System.Text;
using Parley.Core;
using Parley.Web.Pages.Conversations;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page's Export TXT file says what the Export JSON file says: the destroyed record and the item.
/// </summary>
/// <remarks>
/// Lens 3 found it after the JSON file was fixed: a destroyed conversation still downloaded as text with no messages and
/// nothing else, which is what a store that lost something would give, the failure SPEC.md §10.2 and §14 give as the
/// reason the record exists.
/// </remarks>
[Unit]
public sealed class TheTextExportSaysWhatExportReturnsTests
{
    private static readonly DateTimeOffset Gone = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static string Written(ConversationExport export) =>
        Encoding.UTF8.GetString(DetailModel.TextExport("pricing", "alpha", export));

    [Fact]
    public void A_destroyed_conversation_says_it_was_destroyed()
    {
        var written = Written(new ConversationExport(
            [],
            [],
            new DestroyedConversation(
                "pricing", Gone, 7, [AgentName.Create("orders@test"), AgentName.Create("billing@test")])));

        Assert.StartsWith("Conversation pricing in project alpha", written, StringComparison.Ordinal);
        Assert.Contains("Destroyed on 2026-11-01 09:00:00Z.", written, StringComparison.Ordinal);
        Assert.Contains("It held 7 messages, between orders@test, billing@test.", written, StringComparison.Ordinal);

        // Not the preamble of a conversation that had messages and showed none.
        Assert.DoesNotContain("Pairwise throughout", written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_roadmap_item_it_belongs_to_is_named_on_one_line()
    {
        var written = Written(new ConversationExport([], [], null, new RoadmapItemRef(3, 1, "Launch\nForged: line", 2, false)));
        var lines = written.Split(Environment.NewLine);

        Assert.Equal("Roadmap: item 2 of #1 Launch\\nForged: line", lines[1]);
    }

    [Fact]
    public void A_live_conversation_says_nothing_of_a_destroy()
    {
        var written = Written(new ConversationExport([], []));

        Assert.DoesNotContain("Destroyed", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Roadmap:", written, StringComparison.Ordinal);
        Assert.Contains("Pairwise throughout", written, StringComparison.Ordinal);
    }
}
