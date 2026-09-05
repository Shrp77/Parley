using Parley.Client.Rendering;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That every address the terminal's browser key can produce is a page the console serves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than in the client's own tests, because only this project may see both.</b>
/// <c>Parley.Client</c> cannot reference the console — that would put ASP.NET Core on the
/// terminal's graph, which is most of why the client was split out. So the paths are declared there
/// and checked against the pages here.
/// </para>
/// <para>
/// <b>The defect this exists for has already happened once, one layer down.</b> 005's terminal
/// offered to open the web console at a hand-copied <c>127.0.0.1:5045</c> while every other
/// statement of the default said <c>5280</c>, so the action opened a dead port and reported
/// success. A hand-copied route is the same mistake with a worse symptom: a 404 reads as the
/// console being broken rather than the link being wrong.
/// </para>
/// </remarks>
public sealed class DeepLinkTests
{
    private static string Pages()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "Parley.Web")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src", "Parley.Web", "Pages");
    }

    /// <summary>Turns a declared route into the Razor page file it must correspond to.</summary>
    /// <param name="route">The route, as <see cref="Deep"/> declares it.</param>
    /// <returns>The expected file path.</returns>
    private static string FileFor(string route) =>
        route == Deep.Home
            ? Path.Combine(Pages(), "Index.cshtml")
            : Path.Combine(Pages(), route.Trim('/').Replace('/', Path.DirectorySeparatorChar))
                + ".cshtml";

    [Theory]
    [InlineData(Deep.Home)]
    [InlineData(Deep.Agents)]
    [InlineData(Deep.Conversation)]
    public void Every_declared_route_is_a_page_that_exists(string route)
    {
        Assert.True(File.Exists(FileFor(route)), $"{route} has no page at {FileFor(route)}");
    }

    [Fact]
    public void The_conversations_route_reaches_the_index_of_its_folder()
    {
        // Declared without the Index, because that is how a person types it and how the router
        // resolves it. Checked separately so the mapping above stays simple.
        Assert.True(File.Exists(Path.Combine(Pages(), "Conversations", "Index.cshtml")));
    }

    [Theory]
    [InlineData(Deep.Agents, "project")]
    [InlineData(Deep.Conversation, "project")]
    [InlineData(Deep.Conversation, "id")]
    public void Every_query_parameter_a_link_sends_is_one_the_page_binds(string route, string parameter)
    {
        // A link carrying a name the page does not bind is a link that silently opens the page with
        // nothing selected -- which looks like the console losing the thing rather than the link
        // naming it wrongly.
        var model = FileFor(route) + ".cs";

        Assert.True(File.Exists(model), $"{route} has no page model at {model}");

        var text = File.ReadAllText(model);
        var property = char.ToUpperInvariant(parameter[0]) + parameter[1..];

        Assert.Contains("SupportsGet = true", text, StringComparison.Ordinal);
        Assert.Contains($"public string? {property} {{ get; set; }}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // A path resolution that quietly found nothing would make every assertion above vacuous.
        Assert.True(Directory.Exists(Pages()));
        Assert.NotEmpty(Directory.GetFiles(Pages(), "*.cshtml"));
    }
}
