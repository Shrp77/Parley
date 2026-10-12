using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That an open transcript follows its conversation, as the terminal's does: a changed conversation is read again
/// whole, an unchanged one is recognised as unchanged, and following neither keeps a seat nor opens a way for a
/// payload into the page's markup.
/// </summary>
/// <remarks>
/// The script that moves the region in is not run here — this project has no browser — so these hold the server's
/// half to what the script relies on: the page and the handler render one region with one digest, the digest moves
/// exactly when the region does, and a failed read is a failure rather than an empty region.
/// </remarks>
public sealed partial class FollowingTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";

    private static readonly Uri FollowUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared&handler=Follow", UriKind.Relative);

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    [Fact]
    public async Task A_page_and_a_follow_render_the_same_region_under_the_same_digest()
    {
        // The first read a following page makes must change nothing. If the page and the handler rendered the region
        // differently, every page would replace its transcript on its first read, moving whatever a reader had
        // selected or found, for no change at all.
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var follow = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.Equal(Seen(page), Seen(follow));
        Assert.Contains("Reconcile the September invoices.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_that_has_not_moved_reads_as_unchanged()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var first = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);
        var second = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.Equal(Seen(first), Seen(second));
        Assert.Equal(2, console.Broker.Exports);
    }

    [Fact]
    public async Task A_message_arriving_is_in_the_next_read_under_a_new_digest()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        console.Broker.Request(2, Billing, Orders, "Which ledger is authoritative for refunds?");

        var after = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.NotEqual(Seen(before), Seen(after));
        Assert.DoesNotContain("Which ledger is authoritative", before, StringComparison.Ordinal);
        Assert.Contains("Which ledger is authoritative", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_earlier_exchange_closing_moves_the_digest_though_no_message_arrived()
    {
        // Why the whole region is read rather than the messages after the newest one the page has: a person closing
        // an exchange changes a message already on screen, and a follow that only appended would never show it.
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        console.Broker.Exchanges[0] = new Wire.ExchangeSnapshot
        {
            ExchangeId = 1,
            State = Wire.ExchangeState.Closed,
            OpenedUtc = "2026-09-16T10:00:00Z",
            ClosedUtc = "2026-09-16T10:05:00Z",
            Outcome = Wire.ExchangeOutcome.Closed
        };

        var after = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.NotEqual(Seen(before), Seen(after));
        Assert.Contains("closed by a person", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_arrives_in_a_follow_as_text_and_cannot_forge_the_region()
    {
        // The script parses what a follow returns and moves it into the page, so what it returns is markup the page
        // trusts. A peer's words must reach it encoded, and must not be able to supply a second region carrying a
        // digest of the peer's choosing -- the script takes the first element with the region's id.
        using var console = new TranscriptConsole();
        console.Broker.Request(
            1, Orders, Billing, "<script>alert(1)</script><div id=\"followed\" data-seen=\"forged\">");
        using var client = console.CreateClient();

        var follow = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<script>", follow, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", follow, StringComparison.Ordinal);
        Assert.Single(RegionOpening().Matches(follow));
        Assert.DoesNotContain("data-seen=\"forged\"", follow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_has_no_frame_so_it_cannot_keep_a_seat()
    {
        // The frame is the one place a seat is touched, and a touched seat is not released while idle. A tab
        // following a conversation is not a person at the console, so a follow must never render the frame.
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var follow = await client.GetStringAsync(FollowUri, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Skip to content", follow, StringComparison.Ordinal);
        Assert.DoesNotContain("<header", follow, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", follow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_the_frame_touches_a_seat()
    {
        // The other half of the check above. A follow without a frame keeps no seat only while the frame is the one
        // thing that touches one; a handler that touched a seat itself would pass the test above and still hold it.
        var root = Repository.Path("src/Parley.Web");

        var touching = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".cshtml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(".Touch(", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToList();

        // One file touches a seat, and it is not the frame: `PersonSeats` takes the person's seat and
        // marks them here, and the frame and the pages that speak call it. The half this test exists for is the follow,
        // which is a timer rather than a person and must reach none of it.
        Assert.Equal(["PersonSeats.cs"], touching);

        // Line endings normalised first: a Windows checkout with core.autocrlf makes this file CRLF, and the search for the
        // handler's closing brace below would then find nothing.
        var detail = File.ReadAllText(Repository.Path("src/Parley.Web/Pages/Conversations/Detail.cshtml.cs"))
            .ReplaceLineEndings("\n");
        var follow = detail[detail.IndexOf("OnGetFollowAsync()", StringComparison.Ordinal)..];

        follow = follow[..follow.IndexOf("\n    }\n", StringComparison.Ordinal)];

        Assert.DoesNotContain("PersonSeats", follow, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureAsync", follow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_the_broker_did_not_answer_is_a_failure_and_not_an_empty_region()
    {
        // A 200 with nothing in it would replace a conversation with nothing because of a moment's trouble reaching
        // the broker. The page keeps what it shows on a failure and says it may be out of date.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri($"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=shared&handler=Follow", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_follow_of_a_project_this_console_does_not_know_is_not_found()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(
            new Uri("/Conversations/Detail?project=nowhere&id=shared&handler=Follow", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_page_names_where_to_follow_and_what_to_say_when_it_cannot()
    {
        // Without a script the page is the conversation as read, stamped with when. With one, it needs the address
        // to ask and a place to say a read failed; a handler answering honestly into a page with nowhere to put the
        // answer would change nothing anybody sees.
        using var console = new TranscriptConsole();
        console.Broker.Request(1, Orders, Billing, "Reconcile the September invoices.");
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("data-follow=\"/Conversations/Detail?project=alpha&amp;id=shared&amp;handler=Follow\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"uncertain\"", page, StringComparison.Ordinal);
        Assert.Contains("may be out of date", page, StringComparison.Ordinal);
    }

    private static string Seen(string html)
    {
        var found = Digest().Match(html);

        Assert.True(found.Success, "the region carries no digest");

        return found.Groups[1].Value;
    }

    [GeneratedRegex("data-seen=\"([0-9a-f]{64})\"")]
    private static partial Regex Digest();

    [GeneratedRegex("id=\"followed\"")]
    private static partial Regex RegionOpening();

    private static class Repository
    {
        public static string Path(string relative)
        {
            var directory = AppContext.BaseDirectory;

            while (directory is { Length: > 0 } && !File.Exists(System.IO.Path.Combine(directory, "Parley.slnx")))
            {
                directory = System.IO.Path.GetDirectoryName(directory);
            }

            Assert.NotNull(directory);

            return System.IO.Path.Combine(directory!, relative);
        }
    }
}
