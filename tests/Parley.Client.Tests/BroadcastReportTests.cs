using Parley.Client.Seating;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// What a broadcast reports, which is the only thing a person sees of twenty sends.
/// </summary>
/// <remarks>
/// <para>
/// Twenty sentences is not a report, and a bare "sent" hides the case that matters: a broadcast
/// that reached three of nineteen is a different fact from one that reached all nineteen. A surface
/// that said nothing about the difference is what produced the report this feature answers, so the
/// aggregation is worth the same scrutiny as the sends.
/// </para>
/// <para>
/// Driven through the pure builder rather than through twenty round trips. What is being checked is
/// the arithmetic and the wording, and standing a broker up with nineteen agents in four states
/// would be checking the fixture.
/// </para>
/// </remarks>
public sealed class BroadcastReportTests
{
    private static string Say(
        int asked,
        int told,
        int repeated = 0,
        bool stopped = false,
        (Wire.WakeOutcome, int)[]? counts = null,
        string[]? unreachable = null,
        Refusal[]? refused = null) =>
        SeatVoice.Reported(
            asked,
            told,
            repeated,
            stopped,
            (counts ?? []).ToDictionary(one => one.Item1, one => one.Item2),
            unreachable ?? [],
            refused ?? []);

    /// <summary>One refusal, for a reason the caller does not care about.</summary>
    private static Refusal Turned(string agent) =>
        new(agent, SeatVoice.Because(Wire.NotifyRejection.RecipientUnroutable));

    [Fact]
    public void Reaching_everybody_says_so_without_arithmetic()
    {
        var said = Say(3, 3, counts: [(Wire.WakeOutcome.Started, 3)]);

        Assert.Contains("Told all 3", said, StringComparison.Ordinal);
        Assert.Contains("3 being started", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Reaching_some_of_them_says_both_numbers()
    {
        // The whole point. "Told 3" beside nothing else reads as success on a roster of nineteen.
        var said = Say(19, 3, counts: [(Wire.WakeOutcome.Started, 3)], refused: [Turned("a@b")]);

        Assert.Contains("Told 3 of 19", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Those_nothing_can_start_are_named_rather_than_counted()
    {
        // Counted, a person cannot act on it. Named, they can go and look at those machines --
        // and this is not a failure, so the wording must not read as one.
        var said = Say(
            3,
            3,
            counts: [(Wire.WakeOutcome.Started, 1), (Wire.WakeOutcome.Unreachable, 2)],
            unreachable: ["docs@far", "infra@far"]);

        Assert.Contains("docs@far", said, StringComparison.Ordinal);
        Assert.Contains("infra@far", said, StringComparison.Ordinal);
        Assert.Contains("Waiting for", said, StringComparison.Ordinal);

        foreach (var wrong in new[] { "not sent", "failed", "could not send", "lost" })
        {
            Assert.DoesNotContain(wrong, said, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_seat_lost_part_way_is_said_outright()
    {
        // A person told merely that their seat went cannot tell whether to send the whole thing
        // again or the remainder, so the count above it is the actionable half.
        var said = Say(19, 4, stopped: true, counts: [(Wire.WakeOutcome.Started, 4)]);

        Assert.Contains("Told 4 of 19", said, StringComparison.Ordinal);
        Assert.Contains(SeatVoice.Left, said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outcome_nobody_hit_is_not_mentioned()
    {
        // A report reading "0 already running, 0 already being seen to" on every send is a report
        // people stop reading, which is how the one line that matters gets missed.
        var said = Say(2, 2, counts: [(Wire.WakeOutcome.Started, 2)]);

        Assert.DoesNotContain("0 ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("already running", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_state_at_once_still_reads_as_one_sentence()
    {
        // The busiest case, which is the one a real deployment produces: some started, some already
        // up, some already being seen to, some unstartable, one refused.
        var said = Say(
            10,
            9,
            counts:
            [
                (Wire.WakeOutcome.Started, 4),
                (Wire.WakeOutcome.AlreadyRunning, 3),
                (Wire.WakeOutcome.AlreadyComing, 1),
                (Wire.WakeOutcome.Unreachable, 1),
            ],
            unreachable: ["docs@far"],
            refused: [Turned("gone@here")]);

        Assert.Contains("Told 9 of 10", said, StringComparison.Ordinal);
        Assert.Contains("4 being started", said, StringComparison.Ordinal);
        Assert.Contains("3 already running", said, StringComparison.Ordinal);
        Assert.Contains("1 already being seen to", said, StringComparison.Ordinal);
        Assert.Contains("docs@far", said, StringComparison.Ordinal);
        Assert.Contains("gone@here", said, StringComparison.Ordinal);

        // One line. This goes in a status row on a terminal, and a report with newlines in it would
        // be cut to its first line by the surface that shows it.
        Assert.DoesNotContain('\n', said);
        Assert.DoesNotContain('\r', said);
    }

    [Fact]
    public void A_broadcast_that_stored_nothing_does_not_report_that_it_told_everybody()
    {
        // **Found by the review gate, and it is this feature's own defect rebuilt inside it.**
        // §13.1 refuses a byte-identical repeat within sixty seconds, and the person who meets that
        // is precisely the one re-sending a broadcast after a partial failure. The first version
        // counted a deduplicated reply as told, so nineteen refusals reported "Told all 19" — the
        // "Sent." problem this whole feature exists to remove, in the code that removes it.
        var said = Say(19, 0, repeated: 19);

        Assert.DoesNotContain("Told all", said, StringComparison.Ordinal);
        Assert.Contains("Told 0 of 19", said, StringComparison.Ordinal);
        Assert.Contains("Every one was the same notice again", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Some_repeated_and_some_new_says_both()
    {
        // The partial re-send, which is the realistic shape: a few were missed the first time and
        // are told now, and the rest already had it.
        var said = Say(19, 4, repeated: 15, counts: [(Wire.WakeOutcome.Started, 4)]);

        Assert.Contains("Told 4 of 19", said, StringComparison.Ordinal);
        Assert.Contains("15 had already been sent this", said, StringComparison.Ordinal);
        Assert.Contains("4 being started", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_repeated_says_nothing_about_repeats()
    {
        var said = Say(3, 3, counts: [(Wire.WakeOutcome.Started, 3)]);

        Assert.DoesNotContain("again", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("already been sent", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_says_why_and_not_only_who()
    {
        // **The owner's report.** A whole roster refused, and the line read "Told 0 of 19. Refused
        // for" and then nineteen names — which is the one shape nobody can act on. The wire has
        // carried the reason since the operation existed and this path threw it away.
        var said = Say(19, 0, refused: [.. Enumerable.Range(1, 19).Select(at => Turned($"a{at}@b"))]);

        Assert.Contains("Told 0 of 19", said, StringComparison.Ordinal);
        Assert.Contains("nothing has registered under that name", said, StringComparison.Ordinal);
    }

    [Fact]
    public void One_cause_for_the_whole_roster_is_said_once()
    {
        var said = Say(19, 0, refused: [.. Enumerable.Range(1, 19).Select(at => Turned($"a{at}@b"))]);
        var times = said.Split("Refused for", StringSplitOptions.None).Length - 1;

        // Grouped by why rather than listed by whom. Nineteen copies of one sentence is the same
        // unreadable line the names were.
        Assert.Equal(1, times);
    }

    [Fact]
    public void Two_causes_are_told_apart()
    {
        // The mixed case, which is the one grouping exists for: some names nothing has registered,
        // and one conversation that closed underneath the send.
        var said = Say(
            4,
            2,
            counts: [(Wire.WakeOutcome.Started, 2)],
            refused:
            [
                Turned("a@b"),
                new Refusal("c@d", SeatVoice.Because(Wire.NotifyRejection.ConversationClosed)),
            ]);

        Assert.Contains("nothing has registered under that name", said, StringComparison.Ordinal);
        Assert.Contains("that conversation is closed", said, StringComparison.Ordinal);
        Assert.Contains("a@b", said, StringComparison.Ordinal);
        Assert.Contains("c@d", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_list_of_names_is_cut_short_rather_than_running_off_the_line()
    {
        // The other half of the owner's report: the message was cut off part way through the
        // names. A status row shows one line, so the report has to fit in one.
        var said = Say(19, 0, refused: [.. Enumerable.Range(1, 19).Select(at => Turned($"agent-{at:00}@here"))]);

        Assert.Contains("and 15 more", said, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-19@here", said, StringComparison.Ordinal);

        // Short enough that a terminal shows the reason, which is the part that matters.
        Assert.True(said.Length < 320, $"the report is {said.Length} characters: {said}");
    }

    [Fact]
    public void Those_nothing_can_start_are_cut_short_the_same_way()
    {
        var said = Say(
            19,
            19,
            counts: [(Wire.WakeOutcome.Unreachable, 19)],
            unreachable: [.. Enumerable.Range(1, 19).Select(at => $"agent-{at:00}@far")]);

        Assert.Contains("and 15 more", said, StringComparison.Ordinal);
        Assert.Contains("nothing here can start them", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retired_seat_refusing_everything_says_that_rather_than_naming_everybody()
    {
        // The realistic uniform refusal, and the one where naming the recipients is actively
        // misleading: nothing is wrong with any of them.
        var said = Say(
            19,
            0,
            refused:
            [
                .. Enumerable.Range(1, 19).Select(at =>
                    new Refusal($"a{at}@b", SeatVoice.Because(Wire.NotifyRejection.SenderRetired)))
            ]);

        Assert.Contains("your seat has been retired", said, StringComparison.Ordinal);
        Assert.Contains("Take a seat again", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Nobody_told_is_not_reported_as_success()
    {
        var said = Say(4, 0, refused: [Turned("a@b"), Turned("c@d"), Turned("e@f"), Turned("g@h")]);

        Assert.Contains("Told 0 of 4", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Told all", said, StringComparison.Ordinal);
    }
}
