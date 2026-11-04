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
    public void A_notice_the_broker_wrote_is_not_quoted_as_the_senders_words()
    {
        var notice = Said(1, """{"outcomes":[{"id":"q1","outcome":"closed","text":"The person closed this request without an answer."}]}""")
            with { ResponderModel = Responder.Broker };

        var quote = Quoting.Of(notice, "pricing");

        Assert.StartsWith("Parley noted in pricing:", quote, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory@test", quote, StringComparison.Ordinal);
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
        Assert.Contains("(exchange 7, 9) moved from pricing to tax-questions by human-user@test on 2026-11-03 09:00:00Z.", file, StringComparison.Ordinal);
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
    public void In_the_destination_a_moves_note_comes_before_the_first_message_that_moved()
    {
        var export = new ConversationExport(
            [Said(2, """{"ask":"its own"}"""), Said(5, """{"ask":"what moved"}"""), Said(6, """{"ask":"and later"}""")],
            [],
            Moves: [Moved]);

        var lines = Reading.Lines(export, TimeZoneInfo.Utc, "tax-questions").Select(line => line.Text).ToList();

        var note = lines.FindIndex(line => line.Contains("moved here from pricing", StringComparison.Ordinal));
        var own = lines.FindIndex(line => line.Contains("its own", StringComparison.Ordinal));
        var moved = lines.FindIndex(line => line.Contains("what moved", StringComparison.Ordinal));
        var header = lines.FindLastIndex(moved, line => line.Contains("inventory@test", StringComparison.Ordinal));

        Assert.InRange(note, own + 1, header - 1);
    }

    [Fact]
    public void A_moves_note_is_drawn_when_what_moved_was_the_last_of_the_conversation()
    {
        var export = new ConversationExport([Said(1, """{"ask":"what stayed"}""")], [], Moves: [Moved]);

        var lines = Reading.Lines(export, TimeZoneInfo.Utc, "pricing").Select(line => line.Text).ToList();

        var note = lines.FindIndex(line => line.Contains("moved from here to tax-questions", StringComparison.Ordinal));

        Assert.InRange(note, lines.FindIndex(line => line.Contains("what stayed", StringComparison.Ordinal)) + 1, lines.Count - 1);
    }

    [Fact]
    public void Each_moves_note_is_where_its_entries_were_whichever_move_came_first()
    {
        var later = Moved with { Id = 2, Destination = "stock-questions", FirstMessage = 2 };
        var earlier = Moved with { Id = 1, Destination = "tax-questions", FirstMessage = 5 };
        var export = new ConversationExport(
            [Said(1, """{"ask":"one"}"""), Said(3, """{"ask":"three"}"""), Said(6, """{"ask":"six"}""")],
            [],
            Moves: [earlier, later]);

        var lines = Reading.Lines(export, TimeZoneInfo.Utc, "pricing").Select(line => line.Text).ToList();

        Assert.InRange(
            lines.FindIndex(line => line.Contains("to stock-questions", StringComparison.Ordinal)),
            lines.FindIndex(line => line.Contains("one", StringComparison.Ordinal)) + 1,
            lines.FindIndex(line => line.Contains("three", StringComparison.Ordinal)) - 1);
        Assert.InRange(
            lines.FindIndex(line => line.Contains("to tax-questions", StringComparison.Ordinal)),
            lines.FindIndex(line => line.Contains("three", StringComparison.Ordinal)) + 1,
            lines.FindIndex(line => line.Contains("six", StringComparison.Ordinal)) - 1);
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

    private static Message Asked(long id, string from, string to, string words) => new(
        id, id, MessageType.Request, AgentName.Create(from), AgentName.Create(to), "model", $$"""{"ask":"{{words}}"}""", "pricing", At, null);

    [Fact]
    public void Different_words_to_different_agents_are_two_entries_though_they_sit_side_by_side()
    {
        var messages = new[] { Asked(1, "orders@test", "billing@test", "gross?"), Asked(2, "orders@test", "inventory@test", "how many?") };

        Assert.Equal(2, MovePicking.Turns(messages).Count);
        Assert.Equal([1L], MovePicking.WithSendsToSeveral(messages, [1]));
    }

    [Fact]
    public void The_same_words_from_a_different_sender_are_two_entries()
    {
        var messages = new[] { Asked(1, "orders@test", "billing@test", "same"), Asked(2, "inventory@test", "human-user@test", "same") };

        Assert.Equal(2, MovePicking.Turns(messages).Count);
        Assert.Equal([2L], MovePicking.WithSendsToSeveral(messages, [2]));
    }

    [Fact]
    public void A_recipient_asked_twice_in_the_same_words_starts_a_new_entry()
    {
        // The third goes to somebody the entry already holds, which is the same words said again and not one send.
        var toTheSecond = new[]
        {
            Asked(1, "orders@test", "billing@test", "same"), Asked(2, "orders@test", "inventory@test", "same"), Asked(3, "orders@test", "inventory@test", "same"),
        };
        var toTheFirst = new[]
        {
            Asked(1, "orders@test", "billing@test", "same"), Asked(2, "orders@test", "inventory@test", "same"), Asked(3, "orders@test", "billing@test", "same"),
        };

        Assert.Equal([2, 1], MovePicking.Turns(toTheSecond).Select(turn => turn.Count));
        Assert.Equal([1L, 2L], MovePicking.WithSendsToSeveral(toTheSecond, [1]));
        Assert.Equal([3L], MovePicking.WithSendsToSeveral(toTheSecond, [3]));
        Assert.Equal([2, 1], MovePicking.Turns(toTheFirst).Select(turn => turn.Count));
    }

    [Fact]
    public void A_transcript_read_with_no_conversation_named_is_as_it_always_was()
    {
        var export = new ConversationExport([Said(1, """{"ask":"before"}""")], [], Moves: [Moved]);

        Assert.DoesNotContain(Reading.Lines(export, TimeZoneInfo.Utc), line => line.Text.Contains("moved", StringComparison.Ordinal));
    }
}
