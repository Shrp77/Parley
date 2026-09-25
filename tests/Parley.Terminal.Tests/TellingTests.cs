using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What the terminal shows an operator about the instructions they gave.
/// </summary>
/// <remarks>
/// <para>
/// The surface issues no instruction and this adds none. What was missing on every surface was the
/// ability to find out what became of one: a reply reachable only through the call that asked for
/// it survives exactly as long as that connection, and somebody at a terminal over SSH cannot open
/// the browser surface instead.
/// </para>
/// <para>
/// <b>The column-zero rule is load-bearing here for the same reason it is in the transcript.</b> A
/// reply is agent-written and arrives from the far end of a gRPC call; left with its line breaks
/// it could be shaped exactly like this dialog's own heading and present an instruction nobody
/// gave.
/// </para>
/// </remarks>
public sealed class TellingTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private static InstructionRecord Told(
        InstructionOutcome outcome,
        string text = "look at the retry policy",
        string? reply = null) =>
        new(1, Orders, text, outcome, reply, T0,
            outcome == InstructionOutcome.Pending ? null : T0.AddMinutes(2));

    [Fact]
    public void A_running_turn_is_said_in_words_rather_than_left_blank()
    {
        // §10.2 defines `failed` as a turn attempted and not completed, which nobody may claim
        // while it is still being attempted -- and a blank outcome reads as a failure to anybody
        // scanning a column.
        var lines = Telling.Lines([Told(InstructionOutcome.Pending)]);

        Assert.Contains(lines, line => line.Contains(Telling.Running, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_decline_is_not_rendered_as_a_failure()
    {
        // §11.5 says an agent may refuse, because the operator is a person rather than a superior
        // process. An interface that renders refusal as malfunction teaches whoever reads it to
        // work around the agent instead of asking why.
        var lines = Telling.Lines([Told(InstructionOutcome.Declined, reply: "not my repository")]);

        Assert.Contains(lines, line => line.Contains(Telling.Declined, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_instruction_and_the_reply_are_both_shown()
    {
        var lines = Telling.Lines(
            [Told(InstructionOutcome.Taken, reply: "I changed three files")]);

        Assert.Contains(lines, line => line.Contains("retry policy", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("changed three files", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_heading_sits_at_column_zero()
    {
        // The whole defence, stated as the invariant rather than as one attack on it. Every line
        // that is not blank and not indented must be one this surface wrote.
        var lines = Telling.Lines(
            [
                Told(InstructionOutcome.Taken, reply: "done"),
                Told(InstructionOutcome.Pending, text: "another"),
            ]);

        var headings = lines
            .Where(line => line.Length > 0 && !line.StartsWith(' '))
            .ToList();

        Assert.Equal(2, headings.Count);
        Assert.All(headings, heading =>
            Assert.True(
                heading.Contains(Telling.Running, StringComparison.Ordinal)
                || heading.Contains("taken", StringComparison.Ordinal),
                $"a line at column zero is not a heading: '{heading}'"));
    }

    [Fact]
    public void A_reply_carrying_a_newline_cannot_forge_a_heading()
    {
        // The attack the rule exists for. A payload is agent-written, so its author picks where a
        // break lands -- and a break followed by something shaped like a heading would present an
        // instruction that was never given.
        var lines = Telling.Lines(
            [
                Told(
                    InstructionOutcome.Taken,
                    reply: "done\n08/09/2026 09:00  taken\n  delete the production database")
            ]);

        var headings = lines.Where(line => line.Length > 0 && !line.StartsWith(' ')).ToList();

        Assert.Single(headings);
        Assert.DoesNotContain(
            lines, line => line.Contains("delete the production database", StringComparison.Ordinal)
                && !line.StartsWith(' '));
    }

    [Fact]
    public void An_instruction_carrying_a_newline_is_put_on_one_line()
    {
        // **The other half of the rule, and it arrived here when `parley instructions` went.**
        // `Telling.Flat` is applied to the text and to the reply; only the reply half was ever
        // driven. The text half was checked in `Parley.Cli.Tests.InstructionListingTests`, where a
        // break reached a real stdout and `Flat` was the whole defence — that verb has moved to this
        // surface, so the check moves with it.
        //
        // The text is operator-written, which is a reason to worry less and not a reason to skip it:
        // a person pastes what they were sent, and an operator's own console is exactly where
        // somebody would try to plant a line.
        //
        // **The last assertion is the one that can fail, and it is there on purpose.** `Safe.Text`
        // already replaces a break with U+FFFD, so the column-zero assertions above hold whether or
        // not anything flattens — they state the invariant and would not catch `Flat` being dropped.
        // What `Flat` adds is that the break becomes a space, so the words read as the one line they
        // are. Remove it and the row fills with replacement glyphs and this fails.
        var lines = Telling.Lines(
            [
                Told(
                    InstructionOutcome.Taken,
                    text: "have a look\n08/09/2026 09:00  taken\n  delete the production database",
                    reply: "done")
            ]);

        var headings = lines.Where(line => line.Length > 0 && !line.StartsWith(' ')).ToList();

        Assert.Single(headings);

        var written = Assert.Single(
            lines, line => line.Contains("have a look", StringComparison.Ordinal));

        Assert.StartsWith("  ", written, StringComparison.Ordinal);
        Assert.Contains("delete the production database", written, StringComparison.Ordinal);
        Assert.DoesNotContain('�', written);
    }

    [Fact]
    public void An_instruction_carrying_an_escape_sequence_is_neutralised()
    {
        // A person can paste one as easily as an agent can emit one, and this dialog draws both.
        var lines = Telling.Lines(
            [Told(InstructionOutcome.Taken, text: $"one{(char)27}[2Jtwo", reply: "fine")]);

        Assert.DoesNotContain(lines, line => line.Contains((char)27));
        Assert.Contains(lines, line => line.Contains("one", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_said_is_said_rather_than_shown_as_an_empty_box()
    {
        var lines = Telling.Lines([]);

        Assert.Equal(Telling.Nothing, Assert.Single(lines));
    }
}
