using Parley.Core;
using Parley.Client.Seating;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// A person's inbox: what it shows freely, and what it makes them ask for.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is recorded by the inbox drain, and what is delivered is what the flags of §15 measure progress from.
/// Showing a message to the person is what counts it as picked up, so the page drains as it renders, as the terminal
/// does on opening a conversation.
/// </para>
/// <para>
/// Reading and resolving shipped together on purpose. An inbox that could take a Request and not
/// send its Result would leave work collected and owed with no way to report it, so the half would
/// have made the deployment worse rather than merely unfinished.
/// </para>
/// </remarks>
public sealed class InboxTests
{
    private static string Page =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Inbox.cshtml"));

    private static string Model =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Inbox.cshtml.cs"));

    [Fact]
    public void Showing_the_inbox_takes_delivery_and_there_is_no_button_for_it()
    {
        // Delivered as it is drawn, like the terminal. A button for it was a count that never went down.
        Assert.Contains("TakeDeliveryAsync", Model, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPostTakeAsync", Model, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page-handler=\"Take\"", Page, StringComparison.Ordinal);
        Assert.DoesNotContain("Take delivery", Page, StringComparison.Ordinal);
    }

    // `The_inbox_sends_nobody_who_has_not_joined` was here: it expected a redirect to Join. There is no
    // Join and the inbox is always about the person — `NoJoinOnTheWebTests` asserts the page answers with no name.

    [Fact]
    public void Work_owed_cannot_be_dismissed_and_a_result_can()
    {
        // Clearing removes what owes nothing back. A Request that could be cleared would let
        // somebody tidy the page while the exchange behind it stayed open with nobody working it,
        // which is the one outcome the inbox exists to prevent.
        var hold = File.ReadAllText(RepositoryPath("src/Parley.Client/Seating/SeatHold.cs"));

        Assert.Contains("RemoveAll(one => !one.Owes)", hold, StringComparison.Ordinal);

        // And the only thing that clears a debt is answering it.
        Assert.Contains("RemoveAll(one => one.Message.ExchangeId == exchangeId)", hold,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessageType.Request, true)]
    [InlineData(MessageType.Clarify, true)]
    [InlineData(MessageType.Result, false)]
    public void What_is_owed_back_is_a_request_or_a_clarification(MessageType type, bool owes)
    {
        // A Result form offered for a message nobody may answer is an invitation to a refusal,
        // and a Request shown without one is work owed with no way to report it.
        var delivered = new Delivered(
            new Message(
                1, 7, type,
                AgentName.Create("orders@alpha"), AgentName.Create("arvind@alpha"),
                "claude-opus-5", "{}", "c", DateTimeOffset.UnixEpoch, null));

        Assert.Equal(owes, delivered.Owes);
    }

    [Fact]
    public void The_items_a_result_covers_are_the_ones_the_request_carried()
    {
        // The form is built from the Request as stored, and a Result is never offered for anything else.
        var request = new Delivered(
            new Message(
                1, 7, MessageType.Request,
                AgentName.Create("orders@alpha"), AgentName.Create("arvind@alpha"), "claude-opus-5",
                """{"intent":"moving tax","items":[{"id":"r1","kind":"work","text":"a"},{"id":"q2","kind":"question","text":"b"}],"done_when":"both are resolved and said so"}""",
                "c", DateTimeOffset.UnixEpoch, null));

        Assert.Equal(["r1", "q2"], request.Items());
        Assert.Contains("item.Items()", Page, StringComparison.Ordinal);

        var result = request with { Message = request.Message with { Type = MessageType.Result } };

        Assert.Empty(result.Items());
    }

    [Fact]
    public void The_exchange_outcome_is_not_the_persons_to_choose()
    {
        // §5.3. Rolled up by the broker from the items rather than chosen, because a person who
        // could pick it separately could record work complete while leaving an item blocked — and
        // §15 counts that. So the wire has nowhere to put one.
        var fields = typeof(Parley.Transport.ResultRequest)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.Contains("PayloadJson", fields);
        Assert.DoesNotContain(fields, f => f.Contains("Outcome", StringComparison.Ordinal));
    }

    [Fact]
    public void Closed_is_not_an_outcome_a_person_may_choose()
    {
        // §5.3 reserves it for the broker closing a conversation. A participant able to write it
        // over its own open work would be forging the broker's hand in the record §15 reads.
        Assert.Equal(6, SeatVoice.Outcomes.Count);
        Assert.DoesNotContain(SeatVoice.Outcomes, o => o.Value.StartsWith("closed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delivered_message_carries_its_own_framing()
    {
        // The console does not keep §13's envelope, on the grounds that it renders the same
        // facts itself. That is only true while it does — and the envelope exists precisely
        // because an adapter that reconstructs the framing is an adapter that can forget part of
        // it. So each part is pinned here rather than left to a comment claiming it.
        foreach (var fact in new[]
        {
            "item.Message.From.Value",             // who sent it, which is the attested part
            "item.Message.Type",                   // what kind of message it is
            "item.Message.ResponderModel",         // what they claim produced it
            "item.Message.CreatedUtc",             // when
            "item.Message.ConversationId"          // and what it belongs to
        })
        {
            Assert.Contains(fact, Page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_responder_is_shown_as_something_the_sender_claims()
    {
        // A peer demonstrated the gap rather than arguing it: it sent a notice reporting its
        // responder as `human:arvind` — the seated person it was talking to — and the broker
        // stored it, because §11.3 constrains only the `parley:` prefix. The name is attested
        // and this is not, so the page may not present them alike.
        Assert.Contains("says @item.Message.ResponderModel", Page, StringComparison.Ordinal);
        Assert.Contains("Not verified", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_has_exactly_one_source()
    {
        // The fault this replaces: the frame summed counts pushed on the attach streams while
        // the page asked the broker, so after this console drained its own inbox the frame said
        // "1 waiting" beside a page correctly reporting none. A notice is sent when something
        // arrives and never again when something leaves, so a cached one only ever goes up.
        //
        // Asserted as the absence of the cache rather than as agreement between two numbers,
        // because two numbers that happen to agree today are still two numbers.
        var hold = File.ReadAllText(RepositoryPath("src/Parley.Client/Seating/SeatHold.cs"));

        Assert.DoesNotContain("MessagesWaiting { get;", hold, StringComparison.Ordinal);

        var layout = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

        Assert.Contains("PersonSeats.PresenceAsync(", layout, StringComparison.Ordinal);

        // And nowhere else renders one, so a third source cannot appear by being added.
        var pages = Directory.GetFiles(
            RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories);

        var counting = pages
            .Where(p => !string.Equals(Path.GetFileName(p), "Inbox.cshtml", StringComparison.Ordinal)
                     && !string.Equals(Path.GetFileName(p), "_Layout.cshtml", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("waiting for you", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            counting.Count == 0,
            "a second page shows a waiting count, which is a second thing that can be wrong: "
            + string.Join(", ", counting));
    }

    [Fact]
    public void A_count_nobody_could_confirm_is_not_shown_as_none()
    {
        // "No messages" and "could not ask" are different facts and only one of them means stop
        // looking. A broker that did not answer must leave the chip off rather than render a
        // zero somebody would read as an answer.
        var layout = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

        Assert.Contains("Certain: true, Waiting: > 0", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_in_the_frame_leads_to_the_messages_it_counts()
    {
        // It led to the seat page, which says where you are sitting and not what is waiting.
        // A number that promises something and goes somewhere else is worse than no number.
        var layout = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

        var chip = layout[layout.IndexOf("class=\"seat-who\"", StringComparison.Ordinal)..];

        Assert.Contains("href=\"/Inbox\"", chip[..200], StringComparison.Ordinal);
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
