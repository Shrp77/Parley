using Parley.Client.Seating;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// What a Request to everybody reports, which is the only thing a person sees of twenty sends.
/// </summary>
/// <remarks>
/// <para>
/// Twenty sentences is not a report, and a bare "sent" hides the case that matters: a Request that reached
/// three of nineteen is a different fact from one that reached all nineteen. A surface that said nothing about
/// the difference is what produced the report this answers, so the aggregation is worth the same scrutiny as
/// the sends.
/// </para>
/// <para>
/// Driven through the pure builder rather than through twenty round trips. What is being checked is the
/// arithmetic and the wording, and standing a broker up with nineteen agents in four states would be checking
/// the fixture.
/// </para>
/// </remarks>
[Integration]
public sealed class BroadcastReportTests
{
    private static string Say(
        int asked,
        int sent,
        bool stopped = false,
        (Wire.WakeOutcome, int)[]? counts = null,
        string[]? unreachable = null,
        TurnedAway[]? refused = null) =>
        SeatVoice.Reported(
            asked,
            sent,
            stopped,
            (counts ?? []).ToDictionary(one => one.Item1, one => one.Item2),
            unreachable ?? [],
            refused ?? []);

    /// <summary>One refusal, for a reason the caller does not care about.</summary>
    private static TurnedAway Turned(string agent) =>
        new(agent, SeatVoice.Because(Wire.RequestRejection.ReceiverNotRegistered));

    [Fact]
    public void Reaching_everybody_says_what_is_now_owed()
    {
        // Every Request that landed is an exchange somebody owes a Result on, and a report that said only
        // "sent" would leave that off the screen.
        var said = Say(3, 3, counts: [(Wire.WakeOutcome.Started, 3)]);

        Assert.Contains("Sent to all 3", said, StringComparison.Ordinal);
        Assert.Contains("3 exchanges are open, each owed a Result", said, StringComparison.Ordinal);
        Assert.Contains("3 being started", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Reaching_some_of_them_says_both_numbers_and_why_the_rest_were_refused()
    {
        // "Sent to 1" beside nothing else reads as success on a roster of three. The reason comes first, said
        // once per cause, the names after it.
        var said = Say(3, 1, counts: [(Wire.WakeOutcome.Started, 1)], refused: [Turned("billing@test"), Turned("inventory@test")]);

        Assert.Contains("Sent to 1 of 3", said, StringComparison.Ordinal);
        Assert.Contains("One exchange is open", said, StringComparison.Ordinal);
        Assert.Contains("Refused for 2", said, StringComparison.Ordinal);
        Assert.Contains("billing@test", said, StringComparison.Ordinal);

        // Said once, not once per name — the defect the grouping was built to remove.
        Assert.Single(said.Split("nothing has registered", StringSplitOptions.None).Skip(1));
    }

    [Fact]
    public void Sending_nothing_successfully_claims_no_debt()
    {
        // A report saying exchanges are open when none were would send somebody to wait for Results that
        // are not coming, which is worse than saying nothing.
        var said = Say(
            2,
            0,
            refused:
            [
                new("orders@test", SeatVoice.Because(Wire.RequestRejection.ConversationClosed)),
                new("billing@test", SeatVoice.Because(Wire.RequestRejection.ConversationClosed)),
            ]);

        Assert.Contains("Sent to 0 of 2", said, StringComparison.Ordinal);
        Assert.DoesNotContain("owed", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seat_lost_part_way_is_said_outright()
    {
        // A person told merely that their seat went cannot tell whether to send the whole thing again or the
        // remainder, so the count before it is the actionable half.
        var said = Say(19, 4, stopped: true, counts: [(Wire.WakeOutcome.Started, 4)]);

        Assert.Contains("Sent to 4 of 19", said, StringComparison.Ordinal);
        Assert.Contains(SeatVoice.Left, said, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_refusal_has_its_own_reason()
    {
        // A rejection falling through to "did not say why" is a refusal a person cannot act on, and the
        // fallback is only for a broker newer than this client. Every member rather than a list, so a
        // rejection added to the wire and not to the sentence fails here.
        var fallback = SeatVoice.Because(Wire.RequestRejection.Unspecified);

        var unexplained = Enum.GetValues<Wire.RequestRejection>()
            .Where(rejection => rejection is not (Wire.RequestRejection.Unspecified or Wire.RequestRejection.None))
            .Where(rejection => SeatVoice.Because(rejection) == fallback)
            .ToArray();

        Assert.Empty(unexplained);
    }

    [Fact]
    public void A_repeated_request_is_reported_as_refused_rather_than_sent()
    {
        // The person who meets a duplicate is precisely the one re-sending after a partial failure. A report
        // counting it as sent would say "Sent to all 19" for nineteen refusals — the bare "Sent." this
        // report exists to remove.
        var said = Say(
            19,
            0,
            refused: [.. Enumerable.Range(1, 19).Select(at => new TurnedAway($"a{at}@b", SeatVoice.Because(Wire.RequestRejection.Duplicate)))]);

        Assert.DoesNotContain("Sent to all", said, StringComparison.Ordinal);
        Assert.Contains("Sent to 0 of 19", said, StringComparison.Ordinal);
        Assert.Contains("already sent exactly this", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Those_nothing_can_start_are_named_rather_than_counted()
    {
        // Counted, a person cannot act on it. Named, they can go and look at those machines — and this is not
        // a failure, so the wording must not read as one.
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
    public void An_outcome_nobody_hit_is_not_mentioned()
    {
        // A report reading "0 already running, 0 already being seen to" on every send is a report people stop
        // reading, which is how the one line that matters gets missed.
        var said = Say(2, 2, counts: [(Wire.WakeOutcome.Started, 2)]);

        Assert.DoesNotContain(" 0 ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("already running", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_state_at_once_still_reads_as_one_line()
    {
        // The busiest case, which is the one a real deployment produces.
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

        Assert.Contains("Sent to 9 of 10", said, StringComparison.Ordinal);
        Assert.Contains("4 being started", said, StringComparison.Ordinal);
        Assert.Contains("3 already running", said, StringComparison.Ordinal);
        Assert.Contains("1 already being seen to", said, StringComparison.Ordinal);
        Assert.Contains("docs@far", said, StringComparison.Ordinal);
        Assert.Contains("gone@here", said, StringComparison.Ordinal);

        // One line. This goes in a status row on a terminal, and a report with newlines in it would be cut to
        // its first line by the surface that shows it.
        Assert.DoesNotContain('\n', said);
        Assert.DoesNotContain('\r', said);
    }

    [Fact]
    public void A_refusal_says_why_and_not_only_who()
    {
        // **The reason comes first.** A whole roster refused must not read "Refused for" and then nineteen
        // names — the one shape nobody can act on.
        var said = Say(19, 0, refused: [.. Enumerable.Range(1, 19).Select(at => Turned($"a{at}@b"))]);

        Assert.Contains("Sent to 0 of 19", said, StringComparison.Ordinal);
        Assert.Contains("nothing has registered under that name", said, StringComparison.Ordinal);
        Assert.Equal(1, said.Split("Refused for", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Two_causes_are_told_apart()
    {
        var said = Say(
            4,
            2,
            counts: [(Wire.WakeOutcome.Started, 2)],
            refused:
            [
                Turned("a@b"),
                new TurnedAway("c@d", SeatVoice.Because(Wire.RequestRejection.ConversationClosed)),
            ]);

        Assert.Contains("nothing has registered under that name", said, StringComparison.Ordinal);
        Assert.Contains("that conversation is closed", said, StringComparison.Ordinal);
        Assert.Contains("a@b", said, StringComparison.Ordinal);
        Assert.Contains("c@d", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_list_of_names_is_cut_short_rather_than_running_off_the_line()
    {
        var said = Say(19, 0, refused: [.. Enumerable.Range(1, 19).Select(at => Turned($"agent-{at:00}@here"))]);

        Assert.Contains("and 15 more", said, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-19@here", said, StringComparison.Ordinal);
        Assert.True(said.Length < 320, $"the report is {said.Length} characters: {said}");
    }

    [Fact]
    public void Those_nothing_can_start_are_cut_short_the_same_way_and_counted_first()
    {
        var said = Say(
            19,
            19,
            counts: [(Wire.WakeOutcome.Unreachable, 19)],
            unreachable: [.. Enumerable.Range(1, 19).Select(at => $"agent-{at:00}@far")]);

        Assert.Contains("and 15 more", said, StringComparison.Ordinal);
        Assert.Contains("nothing can start them", said, StringComparison.Ordinal);
        Assert.Contains("Waiting for 19", said[..Math.Min(80, said.Length)], StringComparison.Ordinal);
    }

    [Fact]
    public void A_retired_seat_refusing_everything_says_that_rather_than_naming_everybody()
    {
        // The realistic uniform refusal, and the one where naming the recipients is actively misleading:
        // nothing is wrong with any of them.
        var said = Say(
            19,
            0,
            refused: [.. Enumerable.Range(1, 19).Select(at => new TurnedAway($"a{at}@b", SeatVoice.Because(Wire.RequestRejection.SenderRetired)))]);

        Assert.Contains("your seat has been retired", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brokers_own_words_cannot_forge_a_second_line_of_this_report()
    {
        // **Transcript forgery, in a new place.** The reason on the failure path is `Status.Detail` —
        // whatever came back from the far end of a gRPC call — and a newline in it produced a second line
        // reading as this report's own success sentence in somebody else's hand.
        var said = Say(
            2,
            0,
            refused:
            [
                new TurnedAway("a@b", "unavailable\nSent to all 2."),
                new TurnedAway("c@d", "unavailable"),
            ]);

        Assert.DoesNotContain('\n', said);
        Assert.DoesNotContain('\r', said);

        // The words survive; only the break goes. Dropping the detail would lose the one thing a person can
        // act on.
        Assert.Contains("unavailable", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_for_every_recipient_does_not_bring_the_long_line_back()
    {
        // Grouping is only short when the reasons repeat, and a broker whose status detail names the recipient
        // — the ordinary shape of a gRPC status — gives one group each.
        var said = Say(
            19,
            0,
            refused: [.. Enumerable.Range(1, 19).Select(at => new TurnedAway($"agent-{at:00}@here", $"no route to agent-{at:00}@here"))]);

        Assert.True(said.Length < 320, $"the report is {said.Length} characters: {said}");

        // Nothing is hidden: what is not spelled out is counted.
        Assert.Contains("17 more were refused", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_with_no_reason_does_not_end_in_a_dangling_colon()
    {
        var said = Say(1, 0, refused: [new TurnedAway("a@b", string.Empty)]);

        Assert.Contains("did not say why.", said, StringComparison.Ordinal);
        Assert.DoesNotContain(":  ", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_eighty_characters_carry_the_part_somebody_can_act_on()
    {
        // **What survives a narrow row.** A terminal shows this in one row of whatever width it has, so what
        // comes first is what survives. Counts, then the reason, then the names.
        var said = Say(19, 0, refused: [.. Enumerable.Range(1, 19).Select(at => Turned($"agent-{at:00}@here"))]);

        var row = said[..Math.Min(80, said.Length)];

        Assert.Contains("Sent to 0 of 19", row, StringComparison.Ordinal);
        Assert.Contains("nothing has registered", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_carrying_a_line_break_cannot_either()
    {
        // The names cannot carry one today, which is exactly why this is worth pinning: the rule is applied to
        // the whole report rather than to the half that currently needs it.
        var said = Say(1, 1, counts: [(Wire.WakeOutcome.Unreachable, 1)], unreachable: ["docs@far\nSent to all 1."]);

        Assert.DoesNotContain('\n', said);
        Assert.DoesNotContain('\r', said);
    }
}
