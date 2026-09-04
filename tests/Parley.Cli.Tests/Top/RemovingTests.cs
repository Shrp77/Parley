using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Configuration;
using Parley.Core;
using Spectre.Console.Testing;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Removing a project from the terminal: kept by default, forgotten without deleting, or
/// destroyed only after its name is typed back.
/// </summary>
/// <remarks>
/// Driven through <see cref="TopCommand.DriveAsync"/> the way every other journey here is. The
/// project's broker is on a port nothing answers on, so the command's wait for it to stop is
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
    public async Task Destroying_takes_the_name_typed_back_and_refuses_anything_else()
    {
        var alpha = await NamedAsync();

        using var console = Interactive();

        Pick(console, ProjectList.Choices([alpha], null), "alpha");
        Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Remove);
        Pick(console, Removing.Choices, Removing.Destroy);

        // A wrong name is refused at the prompt and nothing happens; the right one destroys.
        console.Input.PushTextWithEnter("alhpa");
        console.Input.PushTextWithEnter("alpha");

        // Back on a list with nothing named, which the form is the way out of.
        Pick(console, ProjectList.Choices([], null), ProjectList.Leave);

        await DriveAsync(console);

        Assert.Contains("not this project's name", console.Output, StringComparison.Ordinal);
        Assert.Contains("Destroyed 'alpha'", console.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(alpha.Store), "the store survived");
        Assert.False(File.Exists(alpha.NodeConfig), "the catalogue survived");
        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task Forgetting_deletes_nothing()
    {
        var alpha = await NamedAsync();

        using var console = Interactive();

        Pick(console, ProjectList.Choices([alpha], null), "alpha");
        Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Remove);
        Pick(console, Removing.Choices, Removing.Forget);
        Pick(console, ProjectList.Choices([], null), ProjectList.Leave);

        await DriveAsync(console);

        Assert.Contains("Nothing was deleted", console.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(alpha.Store));
        Assert.True(File.Exists(alpha.NodeConfig));
        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task Keeping_is_first_and_changes_nothing()
    {
        var alpha = await NamedAsync();

        using var console = Interactive();

        Pick(console, ProjectList.Choices([alpha], null), "alpha");
        Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Remove);

        // Enter on the removal menu is the safe answer.
        Assert.Equal(Removing.Keep, Removing.Choices[0]);
        Enter(console);

        Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Back);
        Pick(console, ProjectList.Choices([alpha], null), ProjectList.Leave);

        await DriveAsync(console);

        Assert.True(File.Exists(alpha.Store));
        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Equal("alpha", Assert.Single(left).Name.Value);
    }

    [Fact]
    public async Task An_empty_answer_to_the_typed_name_keeps_the_project()
    {
        var alpha = await NamedAsync();

        using var console = Interactive();

        Pick(console, ProjectList.Choices([alpha], null), "alpha");
        Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Remove);
        Pick(console, Removing.Choices, Removing.Destroy);
        Enter(console);
        Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Back);
        Pick(console, ProjectList.Choices([alpha], null), ProjectList.Leave);

        await DriveAsync(console);

        Assert.Contains("Kept.", console.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(alpha.Store));
    }

    [Fact]
    public async Task Created_from_the_terminal_the_broker_comes_up_and_destroyed_there_it_goes()
    {
        // Lens 1 probe, and the owner's own afternoon end to end: a process serving the
        // manifest with the watcher on, a project created from the form, its broker answering
        // without anything restarted, then destroyed from the menu -- port freed, files gone,
        // the other project untouched throughout.
        var ct = TestContext.Current.CancellationToken;

        var orders = new ProjectSettings(
            ProjectName.Create("orders"),
            Path.Combine(_directory, "orders.db"),
            $"http://127.0.0.1:{FreePort()}",
            null,
            null);

        ProjectManifest.Write(Manifest, [orders]);

        var stamp = File.GetLastWriteTimeUtc(Manifest);
        var serverOutput = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            serverOutput,
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

            // Create "billing" from the form, on a port of the test's choosing.
            var port = FreePort();
            var billingEndpoint = $"http://127.0.0.1:{port}";

            using var console = Interactive();

            Pick(console, ProjectList.Choices([orders], null), ProjectList.Create);
            console.Input.PushTextWithEnter("billing");
            Enter(console);
            console.Input.PushTextWithEnter(billingEndpoint);
            Enter(console);
            Enter(console);

            var billing = new ProjectSettings(
                ProjectName.Create("billing"), string.Empty, billingEndpoint, null, null);

            Pick(console, ProjectList.Choices([orders, billing], null), ProjectList.Leave);

            await DriveAsync(console);

            Assert.True(await AnswersAsync(billingEndpoint, ct), "the project created from the form never came up");

            var store = Path.Combine(Naming.ProposedStore("billing", [orders], null));

            Assert.True(File.Exists(store), $"the broker created no store at {store}");

            // Destroy it from the menu.
            using var again = Interactive();

            Pick(again, ProjectList.Choices([orders, billing], null), "billing");
            Pick(again, ProjectMenu.Choices(seated: false), ProjectMenu.Remove);
            Pick(again, Removing.Choices, Removing.Destroy);
            again.Input.PushTextWithEnter("billing");
            Pick(again, ProjectList.Choices([orders], null), ProjectList.Leave);

            await DriveAsync(again);

            Assert.Contains("Destroyed 'billing'", again.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(store), "the store survived");
            Assert.True(CanBind(billingEndpoint), "the destroyed project's broker kept its port");
            Assert.True(await AnswersAsync(orders.Endpoint, ct, attempts: 5), "the other project was stopped");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
    }

    private async Task<ProjectSettings> NamedAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var alpha = new ProjectSettings(
            ProjectName.Create("alpha"),
            Path.Combine(_directory, "alpha.db"),
            "http://127.0.0.1:1",
            Path.Combine(_directory, "alpha-node.json"),
            null);

        ProjectManifest.Write(Manifest, [alpha]);
        await File.WriteAllTextAsync(alpha.Store, string.Empty, ct);
        await File.WriteAllTextAsync(alpha.NodeConfig!, "{}", ct);

        return alpha;
    }

    private async Task DriveAsync(TestConsole console)
    {
        using var directory = BrokerDirectory.FromManifest(Manifest);
        using var seats = new SeatBook(directory.AgentsFor);

        var exit = await TopCommand.DriveAsync(
            console, directory, seats, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
    }

    private static TestConsole Interactive()
    {
        var console = new TestConsole();

        console.Interactive();
        console.Profile.Width = 200;

        return console;
    }

    private static void Pick(TestConsole console, IReadOnlyList<string> choices, string wanted)
    {
        var at = choices.ToList().IndexOf(wanted);

        Assert.True(at >= 0, $"'{wanted}' is not on the screen it was expected on");

        for (var step = 0; step < at; step++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        Enter(console);
    }

    private static void Enter(TestConsole console) => console.Input.PushKey(ConsoleKey.Enter);
}
