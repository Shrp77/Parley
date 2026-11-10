using System.Net;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That a placed image is served to a browser only as an image and only when its bytes are one, that Media lists a
/// conversation's files newest first, and that the transcript draws a placement where it was placed (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// The serving is the reduction in the control, so each refusal is checked by its status and by what was not served:
/// a file named <c>.png</c> holding a page must never come back with a body.
/// </remarks>
[Integration]
public sealed class ImagesInTheConversationTests : IDisposable
{
    private const string Conversation = "pricing";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static readonly byte[] Page = "<html><script>alert(1)</script></html>"u8.ToArray();

    private readonly FilesConsole _console = new();

    public ImagesInTheConversationTests()
    {
        _console.Broker.Files.Add(View("mockup.png", Png, "2026-09-14T09:00:00Z"));
        _console.Broker.Contents["mockup.png"] = Png;
        _console.Broker.Files.Add(View("fake.png", Page, "2026-09-14T10:00:00Z"));
        _console.Broker.Contents["fake.png"] = Page;
        _console.Broker.Files.Add(View("notes.md", "# notes"u8.ToArray(), "2026-09-14T11:00:00Z"));
        _console.Broker.Contents["notes.md"] = "# notes"u8.ToArray();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _console.Dispose();

    private static Wire.SharedFileView View(string name, byte[] bytes, string placed, long? size = null) => new()
    {
        ConversationId = Conversation,
        Folder = Conversation,
        Name = name,
        SizeBytes = size ?? bytes.Length,
        Sha256 = ContentHash.Of(bytes).Value,
        PlacedBy = "billing@alpha",
        PlacedUtc = placed,
    };

    private static Uri Image(string name, string sha, bool large = false) => new(
        $"/Conversations/Media?handler=Image&project={FilesConsole.Project}&id={Conversation}&name={name}&sha={sha}{(large ? "&large=true" : string.Empty)}",
        UriKind.Relative);

    [Fact]
    public async Task An_image_is_served_as_an_image_under_a_policy_that_lets_nothing_run()
    {
        using var client = _console.CreateClient();

        using var response = await client.GetAsync(Image("mockup.png", ContentHash.Of(Png).Value), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("sandbox", policy, StringComparison.Ordinal);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task A_file_named_as_an_image_whose_bytes_are_a_page_is_never_served()
    {
        using var client = _console.CreateClient();

        using var response = await client.GetAsync(Image("fake.png", ContentHash.Of(Page).Value), Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.DoesNotContain("<script>", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_file_a_stale_hash_and_a_missing_file_are_not_found_and_a_text_file_is_never_read()
    {
        using var client = _console.CreateClient();

        using var text = await client.GetAsync(Image("notes.md", ContentHash.Of("# notes"u8.ToArray()).Value), Ct);
        using var stale = await client.GetAsync(Image("mockup.png", new string('c', 64)), Ct);
        using var missing = await client.GetAsync(Image("gone.png", new string('d', 64)), Ct);

        Assert.Equal(HttpStatusCode.NotFound, text.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain(_console.Broker.Read, r => r.Name == "notes.md");
    }

    [Fact]
    public async Task An_image_over_the_inline_limit_is_served_only_when_asked_for()
    {
        var big = new byte[Png.Length];
        Png.CopyTo(big, 0);
        _console.Broker.Files.Add(View("big.png", big, "2026-09-14T12:00:00Z", size: Parley.Client.Files.Placements.InlineLimitBytes + 1));
        _console.Broker.Contents["big.png"] = big;

        using var client = _console.CreateClient();

        using var refused = await client.GetAsync(Image("big.png", ContentHash.Of(big).Value), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Empty(await refused.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task An_image_over_the_inline_limit_has_no_thumbnail_and_offers_to_show_it_anyway()
    {
        // Without the guard an image over the inline limit was drawn as a thumbnail the console refuses to serve, a broken
        // picture with no way to see it (review, lens 3 round 35). Its size is the placement's record, so no large file is
        // written.
        var big = new byte[Png.Length];
        Png.CopyTo(big, 0);
        _console.Broker.Files.Add(View("big.png", big, "2026-09-14T12:00:00Z", size: Parley.Client.Files.Placements.InlineLimitBytes + 1));
        _console.Broker.Contents["big.png"] = big;

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(
            new Uri($"/Conversations/Media?project={FilesConsole.Project}&id={Conversation}", UriKind.Relative), Ct);

        var cards = Regex.Matches(body, "<figure class=\"placed-card\">.*?</figure>", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Value)
            .ToList();
        var card = Assert.Single(cards, c => c.Contains("big.png", StringComparison.Ordinal));
        var small = Assert.Single(cards, c => c.Contains("mockup.png", StringComparison.Ordinal));

        Assert.DoesNotContain("<img", card, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"thumb\"", card, StringComparison.Ordinal);
        Assert.Contains("class=\"thumb-tile\"", card, StringComparison.Ordinal);
        Assert.Contains("larger than is shown here.", card, StringComparison.Ordinal);
        Assert.Matches(new Regex("<a href=\"[^\"]*large=true[^\"]*\"[^>]*>Show anyway</a>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), card);

        // And one under the limit still has its thumbnail, so the tile is the limit's doing.
        Assert.Contains("<img", small, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_lists_images_and_other_files_newest_first_with_a_way_back_to_each()
    {
        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(
            new Uri($"/Conversations/Media?project={FilesConsole.Project}&id={Conversation}", UriKind.Relative), Ct);

        var grid = body[body.IndexOf("class=\"media-grid\"", StringComparison.Ordinal)..];

        // fake.png was placed after mockup.png, so it comes first.
        Assert.True(grid.IndexOf("fake.png", StringComparison.Ordinal) < grid.IndexOf("mockup.png", StringComparison.Ordinal));
        Assert.Contains("class=\"media-list\"", body, StringComparison.Ordinal);
        Assert.Contains("notes.md", body[body.IndexOf("class=\"media-list\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("#f-MOCKUP.PNG\"", body, StringComparison.Ordinal);
        Assert.Contains("loading=\"lazy\"", body, StringComparison.Ordinal);

        // Listing reads no file's bytes: thumbnails are fetched by the browser, one at a time, as they come into view.
        Assert.Empty(_console.Broker.Read);
    }

    [Fact]
    public async Task A_conversation_with_no_files_says_so()
    {
        _console.Broker.Files.Clear();

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(
            new Uri($"/Conversations/Media?project={FilesConsole.Project}&id={Conversation}", UriKind.Relative), Ct);

        Assert.Contains("There are no files in this conversation.", body, StringComparison.Ordinal);
    }
}

/// <summary>That a conversation's transcript draws each placed file between the messages around its placement.</summary>
[Integration]
public sealed class PlacementsInTheTranscriptTests
{
    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    [Fact]
    public async Task A_placement_sits_after_the_message_before_it_and_one_placed_last_sits_at_the_end()
    {
        using var console = new TranscriptConsole();

        // Every scripted message is at 10:00, so one placed at 09:00 comes before them and one at 11:00 after.
        console.Broker.Request(1, "orders@test", "billing@test", "What does it look like?");
        console.Broker.Files.Add(new Wire.SharedFileView
        {
            ConversationId = "shared", Folder = "shared", Name = "before.png", SizeBytes = 10, Sha256 = new string('a', 64),
            PlacedBy = "billing@test", PlacedUtc = "2026-09-16T09:00:00Z",
        });
        console.Broker.Files.Add(new Wire.SharedFileView
        {
            ConversationId = "shared", Folder = "shared", Name = "after.png", SizeBytes = 10, Sha256 = new string('b', 64),
            PlacedBy = "billing@test", PlacedUtc = "2026-09-16T11:00:00Z",
        });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var before = page.IndexOf("id=\"f-BEFORE.PNG\"", StringComparison.Ordinal);
        var message = page.IndexOf("id=\"m1\"", StringComparison.Ordinal);
        var after = page.IndexOf("id=\"f-AFTER.PNG\"", StringComparison.Ordinal);

        Assert.True(before >= 0 && message > before && after > message, $"before {before}, message {message}, after {after}");
        Assert.Contains("handler=Image", page, StringComparison.Ordinal);
        Assert.Contains("id=\"viewer\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_a_file_started_shows_the_file_and_never_says_nothing_was_recorded()
    {
        // Without the guard a conversation that holds only a placed file said nothing was recorded under its name, above
        // the file it holds (review, lens 3 round 35).
        using var console = new TranscriptConsole();

        console.Broker.Files.Add(new Wire.SharedFileView
        {
            ConversationId = "shared", Folder = "shared", Name = "first.png", SizeBytes = 10, Sha256 = new string('e', 64),
            PlacedBy = "billing@test", PlacedUtc = "2026-09-16T09:00:00Z",
        });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("id=\"f-FIRST.PNG\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was recorded under this identifier.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_placed_on_a_later_day_sits_under_that_days_heading()
    {
        using var console = new TranscriptConsole();

        // The scripted message is on 16 September; the file a day later. Under 16 September's heading its card's time
        // alone would say it was placed that day.
        console.Broker.Request(1, "orders@test", "billing@test", "What does it look like?");
        console.Broker.Files.Add(new Wire.SharedFileView
        {
            ConversationId = "shared", Folder = "shared", Name = "later.png", SizeBytes = 10, Sha256 = new string('c', 64),
            PlacedBy = "billing@test", PlacedUtc = "2026-09-17T15:00:00Z",
        });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var placedOn = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse("2026-09-17T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.Local);
        var heading = page.IndexOf(">" + placedOn.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture) + "<", StringComparison.Ordinal);
        var file = page.IndexOf("id=\"f-LATER.PNG\"", StringComparison.Ordinal);
        var message = page.IndexOf("id=\"m1\"", StringComparison.Ordinal);

        Assert.True(message >= 0 && heading > message && file > heading, $"message {message}, heading {heading}, file {file}");
    }

    [Fact]
    public void The_page_script_keeps_a_day_that_holds_only_a_file()
    {
        // The filter script hides a day heading with nothing kept under it, and a placed file did not count, so the heading
        // the server draws for a file's own day was hidden in the browser and the file sat under the day before. Checked
        // in a headless browser (2026-11-09: without this line both such headings were hidden; with it, neither); held here
        // by the line itself, since this suite runs no script.
        // Held as strictly as text allows: comments stripped, and the line must be a statement of the loop that decides
        // which day headings show, after the statement that hides what follows a hidden turn. What text cannot hold is a
        // branch that never runs around it; that was checked in the browser.
        var page = File.ReadAllText(Path.Combine(Root(), "src", "Parley.Web", "Pages", "Conversations", "Detail.cshtml"));
        var code = Regex.Replace(page, @"/\*[\s\S]*?\*/|(?<![:""'])//[^
]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(2));
        var start = code.IndexOf("function run()", StringComparison.Ordinal);
        var loop = code.IndexOf("items.forEach(function (li) {", start, StringComparison.Ordinal);
        var end = code.IndexOf("closeDay();", code.IndexOf("});", code.IndexOf("li.classList.toggle(\"filtered-out\", !lastTurn);", loop, StringComparison.Ordinal), StringComparison.Ordinal), StringComparison.Ordinal);
        var body = start >= 0 && loop > start && end > loop ? code[loop..end] : string.Empty;
        var hides = body.IndexOf("li.classList.toggle(\"filtered-out\", !lastTurn);", StringComparison.Ordinal);
        var keeps = body.IndexOf("if (lastTurn && li.classList.contains(\"placed\")) { dayHasOne = true; }", StringComparison.Ordinal);

        Assert.True(hides >= 0 && keeps > hides, $"the day-keeping statement is not live in run()'s loop (hides at {hides}, keeps at {keeps})");
    }

    private static string Root()
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory!;
    }

    [Fact]
    public async Task A_file_placed_between_messages_on_a_day_of_its_own_starts_that_day()
    {
        using var console = new TranscriptConsole();

        // 16 September, then the file on the 17th, then a message on the 18th: the file is drawn before the 18th's
        // message, inside the loop over messages, and needs the 17th's heading there.
        console.Broker.Request(1, "orders@test", "billing@test", "What does it look like?");
        console.Broker.Request(2, "billing@test", "orders@test", "Like this.");
        console.Broker.Messages[^1].CreatedUtc = "2026-09-18T15:00:00Z";
        console.Broker.Files.Add(new Wire.SharedFileView
        {
            ConversationId = "shared", Folder = "shared", Name = "between.png", SizeBytes = 10, Sha256 = new string('d', 64),
            PlacedBy = "billing@test", PlacedUtc = "2026-09-17T15:00:00Z",
        });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var placedOn = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse("2026-09-17T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.Local);
        var heading = page.IndexOf(">" + placedOn.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture) + "<", StringComparison.Ordinal);
        var file = page.IndexOf("id=\"f-BETWEEN.PNG\"", StringComparison.Ordinal);
        var later = page.IndexOf("id=\"m2\"", StringComparison.Ordinal);

        Assert.True(heading >= 0 && file > heading && later > file, $"heading {heading}, file {file}, later message {later}");
    }
}

/// <summary>That the composer's To line is where chips are drawn, and that a chip only ever changes a box.</summary>
/// <remarks>
/// The chips are the page script's, so this holds the contract the script keeps rather than a browser's behaviour: the line
/// the chips go in exists in the composer, a chip's remove control is a button named for its agent, and removing one
/// unticks the box the form sends, so what is sent is what the boxes say.
/// </remarks>
[Integration]
public sealed class RecipientChipsTests
{
    [Fact]
    public async Task The_to_line_draws_a_named_remove_button_per_agent_that_unticks_its_box()
    {
        using var console = new TranscriptConsole();
        console.Broker.Request(1, "orders@test", "billing@test", "Gross or net?");

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var composer = page[page.IndexOf("id=\"composer\"", StringComparison.Ordinal)..];

        Assert.Contains("<p class=\"composer-to\" id=\"to-summary\"", composer, StringComparison.Ordinal);
        Assert.Contains("x.setAttribute(\"aria-label\", \"Remove \" + name);", page, StringComparison.Ordinal);
        Assert.Contains("x.type = \"button\";", page, StringComparison.Ordinal);
        Assert.Contains("b.dispatchEvent(new Event(\"change\", { bubbles: true }));", page, StringComparison.Ordinal);
    }
}
