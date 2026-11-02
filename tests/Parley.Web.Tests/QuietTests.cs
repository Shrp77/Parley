using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console does not narrate itself into somebody else's log.
/// </summary>
/// <remarks>
/// <para>
/// <b>This became a defect the day the console stopped having a process of its own.</b>
/// <c>serve --with-console</c> hosts it beside the broker, <c>autostart install</c> turns that on
/// by default, and both halves write the one file named by <c>--log</c> — so the console's
/// Information tier is not its own output any more, it is three lines about an implicit handler
/// method between every pair of lines a person started the broker to read.
/// </para>
/// <para>
/// <b>The floor matches <c>ParleyServer</c>'s, which has always had one.</b> Two hosts in one
/// process disagreeing about how much is worth saying is the kind of asymmetry that is invisible
/// while they are two processes and obvious the moment they are not.
/// </para>
/// </remarks>
[Integration]
public sealed class QuietTests
{
    /// <summary>The category the flood came from.</summary>
    /// <remarks>
    /// Named rather than generalised. A filter that quietened <c>Microsoft.*</c> and left this
    /// alone would pass a test written against any other category, and this is the one that put
    /// three lines in the log per page render.
    /// </remarks>
    private const string Narrating =
        "Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure.PageActionInvoker";

    [Fact]
    public async Task Nothing_below_a_warning_is_written()
    {
        await using var console = ConsoleHost.Build([]);

        var logger = console.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Narrating);

        // Asked of the logger rather than of the options, so it is the whole filter pipeline
        // being checked and not the one call that configures it. A rule from configuration beats
        // a minimum level, which is exactly the mechanism the second test relies on — so a test
        // reading MinLevel would report a floor the host does not actually apply.
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Debug));

        // The half that makes this a floor rather than silence. A page that throws still says so,
        // and a console that cannot bind still says why — which is the whole reason the noise was
        // tolerable enough to ship in the first place.
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public async Task Somebody_who_wants_the_narration_can_have_it_back()
    {
        // Through the command line, because that is what `Build` is handed and what a person
        // debugging a live console can reach without a rebuild. The environment variable
        // Logging__LogLevel__Default is the same rule by another route.
        //
        // Written as a test rather than as a claim in a comment: "configuration can raise this"
        // is the sort of thing that is true of the framework in general and quietly false of one
        // host that cleared its providers in the wrong order.
        await using var console = ConsoleHost.Build(["--Logging:LogLevel:Default=Information"]);

        var logger = console.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Narrating);

        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public async Task The_broker_and_the_console_agree_about_what_is_worth_saying()
    {
        // The property that was untrue, rather than the line that made it true. ParleyServer sets
        // the same floor with the same reasoning; a change that raised one and left the other
        // would put the asymmetry back, and the log it degrades belongs to neither of them.
        var server = await File.ReadAllTextAsync(
            RepositoryPath("src/Parley.Server/ParleyServer.cs"),
            TestContext.Current.CancellationToken);

        var host = await File.ReadAllTextAsync(
            RepositoryPath("src/Parley.Web/ConsoleHost.cs"),
            TestContext.Current.CancellationToken);

        const string Floor = "SetMinimumLevel(LogLevel.Warning)";

        Assert.Contains(Floor, server, StringComparison.Ordinal);
        Assert.Contains(Floor, host, StringComparison.Ordinal);
    }

    /// <summary>Finds a file by its path from the repository root.</summary>
    /// <param name="relative">Where it sits, from the root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, relative);
    }
}
