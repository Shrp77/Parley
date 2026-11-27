using System.Text.Json;
using Parley.Core;
using Parley.Web.Pages.Conversations;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page's Export JSON file carries everything SPEC.md §10.2 says export returns.
/// </summary>
/// <remarks>
/// Lens 2 found it: the file held only the messages and exchanges, so a destroyed conversation downloaded as two empty
/// arrays, which is also what a store that lost something would give. The destroyed record is what tells them apart.
/// </remarks>
[Unit]
public sealed class TheJsonExportSaysWhatExportReturnsTests
{
    private static readonly DateTimeOffset Gone = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static JsonElement Written(ConversationExport export) =>
        JsonDocument.Parse(DetailModel.RawExport("pricing", export)).RootElement.Clone();

    [Fact]
    public void A_destroyed_conversation_carries_its_record()
    {
        var root = Written(new ConversationExport(
            [],
            [],
            new DestroyedConversation(
                "pricing", Gone, 7, [AgentName.Create("orders@test"), AgentName.Create("billing@test")])));

        var destroyed = root.GetProperty("destroyed");

        Assert.Equal("pricing", destroyed.GetProperty("id").GetString());
        Assert.Equal(7, destroyed.GetProperty("messages").GetInt32());
        Assert.Equal(Gone, destroyed.GetProperty("destroyedUtc").GetDateTimeOffset());
        Assert.Equal(
            ["orders@test", "billing@test"],
            destroyed.GetProperty("participants").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void The_roadmap_item_it_belongs_to_is_named()
    {
        var root = Written(new ConversationExport([], [], null, new RoadmapItemRef(3, 1, "Launch", 2, false)));

        Assert.Equal(3, root.GetProperty("item").GetProperty("roadmapId").GetInt64());
        Assert.Equal(2, root.GetProperty("item").GetProperty("itemNumber").GetInt32());
    }

    [Fact]
    public void A_live_conversation_says_it_was_not_destroyed()
    {
        var root = Written(new ConversationExport([], []));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("destroyed").ValueKind);
        Assert.Equal("pricing", root.GetProperty("conversation").GetString());
    }
}
