using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>What every surface says about a spin-off and a move, and what opens in the box (SPEC.md §5.0).</summary>
[Unit]
public sealed class ConversationWordingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly MoveRecord Moved =
        new(1, "pricing", "tax-questions", "human-user@test", At, 5, 3, [7, 9]);

    private static Message Said(long id, string json) => new(
        id, id, MessageType.Request, AgentName.Create("inventory@test"), AgentName.Create("orders@test"), "model", json, "pricing", At, null);

    [Fact]
    public void A_list_says_what_a_conversation_was_spun_off_from_and_nothing_for_one_with_no_parent()
    {
        Assert.Equal("disk-cleanup - spinoff from logging", ConversationWording.Listed("disk-cleanup", "logging"));
        Assert.Equal("logging", ConversationWording.Listed("logging", null));
    }

    [Fact]
    public void A_name_cannot_imitate_it_because_a_name_cannot_hold_a_space()
    {
        Assert.Contains(' ', ConversationWording.SpinoffSuffix("x"));
        Assert.DoesNotMatch(ConversationName.Pattern, " ");
    }

    [Fact]
    public void A_move_reads_from_each_side_naming_the_other()
    {
        Assert.Contains("moved from here to tax-questions", ConversationWording.MoveLine(Moved, "pricing"), StringComparison.Ordinal);
        Assert.Contains("moved here from pricing", ConversationWording.MoveLine(Moved, "tax-questions"), StringComparison.Ordinal);
        Assert.Equal("tax-questions", ConversationWording.OtherSide(Moved, "pricing"));
        Assert.Equal("pricing", ConversationWording.OtherSide(Moved, "tax-questions"));
    }

    [Fact]
    public void A_moves_note_says_the_day_so_it_cannot_read_as_the_day_of_the_messages_around_it()
    {
        Assert.Contains("on 3 Nov 2026, 09:00", ConversationWording.MoveLine(Moved, "pricing", TimeZoneInfo.Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_says_who_and_where_and_marks_every_line()
    {
        var quote = Quoting.Of(Said(1, """{"ask":"first\nsecond"}"""), "pricing").ReplaceLineEndings("\n");

        Assert.Equal("inventory@test said in pricing:\n> first\n> second\n\n", quote);
    }

    [Fact]
    public void An_export_file_says_what_moved_and_who_is_related()
    {
        var export = new ConversationExport(
            [Said(1, """{"ask":"x"}""")],
            [],
            Links: new ConversationLinks("logging", ConversationLinkKind.SpinOff, ["child-one"]),
            Moves: [Moved],
            SettledElsewhere: [new SettledElsewhere(3, 4, "other")]);

        var file = Transcript.Write("pricing", "alpha", export);

        Assert.Contains("Parent conversation: logging", file, StringComparison.Ordinal);
        Assert.Contains("Sub-conversation: child-one", file, StringComparison.Ordinal);
        Assert.Contains("(exchange 7, 9) moved from pricing to tax-questions by human-user@test", file, StringComparison.Ordinal);
        Assert.Contains("Exchange 3 was settled by exchange 4. One of them is now in other.", file, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transcript_draws_a_moves_note_where_the_entries_were()
    {
        var export = new ConversationExport(
            [Said(1, """{"ask":"before"}"""), Said(8, """{"ask":"after"}""")],
            [],
            Moves: [Moved]);

        var lines = Reading.Lines(export, TimeZoneInfo.Utc, "pricing").Select(line => line.Text).ToList();

        var note = lines.FindIndex(line => line.Contains("moved from here to tax-questions", StringComparison.Ordinal));
        var before = lines.FindIndex(line => line.Contains("before", StringComparison.Ordinal));
        var after = lines.FindIndex(line => line.Contains("after", StringComparison.Ordinal));

        Assert.InRange(note, before + 1, after - 1);
    }

    [Fact]
    public void A_transcript_says_what_a_split_settled_pair_is()
    {
        var export = new ConversationExport(
            [Said(1, """{"ask":"before"}""")], [], SettledElsewhere: [new SettledElsewhere(3, 4, "other")]);

        Assert.Contains(
            Reading.Lines(export, TimeZoneInfo.Utc, "pricing"),
            line => line.Text.Contains("One of them is now in other", StringComparison.Ordinal));
    }

    [Fact]
    public void A_send_to_several_is_one_entry_and_goes_whole()
    {
        Message To(long id, string to, string words) => new(
            id, id, MessageType.Request, AgentName.Create("orders@test"), AgentName.Create(to), "model", $$"""{"ask":"{{words}}"}""", "pricing", At, null);

        var messages = new[] { To(1, "billing@test", "same"), To(2, "inventory@test", "same"), To(3, "billing@test", "other") };

        Assert.Equal(2, MovePicking.Turns(messages).Count);
        Assert.Equal([1L, 2L], MovePicking.WithSendsToSeveral(messages, [2]));
        Assert.Equal([3L], MovePicking.WithSendsToSeveral(messages, [3]));
    }

    [Fact]
    public void A_transcript_read_with_no_conversation_named_is_as_it_always_was()
    {
        var export = new ConversationExport([Said(1, """{"ask":"before"}""")], [], Moves: [Moved]);

        Assert.DoesNotContain(Reading.Lines(export, TimeZoneInfo.Utc), line => line.Text.Contains("moved", StringComparison.Ordinal));
    }
}
