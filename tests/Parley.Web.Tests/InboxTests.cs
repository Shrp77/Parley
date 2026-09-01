using System.Net;
using Parley.Core;
using Parley.Web.Seating;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// A person's inbox: what it shows freely, and what it makes them ask for.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing distinction is between looking and taking. SPEC.md §6 runs the working
/// window from delivery and records delivery by the inbox drain, so a page that drained while
/// rendering would start a four-hour clock for somebody who followed a link — and §14 would
/// ghost what they did not get to. The count is free; the contents are not.
/// </para>
/// <para>
/// Reading and answering shipped together on purpose. An inbox that could take a question and
/// not answer it would guarantee a ghost four hours later, every time, so the half would have
/// made the deployment worse rather than merely unfinished.
/// </para>
/// </remarks>
public sealed class InboxTests
{
    private static string Page =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Inbox.cshtml"));

    private static string Model =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Inbox.cshtml.cs"));

    [Fact]
    public void Opening_the_inbox_does_not_take_delivery()
    {
        // The GET reads counts and nothing else. Draining from OnGet would be indistinguishable
        // on the page and would start everybody's clock the moment they glanced at it.
        var get = Model[Model.IndexOf("OnGetAsync()", StringComparison.Ordinal)..];
        var body = get[..get.IndexOf("public async Task<IActionResult> OnPostTake", StringComparison.Ordinal)];

        Assert.DoesNotContain("Drain", body, StringComparison.Ordinal);

        // And the thing it does call is the one documented as changing nothing.
        Assert.Contains("WaitingAsync", Model, StringComparison.Ordinal);
    }

    [Fact]
    public void Taking_delivery_is_a_post_that_says_what_it_costs()
    {
        Assert.Contains("asp-page-handler=\"Take\"", Page, StringComparison.Ordinal);
        Assert.Contains("OnPostTakeAsync", Model, StringComparison.Ordinal);

        // Described before it happens, like every other operation in this console that changes
        // something. "You cannot put them back" is the part somebody needs before clicking.
        Assert.Contains("starts the clock", Page, StringComparison.Ordinal);
        Assert.Contains("cannot put them back", Page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inbox_sends_nobody_who_has_not_joined()
    {
        // There is no inbox without a seat: the messages are addressed to an agent, and somebody
        // who has not joined is not one.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri("/Inbox", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Join", response.Headers.Location?.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_debt_cannot_be_dismissed_and_a_notice_can()
    {
        // Clearing removes what owes nothing back. A question that could be cleared would let
        // somebody tidy the page while the exchange behind it ran on to be ghosted, which is
        // the one outcome the inbox exists to prevent.
        var hold = File.ReadAllText(RepositoryPath("src/Parley.Web/Seating/SeatHold.cs"));

        Assert.Contains("RemoveAll(one => !one.Owes)", hold, StringComparison.Ordinal);

        // And the only thing that clears a debt is answering it.
        Assert.Contains("RemoveAll(one => one.Message.ExchangeId == exchangeId)", hold,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessageType.Question, 7L, true)]
    [InlineData(MessageType.Clarify, 7L, true)]
    [InlineData(MessageType.Answer, 7L, false)]
    [InlineData(MessageType.Notify, 7L, false)]
    [InlineData(MessageType.Ghost, 7L, false)]
    [InlineData(MessageType.Question, null, false)]
    public void What_is_owed_back_is_a_question_inside_an_exchange(
        MessageType type, long? exchange, bool owes)
    {
        // An answer form offered for a message nobody may answer is an invitation to a refusal,
        // and a question shown without one is a debt with no way to pay it.
        var delivered = new Delivered(
            new Message(
                1, exchange, type,
                AgentName.Create("orders@alpha"), AgentName.Create("arvind@alpha"),
                "claude-opus-5", "{}", "c", DateTimeOffset.UnixEpoch, null));

        Assert.Equal(owes, delivered.Owes);
    }

    [Fact]
    public void The_sub_questions_answered_are_the_ones_the_broker_says_were_asked()
    {
        // Read from the broker rather than parsed out of the payload the page just rendered.
        // §5.3 answers by identifier, and a form built from this process's own reading would be
        // a second opinion — the one that disagrees produces an answer resolving a sub-question
        // nobody asked, which is stored and closes the exchange.
        Assert.Contains("SubQuestionsAsync", Model, StringComparison.Ordinal);

        var voice = File.ReadAllText(RepositoryPath("src/Parley.Web/Seating/SeatVoice.cs"));

        Assert.Contains("GetSubQuestionIdsAsync", voice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_aggregate_takes_the_least_resolved_of_the_parts()
    {
        // §5.3. Rolled up rather than chosen, because a person who could pick it separately
        // could record a question as settled while leaving a sub-question unknown — and §15
        // counts that.
        var voice = File.ReadAllText(RepositoryPath("src/Parley.Web/Seating/SeatVoice.cs"));
        var rollUp = voice[voice.IndexOf("private static Wire.Resolution RollUp", StringComparison.Ordinal)..];

        Assert.True(
            rollUp.IndexOf("Unknown", StringComparison.Ordinal)
            < rollUp.IndexOf("OutOfScope", StringComparison.Ordinal),
            "unknown must win over out_of_scope, or an unresolved answer reads as a scoped one");
    }

    [Fact]
    public void Ghosted_is_not_an_outcome_a_person_may_choose()
    {
        // §5.3 reserves it for the broker recording an exchange nobody returned to. An agent
        // able to write it over its own unanswered work would be manufacturing the evidence
        // §15 counts to find an unhealthy deployment — which is a defect this repository has
        // already had once.
        Assert.DoesNotContain("\"ghosted\"",
            File.ReadAllText(RepositoryPath("src/Parley.Web/Seating/SeatVoice.cs")),
            StringComparison.Ordinal);

        Assert.Equal(3, SeatVoice.Outcomes.Count);
        Assert.DoesNotContain(SeatVoice.Outcomes, o => o.Value.Contains("ghost", StringComparison.Ordinal));
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
        var hold = File.ReadAllText(RepositoryPath("src/Parley.Web/Seating/SeatHold.cs"));

        Assert.DoesNotContain("MessagesWaiting { get;", hold, StringComparison.Ordinal);

        var layout = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

        Assert.Contains("Seats.WaitingAsync(here", layout, StringComparison.Ordinal);

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
