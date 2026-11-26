using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>Each background task a node names reaches the person's registry row by name (feature 043, SPEC.md §11.6).</summary>
/// <remarks>
/// <para>
/// The names live exactly as long as the count beside them: held in memory with the node that said them, cleared when it
/// says none, when it leaves, and when a new process is launched under the name.
/// </para>
/// <para>
/// <b>The broker holds every entry to the rules again</b>, not taking the node's word for it: an id the runner could not
/// have given drops its entry, a label is cut and neutralised, an id named twice is named once, and the list is never
/// longer than the count.
/// </para>
/// </remarks>
[Integration]
public sealed class BackgroundTaskNamesTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Wire.RegistryRow> RowAsync(LoopbackBroker broker) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == Agent);

    private static async Task LaunchAsync(JoinedNodeClient node, string token) =>
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = token }, cancellationToken: Ct);

    private static Wire.RunningTask Task(string id, string label, Wire.TaskKind kind = Wire.TaskKind.Agent) =>
        new() { Id = id, Kind = kind, Label = label };

    private static async Task ReportAsync(JoinedNodeClient node, int tasks, params Wire.RunningTask[] running)
    {
        var background = new Wire.BackgroundWork { Tasks = tasks };
        background.Running.AddRange(running);

        _ = await node.Nodes.ReportTurnAsync(
            new Wire.TurnReport { Machine = Here, Agent = Agent, Background = background }, cancellationToken: Ct);
    }

    private static async Task<(LoopbackBroker Broker, JoinedNodeClient Node, AttachedClient Agent)> StartAsync(string token = "t-1")
    {
        var broker = await LoopbackBroker.StartAsync(Ct);
        var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await LaunchAsync(node, token);
        var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: token);

        return (broker, node, agent);
    }

    [Fact]
    public async Task Each_task_the_node_names_is_on_the_row_in_its_order_with_its_kind()
    {
        var (broker, node, agent) = await StartAsync();
        await using var _ = broker;
        await using var __ = node;
        await using var ___ = agent;

        await ReportAsync(node, 2, Task("af7da04eab7667f03", "Count to three"), Task("b8n705a3e", "Wait twenty-five seconds", Wire.TaskKind.Command));

        var row = await RowAsync(broker);

        Assert.Equal(2, row.BackgroundTasks);
        Assert.Equal(["af7da04eab7667f03", "b8n705a3e"], row.RunningTasks.Select(t => t.Id));
        Assert.Equal(["Count to three", "Wait twenty-five seconds"], row.RunningTasks.Select(t => t.Name));
        Assert.Equal([Wire.TaskKind.Agent, Wire.TaskKind.Command], row.RunningTasks.Select(t => t.Kind));
        Assert.All(row.RunningTasks, t => Assert.Equal(string.Empty, t.Description));
        Assert.All(row.RunningTasks, t => Assert.Equal(string.Empty, t.Link));
    }

    [Fact]
    public async Task The_broker_holds_each_entry_to_the_rules_and_not_the_nodes_word()
    {
        var (broker, node, agent) = await StartAsync();
        await using var _ = broker;
        await using var __ = node;
        await using var ___ = agent;

        await ReportAsync(
            node,
            4,
            Task("has space", "Bad id"),
            Task("ok1", "First\nsecond " + new string('x', 500)),
            Task("ok1", "Named twice"),
            Task("ok2", string.Empty));

        var row = await RowAsync(broker);

        Assert.Equal(4, row.BackgroundTasks);
        Assert.Equal(["ok1", "ok2"], row.RunningTasks.Select(t => t.Id));

        var first = row.RunningTasks[0].Name;
        Assert.DoesNotContain('\n', first);
        Assert.StartsWith("First second", first, StringComparison.Ordinal);
        Assert.True(new System.Globalization.StringInfo(first).LengthInTextElements <= Parley.Core.BackgroundTaskText.LongestName);

        // Unnamed by the runner: travels with no name, and a surface draws a stand-in.
        Assert.Equal(string.Empty, row.RunningTasks[1].Name);
    }

    [Fact]
    public async Task The_list_is_never_longer_than_the_count()
    {
        var (broker, node, agent) = await StartAsync();
        await using var _ = broker;
        await using var __ = node;
        await using var ___ = agent;

        await ReportAsync(node, 1, Task("a", "One"), Task("b", "Two"));

        var row = await RowAsync(broker);

        Assert.Equal(1, row.BackgroundTasks);
        Assert.Equal(["a"], row.RunningTasks.Select(t => t.Id));
    }

    [Fact]
    public async Task Names_clear_with_the_count_when_the_node_says_none()
    {
        var (broker, node, agent) = await StartAsync();
        await using var _ = broker;
        await using var __ = node;
        await using var ___ = agent;

        await ReportAsync(node, 1, Task("a", "Checking VAT"));
        Assert.Single((await RowAsync(broker)).RunningTasks);

        await ReportAsync(node, 0);

        var row = await RowAsync(broker);
        Assert.Equal(0, row.BackgroundTasks);
        Assert.Empty(row.RunningTasks);
    }

    [Fact]
    public async Task A_new_process_under_the_name_starts_with_no_names()
    {
        var (broker, node, agent) = await StartAsync();
        await using var _ = broker;
        await using var __ = node;
        await using var ___ = agent;

        await ReportAsync(node, 1, Task("a", "Checking VAT"));

        await LaunchAsync(node, "t-2");
        await using var again = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-2");

        Assert.Empty((await RowAsync(broker)).RunningTasks);
    }

    [Fact]
    public async Task One_open_outside_Parley_shows_no_names_whatever_was_said()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);

        await ReportAsync(node, 1, Task("a", "Checking VAT"));

        Assert.Empty((await RowAsync(broker)).RunningTasks);
    }
}
