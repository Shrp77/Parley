using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Configuration;
using Parley.Core;
using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Naming a project from the first screen: proposed, checked as typed, and explained.
/// </summary>
/// <remarks>
/// <para>
/// The first version of the form had five blank prompts and validated nothing until the end.
/// The first person to use it answered every one and was then told the name had to be lowercase,
/// and had found the paths it wanted by reading a scheduled task. Each of those is a case here.
/// </para>
/// <para>
/// Driven through <see cref="TopCommand.DriveAsync"/> with a test console, the way every other
/// journey on this surface is, and with the installed entry passed in rather than read — a form
/// that pre-fills from whatever the developer's own scheduler holds would pass on one machine.
/// </para>
/// </remarks>
public sealed class NamingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-naming-" + Guid.NewGuid().ToString("N"));

    public NamingTests() => Directory.CreateDirectory(_directory);

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
    public async Task A_new_project_is_checked_as_it_is_typed_and_proposed_its_own_paths()
    {
        using var console = Interactive();

        // Nothing named yet, so the first screen offers the form and nothing else to choose.
        Pick(console, ProjectList.Choices([], null), ProjectList.Create);

        // Refused at the prompt, not after four more answers.
        console.Input.PushTextWithEnter("Alpha");
        console.Input.PushTextWithEnter("alpha");

        // The proposed store is accepted; the proposed port is replaced with one nothing answers
        // on, so the list drawn afterwards asks nobody real.
        Enter(console);
        console.Input.PushTextWithEnter("http://127.0.0.1:1");
        Enter(console);
        console.Input.PushTextWithEnter("the first one");

        var alpha = new ProjectSettings(
            ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, "the first one");

        Pick(console, ProjectList.Choices([alpha], null), ProjectList.Leave);

        await DriveAsync(console, installed: null);

        Assert.True(ProjectManifest.TryRead(Manifest, out var projects, out var problem), problem);

        var named = Assert.Single(projects);

        Assert.Equal("alpha", named.Name.Value);
        Assert.Equal(Path.Combine(Naming.ProposedStore("alpha", [], null)), named.Store);
        Assert.EndsWith("alpha.db", named.Store, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:1", named.Endpoint);
        Assert.Null(named.NodeConfig);
        Assert.Equal("the first one", named.Description);

        var drawn = console.Output;

        Assert.Contains("lowercase letters", drawn, StringComparison.Ordinal);
        Assert.Contains("Named.", drawn, StringComparison.Ordinal);

        // Nothing starts it, and the form says so rather than leaving the first person to use
        // it assuming a broker was spawned.
        Assert.Contains("Nothing starts it yet", drawn, StringComparison.Ordinal);

        // The description the form asked for is on the list it returns to.
        Assert.Contains("the first one", drawn[drawn.IndexOf("Named.", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_running_deployment_is_named_with_Enter_alone()
    {
        // Every answer is already in the autostart entry, so the form is pre-filled from it and a
        // person presses Enter through it. The one thing typed is the name.
        var entry = new InstalledEntry(
            Store: Path.Combine(_directory, "running.db"),
            Listen: "http://127.0.0.1:1",
            NodeConfig: Path.Combine(_directory, "node.json"),
            Console: null,
            Manifest: null);

        using var console = Interactive();

        Pick(console, ProjectList.Choices([], entry), ProjectList.NameRunning);

        console.Input.PushTextWithEnter("prod");
        Enter(console);
        Enter(console);
        Enter(console);
        Enter(console);

        var prod = new ProjectSettings(
            ProjectName.Create("prod"), entry.Store!, entry.Listen!, entry.NodeConfig, null);

        // Named, so it is no longer offered: the entry's store is claimed.
        var afterwards = ProjectList.Choices([prod], entry);

        Assert.DoesNotContain(ProjectList.NameRunning, afterwards);

        Pick(console, afterwards, ProjectList.Leave);

        await DriveAsync(console, entry);

        Assert.True(ProjectManifest.TryRead(Manifest, out var projects, out var problem), problem);

        var named = Assert.Single(projects);

        Assert.Equal(entry.Store, named.Store);
        Assert.Equal(entry.Listen, named.Endpoint);
        Assert.Equal(entry.NodeConfig, named.NodeConfig);

        // And what to do next: the entry serves one store and will not serve this manifest until
        // it is reinstalled without one.
        Assert.Contains("Re-run `parley autostart install`", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_project_is_not_proposed_the_first_ones_port()
    {
        // The default port when nothing holds it — an MCP server told nothing about a broker
        // looks there — and a free one otherwise, because a second deployment cannot share it.
        Assert.Equal(Naming.DefaultEndpoint, Naming.FreeEndpoint([]));

        var second = Naming.FreeEndpoint([Naming.DefaultEndpoint]);

        Assert.NotEqual(Naming.DefaultEndpoint, second);
        Assert.StartsWith("http://127.0.0.1:", second, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_a_store_or_a_port_already_taken_is_refused_at_the_prompt()
    {
        var alpha = new ProjectSettings(
            ProjectName.Create("alpha"), Path.Combine(_directory, "alpha.db"), "http://127.0.0.1:1", null, null);

        Assert.Contains("lowercase", Naming.NameProblem("Alpha", [alpha]), StringComparison.Ordinal);
        Assert.Contains("already named", Naming.NameProblem("alpha", [alpha]), StringComparison.Ordinal);
        Assert.Null(Naming.NameProblem("beta", [alpha]));

        Assert.Contains("already owns", Naming.StoreProblem(alpha.Store, [alpha]), StringComparison.Ordinal);
        Assert.Null(Naming.StoreProblem(Path.Combine(_directory, "beta.db"), [alpha]));

        Assert.Contains("already listens", Naming.EndpointProblem("http://127.0.0.1:1/", [alpha]), StringComparison.Ordinal);
        Assert.Contains("not an absolute address", Naming.EndpointProblem("nowhere", [alpha]), StringComparison.Ordinal);
        Assert.Null(Naming.EndpointProblem("http://127.0.0.1:2", [alpha]));
    }

    [Fact]
    public void The_running_deployment_is_offered_only_while_nothing_names_it()
    {
        var store = Path.Combine(_directory, "running.db");
        var explicitEntry = new InstalledEntry(store, null, null, null, null);
        var manifestEntry = new InstalledEntry(null, null, null, null, Manifest);

        var claimed = new ProjectSettings(
            ProjectName.Create("prod"), store, "http://127.0.0.1:1", null, null);

        Assert.True(ProjectList.CanNameRunning([], explicitEntry));
        Assert.False(ProjectList.CanNameRunning([claimed], explicitEntry));
        Assert.False(ProjectList.CanNameRunning([], manifestEntry));
        Assert.False(ProjectList.CanNameRunning([], null));
    }

    private async Task DriveAsync(TestConsole console, InstalledEntry? installed)
    {
        using var directory = BrokerDirectory.FromManifest(Manifest);
        using var seats = new SeatBook(directory.AgentsFor);

        var exit = await TopCommand.DriveAsync(
            console, directory, seats, TestContext.Current.CancellationToken, installed);

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
