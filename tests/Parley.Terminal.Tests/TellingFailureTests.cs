using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What the terminal does when the readback cannot be made.
/// </summary>
/// <remarks>
/// <para>
/// <b>An empty dialog and an unreachable broker look identical, and mean opposite things.</b> One
/// says the operator has told this agent nothing; the other says nobody knows what they told it.
/// Getting that wrong is the same failure the install scripts were fixed for in 007 — a guard that
/// fails open reports "nothing in flight" when it means "could not ask".
/// </para>
/// <para>
/// This surface has been bitten by the neighbouring version of it too: a message computed and drawn
/// nowhere. So the assertion is that something is *said*, not merely that nothing opened.
/// </para>
/// </remarks>
public sealed class TellingFailureTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static BrokerDirectory Nowhere() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static BrokerDirectory Living(string address) =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, address, null, null)],
            _ => GrpcChannel.ForAddress(address));

    [Fact]
    public async Task A_broker_that_dies_between_the_two_reads_is_said_rather_than_shown_as_silence()
    {
        // **The fixture took two attempts and the first one taught something.** Pointing the whole
        // surface at a dead address does not reach this path at all: with no registry there is no
        // agent row, so the action short-circuits before any call — which is correct, and is what
        // the two probes below assert.
        //
        // The failure worth catching needs a broker that answers the registry and then goes: a
        // person opens a project, the machine drops, and they press the key. An empty dialog and an
        // unreachable broker look identical and mean opposite things — one says nothing was told,
        // the other says nobody knows what was told.
        var token = TestContext.Current.CancellationToken;

        using var connection = ParleyDatabase.OpenInMemory();
        var broker = LoopbackOperator.Start(connection);

        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            token);

        using var driving = Driving.Open();
        using var directory = Living(broker.Address);
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(token);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Orders.Value);
        await shell.Settled;

        Assert.NotNull(shell.Agents.Selected);

        // And now it is gone.
        await broker.DisposeAsync();

        await shell.ShowTold();
        await shell.Settled;

        Assert.Null(shell.Told);

        Assert.False(
            string.IsNullOrWhiteSpace(shell.Said),
            "the surface reported nothing at all about a broker it could not reach");

        driving.App.LayoutAndDraw(true);

        // And it reached the screen. Every message on this surface was once computed and drawn
        // nowhere; a new one is exactly where that would recur.
        Assert.Contains(shell.Said, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_offered_when_the_cursor_is_on_no_agent_at_all()
    {
        // An empty project has no row under the cursor, so there is nobody to read instructions
        // for. Offering the action there would put a key on screen that reports a failure.
        using var driving = Driving.Open();
        using var directory = Nowhere();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Null(shell.Agents.Selected);

        Assert.DoesNotContain(
            shell.Actions(),
            action => action.Label.StartsWith(Shell.ToldTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_it_with_no_agent_under_the_cursor_does_nothing_and_says_nothing()
    {
        // Reached by the key rather than the menu, which is the route that skips the offer. It must
        // be a no-op rather than an error: a key pressed on an empty list has not failed.
        using var driving = Driving.Open();
        using var directory = Nowhere();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        var before = shell.Said;

        await shell.ShowTold();
        await shell.Settled;

        Assert.Null(shell.Told);
        Assert.Equal(before, shell.Said);

    }
}
