using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The file problems page and the banner on every page: what sweeps found, as the broker said it, encoded, and dismissed
/// only by this page's own POST (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// Every refusal is checked by what the broker was asked, which must be nothing: a page that said nothing was dismissed
/// having asked for a dismissal is the failure that matters.
/// </remarks>
[Integration]
public sealed class FileProblemsPageTests : IDisposable
{
    private const string Page = "/Projects/FileProblems?project=" + FilesConsole.Project;

    private static readonly string Placed = new('a', 64);
    private static readonly string Found = new('b', 64);

    private readonly FilesConsole _console = new();

    public FileProblemsPageTests()
    {
        _console.Broker.Problems.Add(new Wire.FileProblemView
        {
            Id = 7,
            Kind = Wire.FileProblemKind.Quarantined,
            ConversationId = "pricing",
            Path = "pricing/terms.md",
            Sha256Placed = Placed,
            Sha256Found = Found,
            MovedTo = "pricing/terms.md.20260914T090000Z",
            Detail = "Its bytes no longer match what was placed.",
            RaisedUtc = "2026-09-14T09:00:00Z",
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _console.Dispose();

    [Fact]
    public async Task Each_problem_is_listed_with_its_conversation_path_both_hashes_and_where_it_went()
    {
        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.Contains("#7", body, StringComparison.Ordinal);
        Assert.Contains("Changed on disk, moved to quarantine", body, StringComparison.Ordinal);
        Assert.Contains("<td>pricing</td>", body, StringComparison.Ordinal);
        Assert.Contains("pricing/terms.md", body, StringComparison.Ordinal);
        Assert.Contains(Placed, body, StringComparison.Ordinal);
        Assert.Contains(Found, body, StringComparison.Ordinal);
        Assert.Contains("pricing/terms.md.20260914T090000Z", body, StringComparison.Ordinal);
        Assert.Contains("/srv/parley/alpha.db.files-quarantine", body, StringComparison.Ordinal);
        Assert.Contains("It deletes nothing on disk.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_is_encoded_as_somebody_elses_text()
    {
        _console.Broker.Problems.Add(new Wire.FileProblemView
        {
            Id = 8,
            Kind = Wire.FileProblemKind.Locked,
            ConversationId = "pricing",
            Path = "pricing/<script>alert(1)</script>.md",
            Detail = "Another program holds this.",
            RaisedUtc = "2026-09-14T09:00:00Z",
        });

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.DoesNotContain("<script>alert(1)</script>", body, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dismissing_asks_the_broker_for_that_one_and_says_nothing_on_disk_changed()
    {
        using var client = _console.CreateClient();

        var (status, body) = await PostAsync(client, "Dismiss", [("id", "7")]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(7, Assert.Single(_console.Broker.Dismissed));
        Assert.Contains("Dismissed file problem #7. Nothing on disk was changed.", Status(body), StringComparison.Ordinal);
        Assert.Contains("No file problems are waiting.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dismissed_ones_are_shown_again_when_asked_for()
    {
        _console.Broker.Problems[0].DismissedUtc = "2026-09-14T10:00:00Z";

        using var client = _console.CreateClient();

        var waiting = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);
        Assert.DoesNotContain("pricing/terms.md", waiting, StringComparison.Ordinal);

        var all = await client.GetStringAsync(new Uri(Page + "&all=true", UriKind.Relative), Ct);
        Assert.Contains("pricing/terms.md", all, StringComparison.Ordinal);
        Assert.Contains("Dismissed", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_without_this_pages_token_is_refused_and_nothing_is_dismissed()
    {
        using var client = _console.CreateClient();

        var (status, _) = await PostAsync(client, "Dismiss", [("id", "7")], withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_console.Broker.Dismissed);
    }

    [Fact]
    public async Task An_id_in_the_address_is_not_taken_for_one_the_form_left_out()
    {
        // The form posts back to its own address, and an agent's message renders as live links: an id there must not
        // choose what the person's press dismissed.
        using var client = _console.CreateClient();

        var (_, body) = await PostAsync(client, "Dismiss&id=7", []);

        Assert.Empty(_console.Broker.Dismissed);
        Assert.Contains("Nothing was dismissed.", Status(body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Projects/Files?project=" + FilesConsole.Project)]
    [InlineData("/Inbox")]
    [InlineData("/Notifications")]
    public async Task Every_page_carries_a_banner_with_the_count_linking_to_the_problems(string page)
    {
        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri(page, UriKind.Relative), Ct);

        var banner = Regex.Match(body, """<p class="file-problems-banner">\s*<a href="([^"]+)">([^<]+)</a>""", RegexOptions.None, TimeSpan.FromSeconds(2));

        Assert.True(banner.Success, "no banner on " + page);
        Assert.Equal(Page, WebUtility.HtmlDecode(banner.Groups[1].Value));
        Assert.Contains("1 file problem waiting in alpha", WebUtility.HtmlDecode(banner.Groups[2].Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_projects_page_which_waits_for_no_broker_carries_the_count_in_the_projects_row()
    {
        using var client = _console.CreateClient();

        var row = await client.GetStringAsync(new Uri("/?handler=Row&name=" + FilesConsole.Project, UriKind.Relative), Ct);

        var link = Regex.Match(row, """<a class="alarm" href="([^"]+)">([^<]+)</a>""", RegexOptions.None, TimeSpan.FromSeconds(2));

        Assert.True(link.Success, row);
        Assert.Equal(Page, WebUtility.HtmlDecode(link.Groups[1].Value));
        Assert.Equal("1 file problem waiting", link.Groups[2].Value);
    }

    [Fact]
    public async Task Nothing_waiting_puts_no_banner_on_the_page()
    {
        _console.Broker.Problems.Clear();

        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(new Uri("/Projects/Files?project=" + FilesConsole.Project, UriKind.Relative), Ct);

        Assert.DoesNotContain("file-problems-banner\">", body, StringComparison.Ordinal);
    }

    private static string Status(string body)
    {
        var status = Regex.Match(
            body, """<p role="status" class="outcome">(.*?)</p>""", RegexOptions.Singleline, TimeSpan.FromSeconds(2));

        return status.Success ? WebUtility.HtmlDecode(status.Groups[1].Value) : "(no outcome on the page)";
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        HttpClient client, string handler, (string Name, string Value)[] fields, bool withToken = true)
    {
        var form = new List<KeyValuePair<string, string>> { new("project", FilesConsole.Project) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));

        if (withToken)
        {
            var page = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

            var token = Regex.Match(
                page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

            Assert.NotEmpty(token);

            form.Add(new("__RequestVerificationToken", token));
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(new Uri("/Projects/FileProblems?handler=" + handler, UriKind.Relative), content, Ct);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
}
