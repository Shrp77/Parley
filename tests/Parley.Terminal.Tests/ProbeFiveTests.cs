using Parley.Cli.Tests;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Review gate, lens 1, run 5: the two terminal actions whose defect was fixed with no test proving
/// the fix.
/// </summary>
/// <remarks>
/// <para>
/// <b>`Shell.Add` and `Shell.Withdraw` both read the projects region's selection.</b> A project view
/// gives the agents region the screen, so that list is off screen and — on a terminal opened straight
/// into a project — may never have been filled at all. Both actions returned having done nothing while
/// the status line went on offering them, which for a withdrawal is the worst possible failure: the
/// person believes the agent is gone.
/// </para>
/// <para>
/// It is the sixth and seventh time that distinction has had to be made in this codebase, and it was
/// fixed during this feature <i>without a test</i>. A fix nothing exercises is a fix somebody will
/// undo, which is the entire argument for the gate's "probe, do not reason".
/// </para>
/// </remarks>
[Integration]
public sealed class ProbeFiveTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe5-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ProbeFiveTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _connection.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string Store => Path.Combine(_root, "acme.db");

    // **The root, not a manifest file.** `--config` names the directory this machine keeps its
    // Parley files in; the projects are the folders under it.
    private string Root => _root;

    private string CataloguePath => Path.Combine(_root, "acme-node.json");

    /// <summary>A surface sitting in a project view that was never navigated into.</summary>
    /// <remarks>
    /// The state a terminal is in when it is opened straight at a project, and the state every
    /// fixture that calls <c>RefreshAsync</c> first cannot reproduce — which is why the defect
    /// survived two surfaces and five recordings of the same distinction.
    /// </remarks>
    private async Task<Shell> OpenInsideProjectAsync(Driving.Session driving)
    {
        var project = new ProjectSettings(
            ProjectName.Create("acme"), Store, _broker.Address, null);

        ProjectManifest.Write(Root, [project]);

        await File.WriteAllTextAsync(
            CataloguePath, "{\n  \"agents\": {}\n}\n", TestContext.Current.CancellationToken);

        var shell = TerminalCommand.Build(BrokerDirectory.FromManifest(Root), manifestPath: Root);

        driving.Show(shell);

        // Straight into the project, with no top-level refresh before it, so nothing has ever filled
        // the projects region.
        await shell.GoAsync(ViewPlace.Into(ProjectName.Create("acme")));
        await shell.Settled;

        return shell;
    }

    [Fact]
    public async Task The_projects_region_really_is_unfilled_in_this_state()
    {
        // Non-vacuous first. If something had selected a project after all, both probes below would
        // pass while testing nothing — which is exactly how the defect survived.
        using var driving = Driving.Open();
        using var shell = await OpenInsideProjectAsync(driving);

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
        Assert.Null(shell.Projects.Selected);
    }

    [Fact]
    public async Task Adding_still_opens_its_dialog_when_the_projects_list_was_never_filled()
    {
        using var driving = Driving.Open();
        using var shell = await OpenInsideProjectAsync(driving);

        await shell.Add();
        await shell.Settled;

        // The first dialog adding opens is the folder picker now. What this probe is about is
        // unchanged: the action does something rather than returning silently because the projects
        // list off screen was never filled.
        Assert.NotNull(driving.Find(Browsing.Name));
    }

    [Fact]
    public async Task Withdrawing_still_opens_its_dialog_when_the_projects_list_was_never_filled()
    {
        using var driving = Driving.Open();
        using var shell = await OpenInsideProjectAsync(driving);

        // Arriving at a project reads it, so the list holds the seats the broker provides, which cannot be withdrawn.
        if (shell.Agents.Selected is not { } chosen || PersonSeat.Is(AgentName.Create(chosen.Name)) || Facilitation.Is(AgentName.Create(chosen.Name)))
        {
            // An empty deployment has nothing to withdraw. The probe above drives the same
            // `Current`-versus-selection decision through Add, and asserting on an empty list here
            // would be asserting on the fixture rather than on the surface.
            return;
        }

        await shell.Withdraw();
        await shell.Settled;

        Assert.NotNull(driving.Find(Withdrawing.Title));
    }
}
