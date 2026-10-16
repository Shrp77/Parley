using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// What both surfaces say about a conversation that was destroyed.
/// </summary>
/// <remarks>
/// <para>
/// The store keeps a record; this is the sentence a person reads, and it is
/// composed once because two surfaces answering "what happened to it" differently would be two
/// accounts of an event that has no other account left.
/// </para>
/// <para>
/// The line it must never cross is inventing content. The record holds none, and a rendering that
/// implied any — a preview, an ellipsis, a "…" where messages used to be — would suggest something
/// is recoverable when nothing is.
/// </para>
/// </remarks>
public sealed class GoneTests
{
    private static DestroyedConversation Record(int messages = 3, params string[] participants) =>
        new(
            "a-discussion",
            new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero),
            messages,
            [.. participants.Select(AgentName.Create)]);

    [Fact]
    public void It_names_the_conversation_when_it_went_how_much_and_who()
    {
        var said = string.Join("\n", Reading.Gone(Record(3, "orders@test", "billing@test")));

        Assert.Contains("a-discussion", said, StringComparison.Ordinal);
        Assert.Contains("3 messages", said, StringComparison.Ordinal);
        Assert.Contains("orders@test", said, StringComparison.Ordinal);
        Assert.Contains("billing@test", said, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_plainly_that_nothing_of_the_content_was_kept()
    {
        // The sentence that stops somebody asking for it back, and the one that says what kind of
        // record this is: facts, not a summary of what was said.
        var said = string.Join("\n", Reading.Gone(Record(3, "orders@test")));

        Assert.Contains("Nothing of what was said was kept", said, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_time_is_the_reader_s_own()
    {
        // Same rule as every other instant on a screen: the person in front of it should not have
        // to do arithmetic to answer "was that this morning".
        var said = string.Join("\n", Reading.Gone(Record(1, "orders@test")));

        Assert.Contains(
            LocalTime.Write(new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero)),
            said,
            StringComparison.Ordinal);
    }

    [Fact]
    public void One_message_is_not_pluralised()
    {
        Assert.Contains(
            "1 message,",
            string.Join("\n", Reading.Gone(Record(1, "orders@test"))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_no_participants_says_nobody_rather_than_trailing_off()
    {
        // Reachable: a conversation an agent named and never traded in has no participant rows, and
        // an empty list rendered as an empty string reads as a sentence that was cut short.
        var said = string.Join("\n", Reading.Gone(Record(0)));

        Assert.Contains("nobody", said, StringComparison.Ordinal);
        Assert.DoesNotContain("between .", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_it_says_suggests_the_messages_could_be_recovered()
    {
        var said = string.Join("\n", Reading.Gone(Record(9, "orders@test")));

        foreach (var misleading in new[] { "archiv", "restore", "recover", "backup" })
        {
            Assert.DoesNotContain(misleading, said, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void It_refuses_a_null_record_rather_than_rendering_an_empty_one()
    {
        Assert.Throws<ArgumentNullException>(() => Reading.Gone(null!));
    }
}
