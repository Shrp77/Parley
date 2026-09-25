using Parley.Client;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That typing <c>parley</c> puts a screen up, rather than waiting on brokers first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner as "parley takes a while to come up".</b> Measured rather than guessed:
/// the two process starts cost about 150ms together, and the rest was one failed connection —
/// 2.2 seconds, which is <c>BrokerDirectory.Connect</c>'s two-second <c>ConnectTimeout</c> plus
/// overhead.
/// </para>
/// <para>
/// Two faults met at once. <c>DriveAsync</c> awaited the first read before running the
/// application, so nothing was drawn until every broker had answered or given up. And the terminal
/// handed itself a broker to ask on a machine where none could exist: <c>ProjectResolution.Endpoint</c>
/// answers with a built-in default when nothing names one, and that default was passed on as though
/// somebody had chosen it.
/// </para>
/// <para>
/// The second is the one that made it certain rather than occasional. A machine that has not named
/// its first project has no broker listening at all — a broker with nothing to serve binds no port
/// — so the address was guaranteed to fail, and the wait was guaranteed to be paid. Worst during
/// setup, which is when <c>parley</c> is typed most.
/// </para>
/// </remarks>
public sealed class StartsWithoutWaitingTests
{
    [Fact]
    public void A_machine_that_has_named_nothing_offers_no_broker_to_ask()
    {
        // The state the owner was in: tools installed, entry registered, broker running, no
        // projects. With no fallback there is nothing to connect to, so the first read has nothing
        // to wait for.
        using var directory = BrokerDirectory.FromManifest(NoManifestHere, null);

        Assert.Empty(directory.Projects);
    }

    [Fact]
    public void An_address_somebody_chose_is_still_offered()
    {
        // **The fallback is not being removed, and this is the half worth protecting.** SPEC.md
        // §4.1 permits a deployment configured entirely by explicit paths, and --broker and
        // PARLEY_BROKER are how somebody says so. A surface that ignored them would be the only
        // client unable to reach a broker the rest of the tool talks to happily.
        using var directory = BrokerDirectory.FromManifest(
            NoManifestHere, new FallbackBroker("http://127.0.0.1:19100"));

        Assert.Single(directory.Projects);
    }

    [Fact]
    public void The_built_in_default_is_not_mistaken_for_a_choice()
    {
        // What the terminal now asks before passing a fallback on. `Endpoint` always answers, so
        // the value alone cannot distinguish "nobody said" from "somebody said this" — the source
        // is the only thing that can, and it is why the resolver reports one.
        var nobodySaid = ProjectResolution.Endpoint(null, _ => null, null);

        Assert.Equal(ValueSource.Default, nobodySaid.Source);

        var theySaid = ProjectResolution.Endpoint("http://127.0.0.1:19105", _ => null, null);

        Assert.Equal(ValueSource.Argument, theySaid.Source);
    }

    [Fact]
    public void The_terminal_passes_a_fallback_only_when_one_was_chosen()
    {
        // Read off the source, because the decision is in a top-level program that cannot be
        // called into. What is pinned is that the source is consulted at all: passing
        // `Endpoint(...).Value` straight through is the defect, and it is what the line looked
        // like before.
        var source = File.ReadAllText(
            Path.Combine(Root(), "src", "Parley.Terminal", "Program.cs"));

        Assert.Contains("ValueSource.Default ? null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_read_does_not_hold_the_screen_back()
    {
        // **Ordering, read off the source.** A driven test cannot see this: every one of them calls
        // `Show` and then `RefreshAsync`, which is the arrangement being checked — the suite has
        // been exercising the fixed ordering all along while production did the opposite.
        var source = File.ReadAllText(
            Path.Combine(Root(), "src", "Parley.Terminal", "TerminalCommand.cs"));

        var started = source.IndexOf("shell.RefreshAsync(cancellationToken)", StringComparison.Ordinal);
        var runs = source.IndexOf("app.Run(shell)", StringComparison.Ordinal);

        Assert.True(started >= 0, "the terminal no longer reads the deployment at start-up");
        Assert.True(runs > started, "the terminal no longer runs the application");

        // The read is started before the run and awaited after it. What must not come back is an
        // `await` on that call *before* `app.Run`, which is the whole of the defect.
        var between = source[started..runs];

        Assert.DoesNotContain("await shell.RefreshAsync", between, StringComparison.Ordinal);
    }

    /// <summary>A root that does not exist, so nothing is named.</summary>
    /// <remarks>
    /// A directory, not a path ending in <c>projects.json</c>. 019 made the root a directory, and
    /// a path naming a file inside one describes a *misconfigured* machine rather than an
    /// unconfigured one — which reports a manifest problem and offers no fallback at all.
    /// <c>UnnamedMachineTests</c> carries the same warning, having been caught by it; this test
    /// was caught by it again, which is what the warning is for.
    /// </remarks>
    private static string NoManifestHere =>
        Path.Combine(Path.GetTempPath(), $"parley-no-manifest-{Guid.NewGuid():N}");

    /// <summary>The repository root, found by walking up from the build output.</summary>
    /// <returns>The directory holding <c>src</c>.</returns>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
