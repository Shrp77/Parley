using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the screen follows a manifest edited under it, including the edit that takes away what is
/// on screen.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.14. Run 1.8 asked what a dialog acts on when the selection
/// moves under it; this asks the version one layer down, where the thing that moved is the file the
/// selection was drawn from. It is not hypothetical: this surface names projects, and
/// <c>parley project add</c> and <c>--destroy</c> are commands somebody runs in the next window.
/// </para>
/// <para>
/// The failure worth catching is a screen still showing a removed project's agents and transcript
/// under a heading that no longer exists — data from a broker nobody can name any more, which is
/// exactly the state a person opened this to get out of.
/// </para>
/// </remarks>
public sealed class ManifestMovesTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-moves-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;
    private readonly LoopbackOperator _second;

    public ManifestMovesTests()
    {
        Directory.CreateDirectory(_root);

        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
        _second = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _second.DisposeAsync();

        _connection.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string ManifestPath => Path.Combine(_root, "projects.json");

    [Fact]
    public async Task A_project_added_beside_the_surface_appears_on_the_next_refresh()
    {
        Write("alpha");

        using var driving = Driving.Open();
        using var directory = BrokerDirectory.FromManifest(ManifestPath);
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Single(shell.Projects.Projects);

        Write("alpha", "beta");

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Equal(2, shell.Projects.Projects.Count);
        Assert.Contains(shell.Projects.Projects, project => project.Name.Value == "beta");
    }

    [Fact]
    public async Task The_project_on_screen_being_taken_away_leaves_nothing_of_it_behind()
    {
        // The edit that matters. A transcript and an agent list drawn from a broker nobody can name
        // any more is worse than an empty screen, because it reads as current.
        Write("alpha", "beta");

        using var driving = Driving.Open();
        using var directory = BrokerDirectory.FromManifest(ManifestPath);
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Row(shell.Projects.List, "beta");
        shell.Projects.Announce();
        await shell.Settled;

        Assert.Equal("beta", shell.Projects.Selected?.Name.Value);
        Assert.NotEmpty(shell.Agents.Rows);

        Write("alpha");

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Equal("alpha", shell.Projects.Selected?.Name.Value);
        Assert.DoesNotContain(shell.Projects.Projects, project => project.Name.Value == "beta");
        Assert.Null(shell.Transcript.Open);
    }

    [Fact]
    public async Task Every_project_going_at_once_leaves_a_surface_that_still_takes_keys()
    {
        // What `project remove` on the last project produces. The regions have nothing to draw and
        // every action has nothing to act on, which is the state run 1.12 pinned — reached here by
        // an edit rather than by starting with an empty machine.
        Write("alpha");

        using var driving = Driving.Open();
        using var directory = BrokerDirectory.FromManifest(ManifestPath);
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        ProjectManifest.Write(ManifestPath, []);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Empty(shell.Projects.Projects);

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

        driving.Tab();

        Assert.StartsWith(Shell.FocusPrefix, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manifest_that_becomes_unreadable_says_so_rather_than_keeping_the_last_good_one()
    {
        // Showing the last good list would be the worst of the three answers: it is not what the
        // file says, and it is indistinguishable from a file that still says it.
        Write("alpha");

        using var driving = Driving.Open();
        using var directory = BrokerDirectory.FromManifest(ManifestPath);
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Single(shell.Projects.Projects);

        await File.WriteAllTextAsync(ManifestPath, "{ not json", TestContext.Current.CancellationToken);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Empty(shell.Projects.Projects);
        Assert.NotNull(directory.ManifestProblem);
    }

    /// <summary>Writes the manifest, one broker per project.</summary>
    /// <remarks>
    /// Two projects on one endpoint is refused by the manifest itself, and correctly: only one
    /// broker can hold a port. Two loopback servers over one store is the cheapest arrangement
    /// that satisfies it and still lets both projects answer.
    /// </remarks>
    private void Write(params string[] names) =>
        ProjectManifest.Write(
            ManifestPath,
            [.. names.Select((name, at) => new ProjectSettings(
                ProjectName.Create(name),
                Path.Combine(_root, name + ".db"),
                at == 0 ? _broker.Address : _second.Address,
                null))]);
}
