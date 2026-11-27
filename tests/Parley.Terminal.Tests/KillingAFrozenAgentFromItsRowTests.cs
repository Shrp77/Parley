using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the description of ending a frozen agent's left process does not tell the person new work starts it again
/// (SPEC.md §10.2 <c>kill</c>, §14: a freeze gates starting).
/// </summary>
/// <remarks>
/// Review, lens 3 on 6257cf80: the row's frozen check passed every test with it removed. The keys are pressed and the
/// description read from the dialog that opened.
/// </remarks>
[Integration]
public sealed class KillingAFrozenAgentFromItsRowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_kill_of_a_frozen_agents_left_process_says_it_stays_frozen()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(seated.Address);
        var nodes = new Wire.Nodes.NodesClient(channel);
        var agents = new Wire.Agents.AgentsClient(channel);
        var orders = Seated.Orders;

        using var joined = nodes.Join(
            new Wire.JoinRequest
            {
                Machine = orders.Machine,
                StartableRoles = { orders.Role },
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                ReportsTurns = true,
            },
            cancellationToken: Ct);

        Assert.True(await joined.ResponseStream.MoveNext(Ct), "the node was never told it joined");

        // A token no node here reported: a process a node that is gone left running.
        using var attached = agents.Attach(
            new Wire.AttachRequest
            {
                Agent = orders.Value,
                Adapter = "mcp",
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                LaunchToken = "from-a-node-that-is-gone",
                ProcessId = 4242,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "the attach stream said nothing");
        Assert.True((await seated.Operators.FreezeAsync(orders, cancellationToken: Ct)).Changed);

        await seated.Shell.RefreshAsync(Ct);
        await seated.Shell.Settled;
        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();

        seated.Session.Row(seated.Shell.Agents.List, orders.Value);
        Assert.Equal(Standing.FrozenLeft, seated.Shell.Agents.SelectedStanding!.Standing);
        seated.Session.Enter();

        var menu = (ChoiceDialog)seated.Session.Find(orders.Value);
        var offered = menu.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty).ToList();
        var kill = Shell.KillThisTitle + orders.Value;

        menu.Choices.SelectedItem = offered.IndexOf(kill);
        Assert.Equal(kill, menu.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        var describing = (ChoiceDialog)seated.Session.Find(kill);

        Assert.Equal(Parley.Client.Rendering.StoppingWords.Killing(orders.Value, frozen: true), describing.Described);
        Assert.DoesNotContain("The next work for it starts it again", describing.Described, StringComparison.Ordinal);
    }
}
