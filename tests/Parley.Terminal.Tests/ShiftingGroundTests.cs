using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a dialog acts on what it was opened about, even if the screen behind it has moved.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.8. A dialog is opened about the selected project or the
/// selected agent, and then a person fills it in — during which a refresh can arrive, a broker can
/// answer, and a list can reorder underneath. If the dialog read the selection again when it was
/// confirmed rather than when it was opened, it would act on whatever was selected by then.
/// </para>
/// <para>
/// The worst version of that is the removal dialog: it describes one project, and the name typed
/// back authorises removing <i>that</i> one. A confirmation that resolved the target late would
/// take the name for one project as permission to destroy another.
/// </para>
/// </remarks>
public sealed class ShiftingGroundTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-shifting-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ShiftingGroundTests()
    {
        Directory.CreateDirectory(_root);

        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

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
    public async Task A_removal_acts_on_the_project_it_described_and_not_on_the_one_selected_later()
    {
        // The probe. The dialog is opened about alpha; the selection then moves to beta; the name
        // typed back is alpha's. Only alpha may go.
        var ct = TestContext.Current.CancellationToken;

        WriteManifest("alpha", "beta");

        using var driving = Driving.Open();
        using var directory = BrokerDirectory.FromManifest(ManifestPath);
        using var shell = TerminalCommand.Build(directory, manifestPath: ManifestPath);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        driving.Row(shell.Projects.List, "alpha");
        await shell.Settled;

        await shell.Remove();

        var choosing = (ChoiceDialog)driving.Find(Removing.Title);

        Assert.Contains("alpha", choosing.Described, StringComparison.Ordinal);

        // The ground moves while the dialog is open.
        driving.Row(shell.Projects.List, "beta");
        await shell.Settled;

        driving.Row(choosing.Choices, Removing.Destroy);
        driving.Enter();

        var typing = (TextDialog)driving.Find(Removing.Destroy);

        // beta's name is refused, because this dialog is about alpha.
        driving.Type("beta");

        Assert.Equal(Removing.WrongName, typing.Problem);

        typing.Finish(taken: true);
        await shell.Settled;

        Assert.True(ProjectManifest.TryRead(ManifestPath, out var left, out _));
        Assert.Equal(2, left.Count);
    }

    [Fact]
    public async Task A_withdrawal_acts_on_the_agent_it_described()
    {
        // The same shape for the destructive action on the other region.
        var ct = TestContext.Current.CancellationToken;

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { "orders@test", "billing@test" })
        {
            await agents.UpsertAsync(
                new AgentRecord(AgentName.Create(name), "mcp", AgentTier.Pull, null, null, null,
                    false, AgentStatus.Active),
                ct);
        }

        WriteManifest("alpha");

        using var driving = Driving.Open();
        using var directory = BrokerDirectory.FromManifest(ManifestPath);
        using var shell = TerminalCommand.Build(directory, manifestPath: ManifestPath);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        driving.Row(shell.Agents.List, "billing@test");

        await shell.Withdraw();
        await shell.Settled;

        var choosing = (ChoiceDialog)driving.Find(Withdrawing.Title);

        Assert.Contains("billing@test", choosing.Described, StringComparison.Ordinal);

        // Somebody arrows onto the other agent while the dialog is up.
        driving.Row(shell.Agents.List, "orders@test");

        driving.Row(choosing.Choices, Withdrawing.Retire);
        driving.Enter();

        var typing = (TextDialog)driving.Find(Withdrawing.Retire);

        driving.Type("orders@test");

        Assert.Equal(Withdrawing.WrongName, typing.Problem);

        typing.Finish(taken: true);
        await shell.Settled;

        var registry = await new BrokerClient(_broker.Operators).RegistryAsync(ct);

        Assert.All(registry, agent => Assert.Equal(AgentStatus.Active, agent.Status));
    }

    private void WriteManifest(params string[] names) =>
        ProjectManifest.Write(
            ManifestPath,
            [
                // The first project points at the test's broker; any others point nowhere, because
                // two projects cannot share a port and only the first one is read from here.
                .. names.Select((name, at) => new ProjectSettings(
                    ProjectName.Create(name),
                    Path.Combine(_root, name + ".db"),
                    at == 0 ? _broker.Address : $"http://127.0.0.1:{9000 + at}",
                    null)),
            ]);
}
