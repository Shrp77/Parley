using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That agents can be registered from the console, that nothing is written before it is confirmed, and that a refusal says why.</summary>
public sealed class EnrolPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-enrol-" + Guid.NewGuid().ToString("N"));

    public EnrolPageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Repository(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private static Task<string> GetAsync(HttpClient client, string path) =>
        client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_agents_page_offers_to_register_a_repository_or_a_folder()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var everywhere = await GetAsync(client, "/?tab=agents");
        var page = await GetAsync(client, "/?tab=agents&project=alpha");

        // Registering belongs to a project: the all-projects view only looks.
        Assert.DoesNotContain("action=\"/Projects/Enrol\"", everywhere, StringComparison.Ordinal);
        Assert.Contains("action=\"/Projects/Enrol\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"workspace\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"role\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"folder\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_repository_is_described_and_nothing_is_written_by_looking()
    {
        var repository = Repository("shipping");

        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await GetAsync(client, $"/Projects/Enrol?project=alpha&workspace={Uri.EscapeDataString(repository)}&role=Shipping%20Desk");

        Assert.Contains("These files will be written. Nothing has been yet.", page, StringComparison.Ordinal);
        Assert.Contains(".mcp.json", page, StringComparison.Ordinal);

        // The role is corrected as it is everywhere else: a space becomes a hyphen.
        Assert.Contains("shipping-desk", page, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repository, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(repository, "CLAUDE.md")));
    }

    [Theory]
    [InlineData("", "shipping", "Name a repository directory")]
    [InlineData("nowhere-at-all", "shipping", "There is no directory")]
    public async Task A_repository_that_cannot_be_enrolled_says_why(string workspace, string role, string reason)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await GetAsync(client, $"/Projects/Enrol?project=alpha&workspace={Uri.EscapeDataString(workspace)}&role={role}");

        Assert.Contains(reason, page, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Write them and start it", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_is_refused()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await GetAsync(client, $"/Projects/Enrol?project=alpha&workspace={Uri.EscapeDataString(_root)}&role=shipping");

        Assert.Contains("holds no .git", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_lists_its_repositories_and_ticks_none_of_them()
    {
        Repository("alpha-service");
        Repository("beta-service");
        Directory.CreateDirectory(Path.Combine(_root, "notes"));

        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await GetAsync(client, $"/Projects/Enrol?project=alpha&folder={Uri.EscapeDataString(_root)}");

        Assert.Contains("2 repositories under", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Chosen\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" checked", page[page.IndexOf("name=\"Chosen\"", StringComparison.Ordinal)..], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(">notes<", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enrolling_a_folder_with_nothing_ticked_writes_nothing()
    {
        var repository = Repository("alpha-service");

        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var opening = await client.GetAsync(
            new Uri($"/Projects/Enrol?project=alpha&folder={Uri.EscapeDataString(_root)}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var html = await opening.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var token = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(token);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/Projects/Enrol?handler=Apply", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Project"] = "alpha",
                ["Folder"] = _root,
                ["__RequestVerificationToken"] = token,
            }),
        };

        if (opening.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Nothing was ticked", body, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repository, ".mcp.json")));
    }

    [Fact]
    public async Task A_project_this_machine_does_not_name_goes_back_to_the_agents()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/Projects/Enrol?project=nowhere&workspace=x&role=y", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("/?tab=agents", response.RequestMessage!.RequestUri!.ToString(), StringComparison.Ordinal);
    }
}
