using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That the four regions are on screen together, that Tab moves between them, and that opening a
/// conversation leaves the list where it was.
/// </summary>
/// <remarks>
/// <para>
/// SC-001 and User Story 1. The claim being proved is not that each region can render — it is that
/// a person reaches a named conversation's transcript with Tab, arrows and Enter and never types
/// an identifier. Conversation identifiers are opaque and agent-chosen (SPEC.md §5.0) and agent
/// names are <c>role@machine</c>; a surface that asked somebody to type either would be asking
/// them to have read a database first.
/// </para>
/// <para>
/// <b>FR-002 and FR-004 are what this file carries.</b> Focus moves between regions with Tab and
/// within one with the arrow keys, and everything 004 displayed is displayed here — the parity
/// claim the tasks list fills in, checked by a journey rather than by a table.
/// </para>
/// <para>
/// <b>Proved by construction.</b> Nothing but Tab, arrows and Enter is injected, so if any step
/// wanted text there would be none to read and the journey would not finish. The transcript
/// appearing is the proof that no step asked.
/// </para>
/// </remarks>
public sealed class RegionTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public RegionTests()
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
    public async Task All_four_regions_are_on_screen_at_once()
    {
        await SeedAsync();

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        var screen = driving.Text();

        Assert.Contains(ProjectsRegion.RegionTitle, screen, StringComparison.Ordinal);
        Assert.Contains(AgentsRegion.RegionTitle, screen, StringComparison.Ordinal);
        Assert.Contains(ConversationsRegion.RegionTitle, screen, StringComparison.Ordinal);
        Assert.Contains(TranscriptRegion.RegionTitle, screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_project_is_selected_with_its_agents_beside_it_and_no_key_pressed()
    {
        // Scenario 1. A surface that opened on an empty middle and waited for a keystroke would be
        // the wizard this replaced, one screen further in.
        await SeedAsync();

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Equal("alpha", shell.Projects.Selected?.Name.Value);
        Assert.Contains(shell.Agents.Rows, row => row.Name == Orders.Value);
        Assert.NotEmpty(shell.Conversations.Conversations);
    }

    [Fact]
    public void The_surface_opens_and_closes_without_reaching_a_broker()
    {
        // The shortest journey there is, and it must not need the deployment to be up. A surface
        // that could not be opened and left without a broker answering would strand somebody whose
        // broker is exactly what they came to find out about.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        Assert.Contains(ProjectsRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_visits_every_region_in_turn_and_says_which_in_words()
    {
        // Scenario 2, and SC-006's half of it: the focused region is named in the status line, so
        // a screen with every attribute stripped still says where the person is.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var visited = new List<string>();

        for (var step = 0; step < 5; step++)
        {
            visited.Add(shell.Status);
            driving.Tab();
        }

        // The focused region is named first; what the region can do follows it when there is room.
        Assert.Collection(
            visited,
            said => Assert.StartsWith(Shell.FocusPrefix + ProjectsRegion.RegionTitle, said, StringComparison.Ordinal),
            said => Assert.StartsWith(Shell.FocusPrefix + AgentsRegion.RegionTitle, said, StringComparison.Ordinal),
            said => Assert.StartsWith(Shell.FocusPrefix + ConversationsRegion.RegionTitle, said, StringComparison.Ordinal),
            said => Assert.StartsWith(Shell.FocusPrefix + TranscriptRegion.RegionTitle, said, StringComparison.Ordinal),
            said => Assert.StartsWith(Shell.FocusPrefix + ProjectsRegion.RegionTitle, said, StringComparison.Ordinal));
    }

    [Fact]
    public void Shift_tab_goes_back_the_way_it_came()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        driving.Tab();
        driving.Press(Terminal.Gui.Input.Key.Tab.WithShift);

        Assert.StartsWith(Shell.FocusPrefix + ProjectsRegion.RegionTitle, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_is_reached_without_typing_anything()
    {
        // SC-001. Tab, arrows and Enter only -- no text is injected anywhere in this test.
        await SeedAsync();

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();
        driving.Tab();
        driving.Enter();

        await shell.Settled;

        Assert.NotNull(shell.Transcript.Open);
        Assert.Contains("a question for billing", shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Contains(Orders.Value, shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Contains(Billing.Value, shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_transcript_leaves_the_conversation_list_where_it_was()
    {
        // Scenario 3, and most of what the four-region layout is for: the next conversation is one
        // arrow away rather than a journey back out.
        await SeedAsync();

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        var chosen = shell.Conversations.Selected?.Id;

        driving.Tab();
        driving.Tab();
        driving.Enter();

        await shell.Settled;

        Assert.Equal(chosen, shell.Conversations.Selected?.Id);
        Assert.StartsWith(Shell.FocusPrefix + ConversationsRegion.RegionTitle, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_transcript_is_reached_with_the_mouse_alone()
    {
        // FR-003. The same end state, by a route that presses no key at all.
        await SeedAsync();

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.DoubleClick(shell.Conversations.List);

        await shell.Settled;

        Assert.Contains("a question for billing", shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_state_the_regions_name_survives_having_its_colour_stripped()
    {
        // SC-006. The screen a test reads is the screen somebody with no colour sees.
        await SeedAsync();

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        var screen = driving.Text();

        Assert.Contains(Shell.FocusPrefix, screen, StringComparison.Ordinal);
        Assert.Contains(ProjectsRegion.Answering, screen, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", screen, StringComparison.Ordinal);
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
    /// <returns>The directory the surface reads.</returns>
    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));
}
