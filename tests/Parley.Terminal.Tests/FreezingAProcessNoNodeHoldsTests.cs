using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the description of freezing an agent no node holds promises no stop: a process a node that is gone left running
/// is told to end with Kill, and one open in an editor stays until whoever opened it closes it (SPEC.md §14, §11.1).
/// </summary>
/// <remarks>
/// Review, lens 3 on fc26cb8a: the row passed its standing to the words, and every test passed with either half of that
/// removed. The keys are pressed and the description read from the dialog that opened.
/// </remarks>
[Integration]
public sealed class FreezingAProcessNoNodeHoldsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_freeze_of_a_left_or_outside_process_says_what_ends_it(bool left)
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

        // A token no node here reported is a process a node that is gone left running; no token is an editor's session.
        using var attached = agents.Attach(
            new Wire.AttachRequest
            {
                Agent = orders.Value,
                Adapter = "mcp",
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                LaunchToken = left ? "from-a-node-that-is-gone" : string.Empty,
                ProcessId = 4242,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "the attach stream said nothing");

        await seated.Shell.RefreshAsync(Ct);
        await seated.Shell.Settled;
        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();

        seated.Session.Row(seated.Shell.Agents.List, orders.Value);
        Assert.Equal(left ? Standing.Left : Standing.Outside, seated.Shell.Agents.SelectedStanding!.Standing);
        seated.Session.Enter();

        var menu = (ChoiceDialog)seated.Session.Find(orders.Value);
        var offered = menu.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty).ToList();
        var freeze = "Freeze " + orders.Value;

        menu.Choices.SelectedItem = offered.IndexOf(freeze);
        Assert.Equal(freeze, menu.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        var describing = (ChoiceDialog)seated.Session.Find(freeze);

        Assert.Contains(left ? "end it with Kill" : "whoever opened it closes it", describing.Described, StringComparison.Ordinal);
        Assert.DoesNotContain("will be stopped once its current turn ends", describing.Described, StringComparison.Ordinal);
    }
}
