using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a transcript says who could see a message and whether it has been collected, and that the
/// two are not confusable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both concern a broadcast.</b> Telling nineteen agents one thing — nineteen pairwise
/// notifications in one conversation — must not put on every message a line naming the other eighteen
/// participants, four hundred characters of it, under a header that has already said who the
/// message is for. It reads as the recipients not having received it.
/// </para>
/// <para>
/// <b>Two different facts were being read as one.</b> Who <i>could</i> see a message is fixed when
/// it is written and is a statement about §5.0's pairwise rule; whether the recipient has
/// <i>collected</i> it is a fact about this deployment now, and the transcript did not carry it at
/// all. An agent can be attached, running, and idle with mail waiting.
/// </para>
/// </remarks>
[Integration]
public sealed class SeenAndCollectedTests
{
    private static Message Notice(string to, DateTimeOffset? delivered = null) =>
        new(
            1,
            1,
            MessageType.Request,
            AgentName.Create("arvind@test"),
            AgentName.Create(to),
            "human:arvind",
            "{\"ask\":\"the cap moves on Friday\"}",
            "tell-everybody-test",
            DateTimeOffset.UnixEpoch,
            delivered);

    /// <summary>A broadcast: one notice to each of <paramref name="agents"/> agents.</summary>
    private static IReadOnlyList<Message> Broadcast(int agents) =>
        [.. Enumerable.Range(0, agents).Select(at => Notice($"agent-{at}@test"))];

    [Fact]
    public void The_absent_are_named_while_there_are_few_of_them()
    {
        // Unchanged, and the reason is `ExportTests.The_transcript_names_the_agent_that_was_left
        // _out`: "one participant did not see this" is unactionable, and the reader cannot recover
        // the name from the pair alone.
        var messages = Broadcast(4);
        var participants = Visibility.Participants(messages);

        var said = Visibility.Describe(messages[0], participants);

        Assert.Contains("agent-1@test", said, StringComparison.Ordinal);
        Assert.Contains("agent-3@test", said, StringComparison.Ordinal);
        Assert.EndsWith("did not see this.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("others", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broadcast_does_not_put_the_whole_roster_under_every_message()
    {
        // Nineteen agents and a seat. Eighteen names per message is longer than
        // everything it annotates, and the screen cuts it off part way through.
        var messages = Broadcast(19);
        var participants = Visibility.Participants(messages);

        var said = Visibility.Describe(messages[0], participants);

        Assert.True(
            said.Length < 120,
            $"the annotation is {said.Length} characters: {said}");

        Assert.Contains("and 14 others in this conversation did not see this.", said, StringComparison.Ordinal);

        // Some are still named, so the note is not the unactionable "one participant" form.
        // Ordinal order, which puts `agent-10` before `agent-1@`: the four named are whichever
        // sort first, and which four they are is not the point.
        Assert.Contains("agent-10@test", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recipient_is_never_among_the_absent()
    {
        // The misreading this caused: a wall of names that holds eighteen of the nineteen agents
        // somebody has just told reads as "the agents did not get it". The one it was addressed to
        // is not in the list and never was — which is what the header above it says.
        var messages = Broadcast(19);
        var participants = Visibility.Participants(messages);

        foreach (var message in messages)
        {
            Assert.DoesNotContain(
                message.To,
                Visibility.NotSeenBy(message, participants));

            Assert.DoesNotContain(
                message.From,
                Visibility.NotSeenBy(message, participants));
        }
    }

    [Fact]
    public void A_message_says_whether_it_has_been_collected()
    {
        // The fact no line of the transcript carries. §4.2 has
        // the agent take delivery of its inbox; until it does, `delivered_utc` is unset.
        Assert.Equal(Delivery.Waiting, Delivery.Of(Notice("orders@test")));

        var collected = Delivery.Of(
            Notice("orders@test", DateTimeOffset.UnixEpoch.AddHours(2)), TimeZoneInfo.Utc);

        Assert.StartsWith("collected ", collected, StringComparison.Ordinal);
        Assert.DoesNotContain(Delivery.Waiting, collected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_header_is_a_time_a_sender_and_a_recipient_and_nothing_else()
    {
        // **The header carries no message type, exchange state or collected flag.**
        // "Humans just talk; we do not begin by saying 'Inquiry' and 'Response'."
        //
        // Collected was the worst of the three here. It means the recipient drained its inbox, which for a
        // person is a key they press -- so an answer somebody was reading on screen said "not collected yet"
        // and read as a fault in the deployment rather than as a fact about a keystroke.
        var lines = Reading.Lines([Notice("orders@test")], TimeZoneInfo.Utc);
        var header = lines.First(line => line.Kind == LineKind.Header);

        Assert.Contains("arvind@test -> orders@test", header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Delivery.Waiting, header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("open", header.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void And_nothing_on_screen_says_who_was_not_party_to_a_message()
    {
        // The list of everyone who did not see a broadcast is
        // superfluous beside a header that already names one sender and one recipient -- and beside a room
        // column that lists every participant. The export keeps it, which is where it earns its keep: a file
        // read somewhere else has no column beside it.
        var lines = Reading.Lines([Notice("orders@test")], TimeZoneInfo.Utc);

        Assert.DoesNotContain(lines, line => line.Kind == LineKind.Annotation);
    }

    [Fact]
    public void The_export_carries_it_too()
    {
        // One rendering of one conversation: a file that said nothing about delivery while the
        // screen did would be the two disagreeing about what is known.
        var written = Transcript.Write("tell-everybody-test", "alpha", [Notice("orders@test")], []);

        Assert.Contains(Delivery.Waiting, written, StringComparison.Ordinal);
    }
}
