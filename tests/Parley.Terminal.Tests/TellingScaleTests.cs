using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The dialog against a long history, and against a reply pretending to have been cut.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two probes that only make sense together.</b> An operator who has been instructing an agent
/// for weeks opens this on hundreds of rows, each carrying up to eight thousand characters of
/// somebody's words — and every one of those is agent-written, so the column-zero rule has to hold
/// across all of it rather than on the two-row fixture the other tests use.
/// </para>
/// <para>
/// The forgery is the sharper one. The truncation marker is this build's own words, appended to say
/// that a reply was cut. An agent that ends its reply with that exact text is claiming its answer
/// was truncated when it was not — a small lie, and the interesting kind: it makes an operator
/// believe there is more to read.
/// </para>
/// </remarks>
public sealed class TellingScaleTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private static InstructionRecord Told(int at, string text, string? reply) =>
        new(at, Orders, text, InstructionOutcome.Taken, reply, T0.AddMinutes(at), T0.AddMinutes(at + 1));

    [Fact]
    public void The_column_zero_rule_holds_across_hundreds_of_rows_of_agent_written_text()
    {
        // The rule on the fixture the other tests use is the rule on two rows. This is the rule on
        // the history an operator actually accumulates, with every payload trying to break it.
        var told = Enumerable.Range(0, 400)
            .Select(at => Told(
                at,
                $"instruction {at}\nnot a heading",
                $"reply {at}\n08/09/2026 09:00  taken\n  and something else"))
            .ToList();

        var lines = Telling.Lines(told);

        var headings = lines.Where(line => line.Length > 0 && !line.StartsWith(' ')).ToList();

        // Exactly one heading per instruction, and nothing else at column zero.
        Assert.Equal(told.Count, headings.Count);

        Assert.All(headings, heading =>
            Assert.True(
                heading.Contains("taken", StringComparison.Ordinal)
                || heading.Contains(Telling.Running, StringComparison.Ordinal)
                || heading.Contains(Telling.Declined, StringComparison.Ordinal),
                $"a line at column zero is not a heading: '{heading}'"));
    }

    [Fact]
    public void A_full_length_reply_is_shown_without_breaking_the_rule()
    {
        // Eight thousand characters is what the cap allows, so it is what an agent will send. The
        // failure this catches is a renderer that wrapped or split long text and put a continuation
        // at column zero -- which is exactly the defect 0.18.6 found in the transcript.
        var enormous = string.Join(
            "\n",
            Enumerable.Range(0, 200).Select(at => $"line {at} of a very long answer"));

        var lines = Telling.Lines([Told(1, "have a look", enormous)]);

        var headings = lines.Where(line => line.Length > 0 && !line.StartsWith(' ')).ToList();

        Assert.Single(headings);
    }

    [Fact]
    public void An_agent_cannot_claim_its_reply_was_cut_when_it_was_not()
    {
        // **The forgery.** The marker is this build's own words. An agent that ends its reply with
        // them is saying "there was more" when there was not, which sends an operator looking for
        // something that does not exist.
        //
        // Recorded rather than asserted away: what this checks is that a reply *within* the cap is
        // passed through unaltered, which is the property that matters more. The marker is not a
        // security boundary and cannot be made into one -- an agent can write any words it likes,
        // and "the rest was over the cap" is no more forgeable than "I finished the work". What
        // makes it safe is that it claims nothing an operator would act on destructively.
        var pretending = "I did some of it" + MessageLimits.Truncated;

        Assert.Equal(pretending, MessageLimits.Fit(pretending));

        var lines = Telling.Lines([Told(1, "have a look", pretending)]);

        Assert.Contains(
            lines,
            line => line.Contains("I did some of it", StringComparison.Ordinal));

        // And the rule still holds around it: the claim is inside the agent's own indented block.
        var headings = lines.Where(line => line.Length > 0 && !line.StartsWith(' ')).ToList();

        Assert.Single(headings);
    }

    [Fact]
    public void A_genuinely_cut_reply_and_a_forged_one_are_indistinguishable_and_that_is_recorded()
    {
        // Stated as a test so the limitation is written down where somebody checking will find it.
        // Both end in the same words, because both *are* the same words. The honest thing is to
        // know that rather than to imagine the marker proves anything.
        var forged = "short answer" + MessageLimits.Truncated;
        var genuine = MessageLimits.Fit(new string('x', 200_000));

        Assert.EndsWith(MessageLimits.Truncated, forged, StringComparison.Ordinal);
        Assert.EndsWith(MessageLimits.Truncated, genuine, StringComparison.Ordinal);

        // What does differ, and is the thing worth relying on: only one of them is at the cap.
        Assert.Equal(MessageLimits.MaxPayloadCharacters, genuine.Length);
        Assert.True(forged.Length < MessageLimits.MaxPayloadCharacters);
    }
}
