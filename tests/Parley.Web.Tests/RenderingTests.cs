using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console serves, and that what an agent wrote stays content.
/// </summary>
/// <remarks>
/// <para>
/// Message text is untrusted input authored by a peer. The console renders it to a person rather
/// than to a model, so §13's envelope does not apply — but the same principle does: it is data,
/// and it must not be able to alter the page or imitate the console's own labelling. A visibility
/// annotation is exactly the label worth forging, because it is the one a reader trusts.
/// </para>
/// <para>
/// Razor encodes by default, which makes this a check that nothing has opted out rather than a
/// check that somebody remembered to escape. That is the right shape: the review rule becomes
/// "no raw output on a message payload", which is greppable.
/// </para>
/// </remarks>
public sealed class RenderingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _console;

    public RenderingTests(WebApplicationFactory<Program> console) => _console = console;

    [Fact]
    public async Task The_console_serves_its_project_list()
    {
        using var client = _console.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_machine_with_no_projects_says_so_rather_than_showing_an_empty_page()
    {
        // An absent manifest and a broken one are different facts, and a page that rendered both
        // as emptiness would leave somebody unable to tell a fresh machine from a fault.
        using var client = _console.CreateClient();

        var body = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("Projects", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_project_goes_back_to_the_list_rather_than_erroring()
    {
        // The project is the context for everything below it, so a name this machine does not
        // have is the wrong address rather than a failure.
        // Asserted as the redirect it is. Letting the client follow it and checking for OK would
        // pass on a page that had errored and on one that did not exist, because the answer being
        // checked would be the project list's either way.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri("/Projects/Agents?project=not-a-project", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }

    [Fact]
    public void No_page_opts_out_of_encoding_for_a_message_payload()
    {
        // The greppable form of the rule. Razor encodes unless somebody explicitly asks for raw
        // output, so this asserts that nobody has -- which is a check that survives every future
        // page, unlike a test that renders one of them.
        var pages = Directory.GetFiles(
            RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories);

        Assert.NotEmpty(pages);

        var raw = pages
            .Where(p => File.ReadAllText(p).Contains("Html.Raw", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            raw.Count == 0,
            "a page emits raw output, which for a message payload is the injection this console "
            + "must not permit: " + string.Join(", ", raw));
    }

    [Fact]
    public void The_console_has_no_route_to_a_store()
    {
        // §4 gives the database to one process. A second reader is the corruption that section
        // exists to prevent, so the console must not acquire one -- not by package, not by
        // project reference, not by somebody adding a connection string.
        // Read as references rather than as text. The file says in a comment that there is no
        // SQLite here and never may be, and a check that matched any occurrence would fail on the
        // sentence promising the thing it is checking for.
        var project = System.Xml.Linq.XDocument.Load(
            RepositoryPath("src/Parley.Web/Parley.Web.csproj"));

        var referenced = project.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        var forbidden = referenced
            .Where(r => r.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                     || r.Contains("Parley.Store", StringComparison.Ordinal)
                     || r.Contains("Parley.Broker", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            forbidden.Count == 0,
            "the console has acquired a route to a store: " + string.Join(", ", forbidden));
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
