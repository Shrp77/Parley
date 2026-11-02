using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console says what is waiting for somebody who is not currently seated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner, an hour after 0.29.0 was installed.</b> Four requests went out from a seat, two
/// agents answered, and the console said nothing at all — because the upgrade had restarted the broker, which
/// takes every attachment with it, so by the time they looked they held no seat. The inbox went further than
/// silence: it said <i>"You are seated nowhere, so nothing can be waiting"</i>, which is not a missing feature
/// but a false sentence. Two Results were waiting at that moment.
/// </para>
/// <para>
/// <b>The confusion it rested on is worth naming, because the rest of the console gets it right.</b> A seat is
/// how mail is <em>collected</em>; it is not where mail <em>waits</em>. Messages queue against the agent name in
/// the broker's store, and releasing a seat moves none of them. The two ordinary ways to hold no seat — the idle
/// sweep twenty minutes after somebody walks away, and a broker restart — are precisely the times something is
/// most likely to have arrived while nobody was looking.
/// </para>
/// <para>
/// These drive the pages against a broker that answers only <c>Waiting</c>, so what is asserted is what a person
/// would see rather than what the source says.
/// </para>
/// <para>
/// <b>These once carried a typed name and a Join page; neither exists.</b> There is no typed name, no cookie and no Join:
/// the broker provides <c>human-user@&lt;project&gt;</c> and every page takes it. What a broker answering only
/// <c>Waiting</c> now stands for is a console that could not attach as the person — so these assert the count is still
/// said, by the name §12.3 gives the person, and that nothing marks the person present.
/// </para>
/// </remarks>
[Integration]
public sealed class AwayFromTheSeatTests
{
    private static readonly Uri InboxUri = new("/Inbox", UriKind.Relative);

    /// <summary>A client of the console. There is no name to carry.</summary>
    private static HttpClient Seated(WaitingConsole console) => console.CreateClient();

    [Fact]
    public async Task The_inbox_never_claims_that_nothing_can_be_waiting()
    {
        // The sentence itself, gone from every state this page can be in. Asserted separately from the count
        // below because it was wrong even when the count happened to be zero: it stated a rule, and the rule
        // was untrue.
        using var console = new WaitingConsole();

        foreach (var (messages, unreachable) in new[] { (0, false), (2, false), (0, true) })
        {
            console.Broker.Messages = messages;
            console.Broker.Unreachable = unreachable;

            using var client = Seated(console);
            var page = await client.GetStringAsync(InboxUri, TestContext.Current.CancellationToken);

            Assert.DoesNotContain("nothing can be waiting", page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task What_is_waiting_while_not_connected_is_counted()
    {
        // The owner's own case: two Results waiting while nothing held the seat.
        using var console = new WaitingConsole();
        console.Broker.Messages = 2;

        using var client = Seated(console);
        var page = await client.GetStringAsync(InboxUri, TestContext.Current.CancellationToken);

        Assert.Contains("2", page, StringComparison.Ordinal);
        Assert.Contains("waiting for you", page, StringComparison.Ordinal);
        Assert.True(console.Broker.Asked > 0, "the count was never asked for");
    }

    [Fact]
    public async Task Nothing_waiting_is_said_as_nothing_rather_than_as_silence()
    {
        using var console = new WaitingConsole();
        console.Broker.Messages = 0;

        using var client = Seated(console);
        var page = await client.GetStringAsync(InboxUri, TestContext.Current.CancellationToken);

        Assert.Contains("nothing is waiting", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_home_pages_do_not_wait_for_the_count_and_give_it_when_asked()
    {
        // The Projects and Agents pages are drawn before any broker is asked, so the frame leaves the badge empty and the
        // page asks for it afterwards.
        using var console = new WaitingConsole();
        console.Broker.Messages = 3;

        using var client = Seated(console);
        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var said = await client.GetStringAsync(new Uri("/?handler=Presence", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("data-inbox-badge", page, StringComparison.Ordinal);
        Assert.Contains("\"waiting\":3", said, StringComparison.Ordinal);
        Assert.Contains("\"certain\":true", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_could_not_be_asked_is_not_reported_as_nothing()
    {
        // "None" and "could not ask" are different facts and only one of them means stop looking — the rule the
        // frame's own count has been held to since it was written, applied here to the page underneath it.
        using var console = new WaitingConsole();
        console.Broker.Unreachable = true;

        using var client = Seated(console);
        var page = await client.GetStringAsync(InboxUri, TestContext.Current.CancellationToken);

        Assert.Contains("could not be asked", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing is waiting", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_frame_shows_the_count_while_not_connected()
    {
        // On every page rather than only the inbox: somebody who has been away lands wherever they left off, and
        // a count they have to go looking for is one that did not tell them anything.
        using var console = new WaitingConsole();
        console.Broker.Messages = 3;

        using var client = Seated(console);
        var page = await client.GetStringAsync(new Uri("/Projects/Agents?project=alpha", UriKind.Relative), TestContext.Current.CancellationToken);

        var chip = page[page.IndexOf("<a class=\"seat-who\"", StringComparison.Ordinal)..];

        Assert.Contains("3", chip[..300], StringComparison.Ordinal);
        Assert.Contains("seat-waiting", chip[..300], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_count_shown_while_not_connected_marks_nobody_present()
    {
        // The line this must not cross. A count is a reason to come back; it is never a claim that somebody is here.
        // A page render does try to take the seat — being at the console is being here — but when it
        // cannot, the mark that says "here" must not be drawn beside the count.
        using var console = new WaitingConsole();
        console.Broker.Messages = 1;

        using var client = Seated(console);
        var page = await client.GetStringAsync(InboxUri, TestContext.Current.CancellationToken);

        // Asserted on the rendered element rather than on the class name: the stylesheet names `seat-dot` on every
        // page, so searching the whole document finds the rule and proves nothing.
        Assert.DoesNotContain("<span class=\"seat-dot\"", page, StringComparison.Ordinal);
        Assert.True(console.Broker.Asked > 0, "the console never asked how much was waiting");
    }
}
