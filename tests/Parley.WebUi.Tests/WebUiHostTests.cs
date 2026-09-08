using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Parley.Web;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>
/// That `parley-web-ui` serves the web UI, and not an empty host that 404s everything.
/// </summary>
/// <remarks>
/// <para>
/// Razor Pages are discovered by walking the entry assembly and its dependencies. When the
/// console is started by <c>parley</c> the entry assembly is the CLI, so the pages — compiled
/// into <c>Parley.Web</c> — are not found, and every route returns 404. The stylesheet still
/// serves, because that is a file beside the binary, which makes the failure look like a routing
/// problem rather than a missing application part.
/// </para>
/// <para>
/// <c>Parley.Web.Tests</c> cannot catch this and never will: its host is built with
/// <c>Parley.Web</c> as the entry assembly, which is the one case that works. So the test lives
/// here, in a project whose entry assembly is something else — the condition is the point, and
/// running from the wrong assembly is what reproduces it.
/// </para>
/// </remarks>
public sealed class WebUiHostTests
{
    [Fact]
    public async Task The_console_finds_its_pages_when_something_else_is_the_entry_assembly()
    {
        // Started rather than merely built: routes are not materialised into the endpoint source
        // until the host runs, so reading them from a built-and-idle app finds an empty
        // collection whether the pages were discovered or not. Port zero, so this never collides
        // with a console somebody is actually running.
        await using var app = ConsoleHost.Build([], "http://127.0.0.1:0");

        await app.StartAsync(TestContext.Current.CancellationToken);

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        await app.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(routes, r => r.Contains("Projects/Agents", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Contains("Conversations/Detail", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Contains("Projects/Health", StringComparison.Ordinal));
    }

    [Fact]
    public void The_web_ui_binds_loopback_when_nothing_says_otherwise()
    {
        // §16 confines the transport to a trusted path, and the web UI is a second door onto it.
        // The default has to be the one where the question does not arise.
        Assert.Equal("http://127.0.0.1:5280", ConsoleHost.LoopbackAddress);
    }

    [Fact]
    public void Choosing_an_address_off_this_machine_says_so()
    {
        // Widening announces itself rather than happening quietly. Somebody who meant to reach
        // the console from their laptop has done something with consequences, and the one moment
        // they will read a line about it is the moment they typed the address.
        var warned = new StringWriter();

        using var app = ConsoleHost.Build([], "http://0.0.0.0:5280", warned);

        Assert.Contains("not an authentication boundary", warned.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_loopback_explicitly_is_not_warned_about()
    {
        // A warning on an address that is already loopback is a warning nobody reads, including
        // on the run where it matters.
        var warned = new StringWriter();

        using var app = ConsoleHost.Build([], "http://127.0.0.1:5399", warned);

        Assert.Equal(string.Empty, warned.ToString());
    }

    [Fact]
    public void A_project_named_at_the_web_ui_is_reported_rather_than_dropped()
    {
        // Every other command here acts on one deployment, so naming one is the habit. The
        // console's first page is the list of them, and silently dropping the name would answer
        // a narrower question than was asked without saying so.
        var program = File.ReadAllText(RepositoryPath("src/Parley.Cli/Program.cs"));

        Assert.Contains(
            "The web UI is not scoped to one project", program, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_address_is_refused_before_the_host_sees_it()
    {
        // Kestrel refuses one several frames deep in a stack trace that says nothing about what
        // was typed. Checking first costs a line and turns a crash into a sentence.
        var program = File.ReadAllText(RepositoryPath("src/Parley.WebUi/Program.cs"));

        Assert.Contains(
            "is not an address the web UI can bind", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_ui_is_offered_in_the_help_of_the_tool_that_forwards_to_it()
    {
        // A subcommand nobody is told about is one nobody runs.
        var program = File.ReadAllText(RepositoryPath("src/Parley.Cli/Program.cs"));

        Assert.Contains("parley web-ui", program, StringComparison.Ordinal);
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
