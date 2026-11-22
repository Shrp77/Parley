using Parley.Client.Views;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a frozen agent's row offers neither Stop nor Restart while the process a node started is still finishing its turn
/// (SPEC.md §14: the freeze already stops it at its turn's end, and the broker refuses a restart of a frozen agent).
/// </summary>
/// <remarks>
/// bd727746 left both out for a frozen row, and no test held it: with the condition removed every row-menu and freeze test
/// passed (review, lens 1 on 6fde4ef3).
/// </remarks>
[Integration]
public sealed class AFrozenRowOffersNeitherStopNorRestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_frozen_agent_whose_node_holds_its_process_is_offered_neither(bool kept)
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

        _ = await nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = orders.Machine, Agent = orders.Value, Token = "t-1" }, cancellationToken: Ct);

        using var attached = agents.Attach(
            new Wire.AttachRequest
            {
                Agent = orders.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                LaunchToken = "t-1",
                ProcessId = 5151,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "the attach stream said nothing");
        Assert.True((await seated.Operators.FreezeAsync(orders, kept, Ct)).Changed);

        await seated.Shell.RefreshAsync(Ct);
        await seated.Shell.Settled;
        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();

        seated.Session.Row(seated.Shell.Agents.List, orders.Value);
        seated.Session.Enter();

        var menu = (ChoiceDialog)seated.Session.Find(orders.Value);
        var offered = menu.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty).ToList();

        Assert.Equal(Standing.Freezing, seated.Shell.Agents.SelectedStanding!.Standing);
        Assert.Contains("Unfreeze " + orders.Value, offered);
        Assert.DoesNotContain(Shell.StopThisTitle + orders.Value, offered);
        Assert.DoesNotContain(Shell.RestartThisTitle + orders.Value, offered);
        Assert.DoesNotContain(Shell.KillThisTitle + orders.Value, offered);
    }
}
