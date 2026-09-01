using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That another page in the operator's browser cannot act through this one.
/// </summary>
/// <remarks>
/// <para>
/// The console binds loopback, which is a smaller protection than it sounds: every page the
/// operator visits can reach <c>127.0.0.1</c> from their browser, with their session, and a form
/// on a hostile page posting to this console would be indistinguishable from the operator
/// clicking. Purge cannot be undone, so this is the one place where "it is only on localhost" is
/// not an answer.
/// </para>
/// <para>
/// Razor Pages validates an antiforgery token on every POST by default and the form tag helper
/// emits one, so this is a check that nothing has opted out — which is the shape that survives
/// pages nobody has written yet, unlike a test that exercises one.
/// </para>
/// </remarks>
public sealed class ForgeryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _console;

    public ForgeryTests(WebApplicationFactory<Program> console) => _console = console;

    [Theory]
    [InlineData("purge")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("forceconclude")]
    [InlineData("instruct")]
    public async Task A_post_without_a_token_is_refused(string action)
    {
        using var client = _console.CreateClient();

        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["text"] = "do the thing" });

        using var response = await client.PostAsync(
            new Uri($"/Projects/Act?project=alpha&action={action}&subject=orders@alpha",
                UriKind.Relative),
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_post_with_a_json_body_is_refused_too()
    {
        // The shape that skips a form entirely. Without the token it is the same request from the
        // console's point of view, and it must be refused for the same reason.
        using var client = _console.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/Projects/Act?project=alpha&action=purge&subject=orders@alpha",
                UriKind.Relative),
            new { subject = "orders@alpha" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void No_page_opts_out_of_antiforgery_validation()
    {
        // The greppable form. A single attribute anywhere would undo the whole of the above, and
        // it would do so silently: the page would keep working, and only the refusal would stop.
        var sources = Directory.GetFiles(
            RepositoryPath("src/Parley.Web"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(
                RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sources);

        var opted = sources
            .Where(p => File.ReadAllText(p)
                .Contains("IgnoreAntiforgeryToken", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            opted.Count == 0,
            "a page has opted out of antiforgery validation, which makes every acting control on "
            + "it reachable from any page in the operator's browser: " + string.Join(", ", opted));
    }

    [Fact]
    public async Task Describing_an_action_never_performs_it()
    {
        // A GET describes and only a POST acts, so nothing that follows links -- a crawler, a
        // preloader, a chat client fetching a preview of a pasted URL -- can destroy anything.
        // Asserted against a project this machine does not have, so the only thing that could
        // make this pass wrongly is a handler that acts before it resolves the project.
        using var client = _console.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/Projects/Act?project=alpha&action=purge&subject=orders@alpha",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Purged:", body, StringComparison.Ordinal);
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
