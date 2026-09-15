using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Parley.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Review gate, lens 2, run 18: what the screen claims when the writing stops half way.
/// </summary>
/// <remarks>
/// <para>
/// 2.14 established that an enrolment can fail after files have already changed: three files are
/// written in order and the catalogue is last, so a catalogue that cannot be written arrives with the
/// launch configuration already replaced. The shell now names what changed.
/// </para>
/// <para>
/// This surface catches the same failure and adds a sentence of its own. The question is whether that
/// sentence is true — and it is the more dangerous half if it is not, because a report that merely
/// omits something leaves somebody uncertain, while one that asserts the opposite settles it.
/// </para>
/// </remarks>
public sealed class ProbeTwentyNineTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe29-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ProbeTwentyNineTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

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
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string ManifestPath => Path.Combine(_root, "projects.json");

    private string CataloguePath => Path.Combine(_root, "acme-node.json");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private async Task<Shell> OpenAsync(Driving.Session driving)
    {
        ProjectManifest.Write(
            ManifestPath,
            [new ProjectSettings(
                ProjectName.Create("acme"), Path.Combine(_root, "acme.db"), _broker.Address,
                CataloguePath, null)]);

        await File.WriteAllTextAsync(
            CataloguePath, "{\n  \"agents\": {}\n}\n", TestContext.Current.CancellationToken);

        var shell = TerminalCommand.Build(BrokerDirectory.FromManifest(ManifestPath), manifestPath: ManifestPath);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }

    private static async Task AddAsync(Driving.Session driving, Shell shell, string workspace, string role)
    {
        await shell.Add();

        ((Browsing)driving.Find(Browsing.Name)).Choose(workspace);

        var form = (FormDialog)driving.Find(Adding.Title);

        form.Fill(0, workspace);
        form.Fill(1, role);
        form.Finish(taken: true);

        var asking = (ChoiceDialog)driving.Find(Adding.ConfirmTitle);

        driving.Row(asking.Choices, Adding.Write);
        driving.Enter();

        await shell.Settled;
    }

    /// <summary>Puts something at the catalogue's path that cannot be written over.</summary>
    private void JamTheCatalogue()
    {
        File.Delete(CataloguePath);
        Directory.CreateDirectory(CataloguePath);
    }

    [Fact]
    public async Task The_screen_does_not_claim_nothing_was_written_when_something_was()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        JamTheCatalogue();

        await AddAsync(driving, shell, Workspace, "orders");

        // It really did get that far: the repository is now announcing itself as an agent.
        Assert.True(File.Exists(LaunchFile), "the launch configuration was never written");

        Assert.DoesNotContain(Adding.NothingWritten, shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_screen_names_the_file_that_changed()
    {
        // Not merely dropping the false sentence. The person is at a screen rather than a shell and
        // has no output to scroll back through, so the one place this can be said is here.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        JamTheCatalogue();

        await AddAsync(driving, shell, Workspace, "orders");

        Assert.Contains(Enrolment.LaunchFile, shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_that_really_wrote_nothing_still_says_so()
    {
        // The control, and the reason this is one finding rather than a licence to delete the
        // sentence. **The first version of this probe used a directory that is not a repository and
        // proved nothing**: that is refused at the form, before the confirmation this path goes
        // through at all. FR-009's stranger refusal is the one that reaches the writing phase and
        // still writes nothing, so it is the case that has to keep saying so.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await File.WriteAllTextAsync(
            LaunchFile,
            """
            { "mcpServers": { "parley-acme": { "command": "npx", "args": ["-y", "something"] } } }
            """,
            TestContext.Current.CancellationToken);

        await AddAsync(driving, shell, Workspace, "orders");

        Assert.Contains(Adding.NothingWritten, shell.Said, StringComparison.Ordinal);
    }
}
