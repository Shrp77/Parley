using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Client;
using Parley.Client.Seating;
using Xunit;

namespace Parley.Terminal.Tests;

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
[Integration]
public sealed class EmptyDeploymentTests
{
    [Fact]
    public async Task A_machine_with_no_projects_still_draws_and_says_what_to_do()
    {
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory([], _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = TerminalCommand.Build(directory, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.Empty(shell.Projects.Projects);
        Assert.Null(shell.Projects.Selected);

        // Read from the row rather than from the screen: at this width the sentence wraps, and a
        // substring search over a wrapped draw is a check that passes or fails on column count.
        Assert.Equal(ProjectsRegion.Nothing, Assert.Single(Drawn.Rows(shell.Projects.List)));
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
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        for (var region = 0; region < 4; region++)
        {
            shell.Focus(region);

            // Every action, the one that hands off to a browser included. It used to be skipped
            // here: it reaches a real `Process.Start` at the console's address, so running it
            // opened a window on whatever machine the suite was on -- which it did, on the
            // owner's, twice. `Driving.Show` gives a driven surface a launcher that records an
            // address and opens nothing, so the exception is no longer needed and the action that
            // was least exercised is now exercised like the rest.
            foreach (var action in shell.Actions())
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

        // Six panes across the three views since 0.30.0, where there were four: the conversation view
        // gained the room beside its transcript and one line to speak from under it. Counted rather than
        // listed so that a pane added without a name in the status line fails here.
        Assert.Equal(6, named.Count);
        Assert.Contains(named, said => said.Contains(ProjectsRegion.RegionTitle, StringComparison.Ordinal));
        Assert.Contains(named, said => said.Contains(AgentsRegion.RegionTitle, StringComparison.Ordinal));
        Assert.Contains(named, said => said.Contains(ConversationsRegion.RegionTitle, StringComparison.Ordinal));
        Assert.Contains(named, said => said.Contains(TranscriptRegion.RegionTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_says_so_rather_than_looking_empty()
    {
        // Two different facts. "This machine names no projects" is a machine waiting to be set up; "a
        // project could not be read" is somebody's edit, thirty seconds ago, that they can undo — and
        // showing the first for the second hides the edit that caused it.
        //
        // **Repointed at a project's own file, which is where a hand edit lands now.** A damaged single
        // manifest used to be the only shape this could take; a directory of them makes the distinction
        // sharper rather than weaker, because one unreadable project must not read as a machine with
        // none.
        var root = Path.Combine(Path.GetTempPath(), "parley-broken-" + Path.GetRandomFileName());
        var folder = ProjectFolder.For(root, ProjectName.Create("alpha"));

        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(
            Path.Combine(folder, ProjectFolder.ConfigurationFileName),
            "{ not json",
            TestContext.Current.CancellationToken);

        try
        {
            using var driving = Driving.Open();
            using var directory = BrokerDirectory.FromManifest(root);
            using var shell = TerminalCommand.Build(directory, manifestPath: root);

            driving.Show(shell);
            await shell.RefreshAsync(TestContext.Current.CancellationToken);
            await shell.Settled;
            driving.App.LayoutAndDraw(true);

            var said = Assert.Single(Drawn.Rows(shell.Projects.List));

            Assert.Contains("could not be read", said, StringComparison.Ordinal);
            Assert.NotEqual(ProjectsRegion.Nothing, said);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
