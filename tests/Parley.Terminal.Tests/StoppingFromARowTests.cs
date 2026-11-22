using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// That stopping, restarting and killing an agent from its row are each described first, do nothing until the second
/// choice is taken, and then ask the agent's node for exactly that (SPEC.md §10.2 <c>stop</c>, <c>restart</c>,
/// <c>kill</c>; §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case presses the keys and reads what the node was sent.</b> A node is joined to the fixture's broker for the
/// agents' machine, and the agent is attached as a process that node started (or, for a kill, as one a node that is gone
/// left running), so what the choice asks for arrives as a command and is read there.
/// </para>
/// <para>
/// <b>"Nothing was asked" is held by what arrives next, not by waiting.</b> After the choice that changes nothing, the
/// node is asked to start the fixture's other agent; commands arrive in the order the broker sent them, so that start being
/// the first one read says no stop or kill went before it.
/// </para>
/// </remarks>
[Integration]
public sealed class StoppingFromARowTests
{
    private const string KeepItRunning = "Keep it running";
    private const long ProcessId = 4242;

    private static readonly string Stop = "Stop " + Seated.Orders.Value;
    private static readonly string Restart = "Restart " + Seated.Orders.Value;
    private static readonly string Kill = "Kill process of " + Seated.Orders.Value;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_is_described_and_keeping_it_running_asks_its_node_for_nothing(bool restart)
    {
        await using var seated = await Seated.OpenAsync(Ct);
        await using var node = await JoinedNode.JoinAsync(seated.Address, Ct);
        using var agent = await node.RunsAsync(Seated.Orders.Value, Ct);
        var act = restart ? Restart : Stop;

        await OnAgentsAsync(seated);

        var offered = Open(seated, Seated.Orders.Value);

        Assert.Contains(Stop, offered);
        Assert.Contains(Restart, offered);
        Assert.DoesNotContain(Kill, offered);

        await ChooseAsync(seated, offered, act);

        var describing = (ChoiceDialog)seated.Session.Find(act);

        Assert.Equal([KeepItRunning, act], Choices(describing));
        Assert.Contains(KeepItRunning, seated.Session.Text(), StringComparison.Ordinal);

        // The first choice is the one the cursor opens on, and it is the one that does nothing.
        Assert.Equal(KeepItRunning, describing.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        Assert.False(seated.Session.Has(act), "the description stayed open");
        await AssertNothingWasAskedAsync(seated, node);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Choosing_the_stop_asks_its_node_to_stop_it_and_to_start_it_again_only_for_a_restart(bool restart)
    {
        await using var seated = await Seated.OpenAsync(Ct);
        await using var node = await JoinedNode.JoinAsync(seated.Address, Ct);
        using var agent = await node.RunsAsync(Seated.Orders.Value, Ct);
        var act = restart ? Restart : Stop;

        await OnAgentsAsync(seated);
        await ChooseAsync(seated, Open(seated, Seated.Orders.Value), act);

        var describing = (ChoiceDialog)seated.Session.Find(act);

        seated.Session.Row(describing.Choices, act);
        Assert.Equal(act, describing.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        var command = await node.NextAsync(Patience, Ct);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, command.CommandCase);
        Assert.Equal(Seated.Orders.Value, command.Stop.Agent);
        Assert.Equal(restart, command.Stop.Restart);
    }

    [Fact]
    public async Task Killing_a_process_a_node_left_running_is_described_and_keeping_it_running_asks_for_nothing()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        await using var node = await JoinedNode.JoinAsync(seated.Address, Ct);
        using var left = await node.LeftRunningAsync(Seated.Orders.Value, ProcessId, Ct);

        await OnAgentsAsync(seated);

        var offered = Open(seated, Seated.Orders.Value);

        // Ended, not stopped: nothing holds it to wait for the end of its turn.
        Assert.Equal(Standing.Left, seated.Shell.Agents.SelectedStanding!.Standing);
        Assert.Contains(Kill, offered);
        Assert.DoesNotContain(Stop, offered);
        Assert.DoesNotContain(Restart, offered);

        await ChooseAsync(seated, offered, Kill);

        var describing = (ChoiceDialog)seated.Session.Find(Kill);

        Assert.Equal([KeepItRunning, Kill], Choices(describing));
        Assert.Equal(KeepItRunning, describing.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        Assert.False(seated.Session.Has(Kill), "the description stayed open");
        await AssertNothingWasAskedAsync(seated, node);
    }

    [Fact]
    public async Task Choosing_the_kill_tells_the_node_which_process_to_end()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        await using var node = await JoinedNode.JoinAsync(seated.Address, Ct);
        using var left = await node.LeftRunningAsync(Seated.Orders.Value, ProcessId, Ct);

        await OnAgentsAsync(seated);
        await ChooseAsync(seated, Open(seated, Seated.Orders.Value), Kill);

        var describing = (ChoiceDialog)seated.Session.Find(Kill);

        seated.Session.Row(describing.Choices, Kill);
        Assert.Equal(Kill, describing.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);

        var command = await node.NextAsync(Patience, Ct);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Kill, command.CommandCase);
        Assert.Equal(Seated.Orders.Value, command.Kill.Agent);
        Assert.Equal(ProcessId, command.Kill.ProcessId);
    }

    /// <summary>Holds that the node has been sent nothing, by asking it for something else and reading that first.</summary>
    private static async Task AssertNothingWasAskedAsync(Seated seated, JoinedNode node)
    {
        Assert.True(await seated.Operators.StartAsync(Seated.Billing, "to see what the node was sent before this", Ct));

        var first = await node.NextAsync(Patience, Ct);

        Assert.True(
            first.CommandCase == Wire.NodeCommand.CommandOneofCase.Start,
            $"the choice that changes nothing asked the node for {first.CommandCase}");
        Assert.Equal(Seated.Billing.Value, first.Start.Agent);
    }

    /// <summary>Reads the registry again, now that an agent is attached, and puts the cursor in the agents pane.</summary>
    private static async Task OnAgentsAsync(Seated seated)
    {
        await seated.Shell.RefreshAsync(Ct);
        await seated.Shell.Settled;
        await Walking.GoAsync(seated.Shell, Viewing.Project);
        seated.Focus<AgentsRegion>();
    }

    /// <summary>Puts the cursor on a row, presses Enter, and reads what the menu offers.</summary>
    private static IReadOnlyList<string> Open(Seated seated, string row)
    {
        seated.Session.Row(seated.Shell.Agents.List, row);

        Assert.True(seated.Shell.Agents.SelectedStanding!.Running, "the fixture's agent does not read as running");

        seated.Session.Enter();

        return Choices((ChoiceDialog)seated.Session.Find(row));
    }

    /// <summary>Presses Enter on one entry of the open row menu, by its whole label, and waits for what that starts.</summary>
    private static async Task ChooseAsync(Seated seated, IReadOnlyList<string> offered, string entry)
    {
        var menu = (ChoiceDialog)seated.Session.Find(Seated.Orders.Value);

        menu.Choices.SelectedItem = offered.ToList().IndexOf(entry);
        Assert.Equal(entry, menu.Chosen);

        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);
    }

    private static IReadOnlyList<string> Choices(ChoiceDialog dialog) =>
        [.. dialog.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty)];

    /// <summary>A node joined to the fixture's broker for the agents' machine, which keeps what it is sent.</summary>
    private sealed class JoinedNode : IAsyncDisposable
    {
        private readonly GrpcChannel _channel;
        private readonly Wire.Nodes.NodesClient _nodes;
        private readonly AsyncServerStreamingCall<Wire.NodeCommand> _stream;
        private readonly CancellationTokenSource _ending = new();
        private readonly Channel<Wire.NodeCommand> _commands = Channel.CreateUnbounded<Wire.NodeCommand>();
        private readonly Task _pump;

        private JoinedNode(GrpcChannel channel, Wire.Nodes.NodesClient nodes, AsyncServerStreamingCall<Wire.NodeCommand> stream)
        {
            _channel = channel;
            _nodes = nodes;
            _stream = stream;
            _pump = Task.Run(PumpAsync);
        }

        /// <summary>Joins as the node on the machine both of the fixture's agents are on.</summary>
        public static async Task<JoinedNode> JoinAsync(string address, CancellationToken cancellationToken)
        {
            var channel = GrpcChannel.ForAddress(address);
            var nodes = new Wire.Nodes.NodesClient(channel);
            var request = new Wire.JoinRequest
            {
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                Machine = Seated.Orders.Machine,
                Facts = new Wire.NodeFacts { Os = "test-os" },
                ReportsTurns = true,
            };

            request.StartableRoles.AddRange(["orders", "billing"]);

            var stream = nodes.Join(request, cancellationToken: cancellationToken);

            if (!await stream.ResponseStream.MoveNext(cancellationToken)
                || stream.ResponseStream.Current.CommandCase != Wire.NodeCommand.CommandOneofCase.Joined)
            {
                throw new InvalidOperationException("The first command on the join stream was not an acknowledgement.");
            }

            return new JoinedNode(channel, nodes, stream);
        }

        /// <summary>Attaches an agent as a process this node started: the launch is reported, then the token attached with.</summary>
        public async Task<IDisposable> RunsAsync(string agent, CancellationToken cancellationToken)
        {
            var token = Guid.NewGuid().ToString("N");

            _ = await _nodes.ReportLaunchAsync(
                new Wire.LaunchReport { Machine = Seated.Orders.Machine, Agent = agent, Token = token },
                cancellationToken: cancellationToken);

            return await AttachAsync(agent, token, processId: 0, cancellationToken);
        }

        /// <summary>Attaches an agent as a process a node that is gone started: a token this node never reported.</summary>
        public Task<IDisposable> LeftRunningAsync(string agent, long processId, CancellationToken cancellationToken) =>
            AttachAsync(agent, "from-a-node-that-is-gone", processId, cancellationToken);

        /// <summary>The next command the broker sent, in the order it sent them.</summary>
        public async Task<Wire.NodeCommand> NextAsync(TimeSpan within, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(within);

            try
            {
                return await _commands.Reader.ReadAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No command reached the node within {within}.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _ending.CancelAsync();

            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
            }

            _stream.Dispose();
            _ending.Dispose();
            _channel.Dispose();
        }

        private async Task<IDisposable> AttachAsync(string agent, string launchToken, long processId, CancellationToken cancellationToken)
        {
            var stream = new Wire.Agents.AgentsClient(_channel).Attach(
                new Wire.AttachRequest
                {
                    Agent = agent,
                    Adapter = "mcp",
                    Tier = Wire.AgentTier.Pull,
                    ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                    LaunchToken = launchToken,
                    ProcessId = processId,
                },
                cancellationToken: cancellationToken);

            if (!await stream.ResponseStream.MoveNext(cancellationToken)
                || stream.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
            {
                throw new InvalidOperationException("The first event on an attach stream was not the attach result.");
            }

            return stream;
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var command in _stream.ResponseStream.ReadAllAsync(_ending.Token))
                {
                    await _commands.Writer.WriteAsync(command, _ending.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (RpcException)
            {
                // The broker went away, which for a test is a teardown.
            }
            finally
            {
                _ = _commands.Writer.TryComplete();
            }
        }
    }
}
