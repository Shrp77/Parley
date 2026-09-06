using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That a machine with nothing on it is a screen somebody can act from.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.12. The first thing anybody sees is a machine that names no
/// projects, because that is what a machine looks like before Parley is set up on it — and it is
/// the state every action has to be safe in, since none of them has anything to act on.
/// </para>
/// <para>
/// It is also the state most likely to be reached with a broken manifest, which empties the list
/// while being a different fact from an empty one.
/// </para>
/// </remarks>
public sealed class EmptyDeploymentTests
{
    [Fact]
    public async Task A_machine_with_no_projects_still_draws_and_says_what_to_do()
    {
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory([], _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = TopCommand.Build(directory, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.Empty(shell.Projects.Projects);
        Assert.Null(shell.Projects.Selected);

        // Read from the row rather than from the screen: at this width the sentence wraps, and a
        // substring search over a wrapped draw is a check that passes or fails on column count.
        Assert.Equal(ProjectsRegion.Nothing, Assert.Single(Rows(shell)));
        Assert.Contains(ProjectsRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_action_is_safe_with_nothing_selected()
    {
        // None of them has anything to act on, and the failure this prevents is not a wrong result
        // but an exception on the first key somebody presses on a fresh machine.
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory([], _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        for (var region = 0; region < 4; region++)
        {
            shell.Focus(region);

            // Every action but the one that starts a browser. It is a real `Process.Start` at the
            // console's address, so running it here opens a window on whatever machine the suite
            // is on -- which it did, on the owner's, before this was caught.
            foreach (var action in shell.Actions().Where(action => !Launches(action)))
            {
                await action.Run();
                await shell.Settled;
            }
        }
    }

    [Fact]
    public async Task Every_view_can_be_reached_and_named_on_an_empty_machine()
    {
        // A machine with nothing on it is where an empty pane is most likely to be mistaken for a
        // broken one, so every view is visited and every pane named -- one view at a time now
        // rather than four regions at once.
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory([], _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        var named = new List<string>();

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            named.Add(shell.Status);
        }

        Assert.Equal(4, named.Count);
        Assert.Contains(named, said => said.Contains(ProjectsRegion.RegionTitle, StringComparison.Ordinal));
        Assert.Contains(named, said => said.Contains(AgentsRegion.RegionTitle, StringComparison.Ordinal));
        Assert.Contains(named, said => said.Contains(ConversationsRegion.RegionTitle, StringComparison.Ordinal));
        Assert.Contains(named, said => said.Contains(TranscriptRegion.RegionTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_says_so_rather_than_looking_empty()
    {
        // Two different facts. "This machine names no projects" is a machine waiting to be set up;
        // "the manifest could not be read" is somebody's edit, thirty seconds ago, that they can
        // undo — and showing the first for the second hides the edit that caused it.
        var broken = Path.Combine(Path.GetTempPath(), "parley-broken-" + Path.GetRandomFileName() + ".json");

        await File.WriteAllTextAsync(broken, "{ not json", TestContext.Current.CancellationToken);

        try
        {
            using var driving = Driving.Open();
            using var directory = BrokerDirectory.FromManifest(broken);
            using var shell = TopCommand.Build(directory, manifestPath: broken);

            driving.Show(shell);
            await shell.RefreshAsync(TestContext.Current.CancellationToken);
            await shell.Settled;
            driving.App.LayoutAndDraw(true);

            var said = Assert.Single(Rows(shell));

            Assert.Contains("could not be read", said, StringComparison.Ordinal);
            Assert.NotEqual(ProjectsRegion.Nothing, said);
        }
        finally
        {
            File.Delete(broken);
        }
    }

    /// <summary>What the projects region is actually showing, row by row.</summary>
    private static List<string> Rows(Shell shell)
    {
        var source = shell.Projects.List.Source;

        return [.. Enumerable.Range(0, source?.Count ?? 0)
            .Select(at => source!.ToList()[at]?.ToString() ?? string.Empty)];
    }

    /// <summary>Whether an action leaves this process to do its work.</summary>
    /// <remarks>
    /// Named by its label rather than skipped by position, so an action added beside it is still
    /// exercised and only this one is left out.
    /// </remarks>
    private static bool Launches(RegionAction action) =>
        action.Label.Contains("browser", StringComparison.Ordinal);
}
