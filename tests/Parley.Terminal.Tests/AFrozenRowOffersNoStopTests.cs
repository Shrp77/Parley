using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a frozen agent's row offers neither Stop nor Restart, whether its node holds its process or it is open outside
/// Parley (SPEC.md §14, §11.1).
/// </summary>
/// <remarks>
/// A freeze already stops a process its node holds once its turn ends, and the broker refuses to restart a frozen agent, so
/// both entries would offer what is done already or refused. The row decides from the standing phrase, and the frozen
/// phrases were held only for a process a node that is gone left running. Review, lens 3 run 1 on 6fde4ef3: the row's
/// frozen check passed every test with it removed. Every case presses the key.
/// </remarks>
[Integration]
[Slow]
public sealed class AFrozenRowOffersNoStopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_frozen_running_agent_offers_unfreeze_and_neither_stop_restart_nor_kill(bool startedByParley)
    {
        await using var seated = await Seated.OpenAsync(Ct);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(seated.Address);
        var nodes = new Parley.Transport.Nodes.NodesClient(channel);
        var agents = new Parley.Transport.Agents.AgentsClient(channel);
        var orders = Seated.Orders;

        using var joined = nodes.Join(
            new Parley.Transport.JoinRequest
            {
                Machine = orders.Machine,
                StartableRoles = { orders.Role },
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                ReportsTurns = true,
            },
            cancellationToken: Ct);

        Assert.True(await joined.ResponseStream.MoveNext(Ct), "the node was never told it joined");

        if (startedByParley)
        {
            _ = await nodes.ReportLaunchAsync(
                new Parley.Transport.LaunchReport { Machine = orders.Machine, Agent = orders.Value, Token = "t-1" },
                cancellationToken: Ct);
        }

        using var attached = agents.Attach(
            new Parley.Transport.AttachRequest
            {
                Agent = orders.Value,
                Adapter = "test",
                Tier = Parley.Transport.AgentTier.Pull,
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                LaunchToken = startedByParley ? "t-1" : string.Empty,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "the attach stream said nothing");
        Assert.True((await seated.Operators.FreezeAsync(orders, cancellationToken: Ct)).Changed);

        await seated.Shell.RefreshAsync(Ct);
        await seated.Shell.Settled;
        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();

        var offered = Open(seated, orders.Value);

        // Read as frozen, so the row offers the way out of the freeze.
        Assert.Contains("Unfreeze " + orders.Value, offered);
        Assert.DoesNotContain(Shell.StopThisTitle + orders.Value, offered);
        Assert.DoesNotContain(Shell.RestartThisTitle + orders.Value, offered);
        Assert.DoesNotContain(Shell.KillThisTitle + orders.Value, offered);
    }

    /// <summary>Puts the cursor on a row, presses Enter, and reads what the menu offers.</summary>
    private static IReadOnlyList<string> Open(Seated seated, string row)
    {
        seated.Session.Row(seated.Shell.Agents.List, row);
        seated.Session.Enter();

        var menu = (ChoiceDialog)seated.Session.Find(row);

        return [.. menu.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty)];
    }
}
