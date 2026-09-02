using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That the whole journey is reachable by choosing, and never by typing.
/// </summary>
/// <remarks>
/// <para>
/// SC-001: a person goes from launching the surface to reading a named conversation without typing
/// any identifier — every step chosen from what is on screen. Conversation identifiers are opaque
/// and agent-chosen (§5.0) and agent names are <c>role@machine</c>; a surface that asked a person
/// to type either would be asking them to have read a database.
/// </para>
/// <para>
/// <b>The claim is proved by construction rather than asserted.</b> Nothing but arrow keys and
/// Enter is pushed onto the input — no text at all. If any step along the way wanted an
/// identifier typed, its prompt would find nothing to read and the journey would not finish. So
/// the test reaching a transcript <i>is</i> the proof that none of them asked.
/// </para>
/// <para>
/// The console is passed to <see cref="TopCommand.DriveAsync"/> rather than set on
/// <c>AnsiConsole.Console</c>. That static is process-global and xUnit runs test classes in
/// parallel, so redirecting it would redirect it for whatever else was running — the shape of the
/// bug <c>BannedApiAnalyzers</c> exists here to prevent, reached from a different direction.
/// </para>
/// </remarks>
public sealed class NavigationTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public NavigationTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task A_conversation_is_reached_without_typing_anything()
    {
        await SeedAsync();

        using var console = Interactive();

        // Every step chosen by name rather than by a counted number of keys, so a menu gaining
        // an entry moves the highlight rather than silently sending this somewhere else.
        Pick(console, Projects(), "alpha");
        Pick(console, Menu(), ProjectMenu.Conversations);
        Enter(console);                                   // the first conversation
        Pick(console, Menu(), ProjectMenu.Back);
        Pick(console, Projects(), ProjectList.Leave);

        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var exit = await TopCommand.DriveAsync(
            console, directory, seats, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);

        var drawn = console.Output;

        // The transcript was reached, and it is the transcript rather than the list: the message
        // body only ever appears once the conversation is open.
        Assert.Contains("a question for billing", drawn, StringComparison.Ordinal);
        Assert.Contains(Orders.Value, drawn, StringComparison.Ordinal);
        Assert.Contains(Billing.Value, drawn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agents_are_reached_the_same_way()
    {
        await SeedAsync();

        using var console = Interactive();

        Pick(console, Projects(), "alpha");
        Pick(console, Menu(), ProjectMenu.Agents);
        Pick(console, Menu(), ProjectMenu.Back);
        Pick(console, Projects(), ProjectList.Leave);

        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var exit = await TopCommand.DriveAsync(
            console, directory, seats, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);

        // The registry, with the one column a person is actually looking for.
        Assert.Contains(Orders.Value, console.Output, StringComparison.Ordinal);
        Assert.Contains("not stated", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_the_first_screen_ends_it_without_reaching_a_broker()
    {
        // The shortest journey there is, and it must not need the deployment to be up. A first
        // screen that could not be left without a broker answering would strand somebody whose
        // broker is exactly what they came to find out about.
        using var console = Interactive();

        Pick(console, Projects(), ProjectList.Leave);

        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var exit = await TopCommand.DriveAsync(
            console, directory, seats, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
    }

    /// <summary>A conversation between two of the seeded agents.</summary>
    /// <returns>When the broker has recorded it.</returns>
    private async Task SeedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, ct);
        await broker.StartAsync(Billing, T0, ct);

        await broker.NotifyAsync(
            Orders, Billing, "model-a", """{"text":"a question for billing"}""",
            T0.AddMinutes(1), "chat", ct);
    }

    /// <summary>One project, pointed at the broker this test started.</summary>
    /// <returns>The directory the surface navigates.</returns>
    /// <remarks>
    /// Built from an explicit list rather than from a manifest on disk, which is the constructor
    /// that exists for exactly this: the projects given are the answer for the object's life, so
    /// nothing here reads the manifest of the machine running the suite.
    /// </remarks>
    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, Address, null, null)],
            _ => GrpcChannel.ForAddress(Address));

    /// <summary>Where the test's broker is listening.</summary>
    private string Address => _broker.Address;

    /// <summary>A console that answers prompts from pushed keys.</summary>
    /// <returns>The console, which also records everything drawn on it.</returns>
    private static TestConsole Interactive()
    {
        var console = new TestConsole();

        console.Interactive();
        console.Profile.Width = 200;

        return console;
    }

    /// <summary>Chooses one entry by name, whatever position it is in.</summary>
    /// <param name="console">The console to push the keys onto.</param>
    /// <param name="choices">The entries the screen is about to offer.</param>
    /// <param name="wanted">The one to take.</param>
    /// <remarks>
    /// The lists come from the screens themselves, so this cannot drift from what is drawn. A
    /// test that hard-coded the number of arrow presses would still pass after a menu grew — it
    /// would simply be selecting a different entry, and the assertion it then failed would point
    /// anywhere but here.
    /// </remarks>
    private static void Pick(
        TestConsole console, IReadOnlyList<string> choices, string wanted)
    {
        var at = choices.ToList().IndexOf(wanted);

        Assert.True(at >= 0, $"'{wanted}' is not on the screen it was expected on");

        for (var step = 0; step < at; step++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        Enter(console);
    }

    /// <summary>What the first screen offers for this test's one project.</summary>
    /// <returns>The entries.</returns>
    private IReadOnlyList<string> Projects() =>
        ProjectList.Choices(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, Address, null, null)]);

    /// <summary>What the project menu offers while nobody is seated.</summary>
    /// <returns>The entries.</returns>
    private static IReadOnlyList<string> Menu() => ProjectMenu.Choices(seated: false);

    /// <summary>Takes whatever is highlighted.</summary>
    /// <param name="console">The console to push the key onto.</param>
    private static void Enter(TestConsole console) =>
        console.Input.PushKey(ConsoleKey.Enter);
}
