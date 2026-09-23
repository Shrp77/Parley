using Parley.Cli;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Terminal.Tests;

/// <summary>
/// Removing a project from the terminal: kept by default, forgotten without deleting, or
/// destroyed only after its name is typed back.
/// </summary>
/// <remarks>
/// Driven through the dialogs the surface actually opens, the way every other journey here is.
/// The project's broker is on a port nothing answers on, so the command's wait for it to stop is
/// skipped and what is exercised is the surface's own confirmation and the command's deleting.
/// </remarks>
public sealed class RemovingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-removing-" + Guid.NewGuid().ToString("N"));

    public RemovingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Manifest => Path.Combine(_directory, "projects.json");

    [Fact]
    public async Task Keeping_is_first_and_changes_nothing()
    {
        var alpha = await NamedAsync();

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Remove();

        // Enter on the removal dialog is the safe answer, and the safe answer is first.
        Assert.Equal(Removing.Keep, Removing.Choices[0]);

        var choosing = (ChoiceDialog)driving.Find(Removing.Title);

        Assert.Equal(Removing.Keep, choosing.Chosen);

        driving.Enter();
        await shell.Settled;

        Assert.Equal(Removing.Kept, shell.Said);
        Assert.True(File.Exists(alpha.Store));
        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Equal("alpha", Assert.Single(left).Name.Value);
    }

    [Fact]
    public async Task Forgetting_deletes_nothing()
    {
        var alpha = await NamedAsync();

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Remove();

        var choosing = (ChoiceDialog)driving.Find(Removing.Title);

        driving.Row(choosing.Choices, Removing.Forget);
        driving.Enter();
        await shell.Settled;

        Assert.Contains("Nothing was deleted", shell.Said, StringComparison.Ordinal);
        Assert.True(File.Exists(alpha.Store));
        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task Destroying_takes_the_name_typed_back_and_refuses_anything_else()
    {
        var alpha = await NamedAsync();

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Remove();

        var choosing = (ChoiceDialog)driving.Find(Removing.Title);

        driving.Row(choosing.Choices, Removing.Destroy);
        driving.Enter();

        var asking = (TextDialog)driving.Find(Removing.Destroy);

        // A wrong name is refused while it is being typed, and the button that goes ahead is
        // refused with it.
        driving.Type("alhpa");

        Assert.Equal(Removing.WrongName, asking.Problem);

        asking.Finish(taken: true);
        await shell.Settled;

        Assert.True(File.Exists(alpha.Store), "a wrong name destroyed the store anyway");
        Assert.True(ProjectManifest.TryRead(Manifest, out var still, out _));
        Assert.Single(still);

        // The right one destroys.
        await shell.Remove();

        var again = (ChoiceDialog)driving.Find(Removing.Title);

        driving.Row(again.Choices, Removing.Destroy);
        driving.Enter();

        var typing = (TextDialog)driving.Find(Removing.Destroy);

        driving.Type("alpha");

        Assert.Equal(string.Empty, typing.Problem);

        typing.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("Destroyed 'alpha'", shell.Said, StringComparison.Ordinal);
        Assert.False(File.Exists(alpha.Store), "the store survived");
        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task An_empty_answer_to_the_typed_name_keeps_the_project()
    {
        // Escape, and an empty field, are both "no". Neither is an answer that destroys anything,
        // and the difference between them is not one a person should have to know.
        var alpha = await NamedAsync();

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Remove();

        var choosing = (ChoiceDialog)driving.Find(Removing.Title);

        driving.Row(choosing.Choices, Removing.Destroy);
        driving.Enter();

        var asking = (TextDialog)driving.Find(Removing.Destroy);

        asking.Finish(taken: true);
        await shell.Settled;

        Assert.Equal(Removing.Kept, shell.Said);
        Assert.True(File.Exists(alpha.Store));
    }

    [Fact]
    public async Task Escape_at_the_name_keeps_it_too()
    {
        var alpha = await NamedAsync();

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Remove();

        var choosing = (ChoiceDialog)driving.Find(Removing.Title);

        driving.Row(choosing.Choices, Removing.Destroy);
        driving.Enter();
        driving.Escape();
        await shell.Settled;

        Assert.Equal(Removing.Kept, shell.Said);
        Assert.True(File.Exists(alpha.Store));
    }

    [Fact]
    public async Task Created_from_the_terminal_the_broker_comes_up_and_destroyed_there_it_goes()
    {
        // The owner's own afternoon end to end: a process serving the manifest with the watcher
        // on, a project created from the form, its broker answering without anything restarted,
        // then destroyed -- port freed, files gone, the other project untouched throughout.
        var ct = TestContext.Current.CancellationToken;

        var orders = new ProjectSettings(
            ProjectName.Create("orders"),
            Path.Combine(_directory, "orders.db"),
            $"http://127.0.0.1:{FreePort()}",
            null);

        ProjectManifest.Write(Manifest, [orders]);

        var stamp = File.GetLastWriteTimeUtc(Manifest);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            new StringWriter(),
            TextWriter.Null,
            reread: () =>
            {
                var now = File.GetLastWriteTimeUtc(Manifest);

                if (now == stamp)
                {
                    return null;
                }

                stamp = now;

                return ProjectManifest.TryRead(Manifest, out var current, out _) ? current : null;
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "the first project never came up");

            var billingEndpoint = $"http://127.0.0.1:{FreePort()}";
            var store = Naming.ProposedStore("billing", [orders], null);

            using var driving = Driving.Open();
            using var shell = await OpenAsync(driving);

            await shell.Create();

            var form = (FormDialog)driving.Find(Naming.CreateTitle);

            form.Fill(0, "billing");
            form.Fill(1, store);
            form.Fill(2, billingEndpoint);
            form.Finish(taken: true);

            await shell.Settled;

            Assert.Contains(Naming.Named, shell.Said, StringComparison.Ordinal);
            Assert.True(
                await AnswersAsync(billingEndpoint, ct),
                "the project created from the form never came up");

            Assert.True(File.Exists(store), $"the broker created no store at {store}");

            // Destroy it.
            driving.Row(shell.Projects.List, "billing");
            await shell.Settled;

            Assert.Equal("billing", shell.Projects.Selected?.Name.Value);

            await shell.Remove();

            var choosing = (ChoiceDialog)driving.Find(Removing.Title);

            driving.Row(choosing.Choices, Removing.Destroy);
            driving.Enter();

            var typing = (TextDialog)driving.Find(Removing.Destroy);

            driving.Type("billing");
            typing.Finish(taken: true);

            await shell.Settled;

            Assert.Contains("Destroyed 'billing'", shell.Said, StringComparison.Ordinal);
            Assert.False(File.Exists(store), "the store survived");
            Assert.True(CanBind(billingEndpoint), "the destroyed project's broker kept its port");
            Assert.True(
                await AnswersAsync(orders.Endpoint, ct, attempts: 5),
                "the other project was stopped");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
    }

    /// <summary>Opens the surface on this test's manifest.</summary>
    /// <param name="driving">The application.</param>
    /// <returns>The surface, with its projects read.</returns>
    private async Task<Shell> OpenAsync(Driving.Session driving)
    {
        var directory = BrokerDirectory.FromManifest(Manifest);
        var shell = TerminalCommand.Build(directory, manifestPath: Manifest);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }

    private async Task<ProjectSettings> NamedAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var alpha = new ProjectSettings(
            ProjectName.Create("alpha"),
            Path.Combine(_directory, "alpha.db"),
            "http://127.0.0.1:1",
            null);

        ProjectManifest.Write(Manifest, [alpha]);
        await File.WriteAllTextAsync(alpha.Store, string.Empty, ct);

        return alpha;
    }
}
