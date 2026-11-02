using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console works when it is started from somewhere other than its own directory.
/// </summary>
/// <remarks>
/// <para>
/// The way it is actually started: by an operator in their repository, or by a scheduled task
/// with no meaningful working directory. The default content root is the current directory, and
/// the static web asset manifest resolves back to the source tree — so a binary launched from
/// anywhere else serves a page with no stylesheet and no error beyond a 404 for a file sitting
/// next to it.
/// </para>
/// <para>
/// Nothing in the rest of this suite can catch it. <c>WebApplicationFactory</c> sets the content
/// root from the test project's assembly attributes, so the tests are always looking in the right
/// place — the console is only ever wrong where nobody was testing. It was found by starting it
/// and opening the page, which is the argument for doing that.
/// </para>
/// </remarks>
[Integration]
public sealed class LaunchTests
{
    [Fact]
    public async Task The_console_is_styled_by_the_page_rather_than_by_a_file()
    {
        // It was a file, and the file did not survive `dotnet pack` into the tool that starts
        // this console: the page rendered unstyled from an installed `parley console` while
        // working perfectly from the build output. At 365 bytes the fix is to stop having a file,
        // which removes the class of problem rather than the instance.
        //
        // Asserted against what is served, so it covers the styling arriving at all rather than
        // the mechanism it arrives by.
        using var console = new Microsoft.AspNetCore.Mvc.Testing
            .WebApplicationFactory<Program>();

        using var client = console.CreateClient();

        var served = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("<style>", served, StringComparison.Ordinal);
        Assert.Contains(".unreachable", served, StringComparison.Ordinal);
        Assert.DoesNotContain("console.css", served, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_served_from_a_directory_beside_the_binary()
    {
        // The whole static-file path is gone, so there is no longer a way for the console to be
        // correct in a build tree and wrong once packaged. A page that reintroduces one brings
        // the packaging question back with it.
        var host = File.ReadAllText(RepositoryPath("src/Parley.Web/ConsoleHost.cs"));

        Assert.DoesNotContain("UseStaticFiles", host, StringComparison.Ordinal);
    }

    [Fact]
    public void The_content_root_is_anchored_to_the_executable()
    {
        // Kept although no asset depends on it any more: the host reads its content root for more
        // than static files, and a console started from a scheduled task has no meaningful
        // working directory to fall back on.
        var host = File.ReadAllText(RepositoryPath("src/Parley.Web/ConsoleHost.cs"));

        Assert.Contains("ContentRootPath = AppContext.BaseDirectory", host, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_one_host_rather_than_one_per_caller()
    {
        // Two callers start this console -- its own Main and `parley console` -- and two callers
        // each building a host is two hosts that differ the first time one of them is edited.
        // The differences that would matter are exactly the ones nobody would notice: the
        // loopback default, the log providers, the content root.
        var program = File.ReadAllText(RepositoryPath("src/Parley.Web/Program.cs"));

        Assert.Contains("ConsoleHost.RunAsync", program, StringComparison.Ordinal);
        Assert.DoesNotContain("WebApplication.CreateBuilder", program, StringComparison.Ordinal);
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
