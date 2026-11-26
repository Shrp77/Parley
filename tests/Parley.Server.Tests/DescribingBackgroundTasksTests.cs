using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>An agent's own words for its background tasks, as the person is shown them (SPEC.md §10.1, §11.6).</summary>
/// <remarks>
/// <para>
/// The words are the agent's, about its own work, for the person only. They take the runner's label's place on the
/// registry row once the node lists the task, in whichever order the two arrive, and go when that work ends or a new
/// process is launched under the name. A node that leaves and comes back with the same process keeps them.
/// </para>
/// <para>
/// <b>The answer carries no turn fact.</b> It says what became of the words, never whether the task runs, so every
/// refusal is about what the agent sent.
/// </para>
/// </remarks>
[Integration]
public sealed class DescribingBackgroundTasksTests
{
    private const string Here = "m1";
    private const string Billing = "billing@m1";
    private const string Orders = "orders@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Wire.RegistryRow> RowAsync(LoopbackBroker broker, string agent = Billing) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == agent);

    private static async Task LaunchAsync(JoinedNodeClient node, string token, string agent = Billing) =>
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = agent, Token = token }, cancellationToken: Ct);

    private static async Task ReportAsync(JoinedNodeClient node, string agent, int tasks, params (string Id, string Label)[] running)
    {
        var background = new Wire.BackgroundWork { Tasks = tasks };
        background.Running.AddRange(running.Select(t => new Wire.RunningTask { Id = t.Id, Kind = Wire.TaskKind.Agent, Label = t.Label }));

        _ = await node.Nodes.ReportTurnAsync(
            new Wire.TurnReport { Machine = Here, Agent = agent, Background = background }, cancellationToken: Ct);
    }

    private static async Task<Wire.DescribeBackgroundTaskReply> DescribeAsync(
        AttachedClient agent, string id, string name, string description = "", string link = "") =>
        await agent.Client.DescribeBackgroundTaskAsync(
            new Wire.DescribeBackgroundTaskRequest { Agent = agent.Name, TaskId = id, Name = name, Description = description, Link = link },
            cancellationToken: Ct);

    private static async Task<JoinedNodeClient> JoinAsync(LoopbackBroker broker)
    {
        // A node that has just left may still hold the machine for a moment.
        for (var tries = 0; ; tries++)
        {
            try
            {
                return await JoinedNodeClient.JoinAsync(broker, Here, ["billing", "orders"], Ct, reportsTurns: true);
            }
            catch (Grpc.Core.RpcException refused) when (refused.StatusCode == Grpc.Core.StatusCode.AlreadyExists && tries < 50)
            {
                await Task.Delay(100, Ct);
            }
        }
    }

    [Fact]
    public async Task The_agents_words_take_the_runners_label_on_the_row_in_either_order()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        // Described before the node has listed it: kept, and shown once it does.
        var early = await DescribeAsync(billing, "a1", "Checking VAT on gift cards", "Reads the VAT rules for stored value.", "exchange #57");
        Assert.True(early.Kept);
        Assert.Empty((await RowAsync(broker)).RunningTasks);

        await ReportAsync(node, Billing, 2, ("a1", "Count to three"), ("b2", "Wait twenty-five seconds"));

        // Described after.
        _ = await DescribeAsync(billing, "b2", "Running the tax tests", link: "https://tickets.example.com/SHOP-12");

        var row = await RowAsync(broker);

        Assert.Equal(["Checking VAT on gift cards", "Running the tax tests"], row.RunningTasks.Select(t => t.Name));
        Assert.Equal("Reads the VAT rules for stored value.", row.RunningTasks[0].Description);
        Assert.Equal("exchange #57", row.RunningTasks[0].Link);
        Assert.Equal("https://tickets.example.com/SHOP-12", row.RunningTasks[1].Link);
    }

    [Fact]
    public async Task One_agents_words_never_reach_another_agents_row()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await LaunchAsync(node, "t-2", Orders);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct, launchToken: "t-2");

        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));
        _ = await DescribeAsync(orders, "a1", "Orders says this");

        Assert.Equal("Count to three", Assert.Single((await RowAsync(broker)).RunningTasks).Name);
    }

    [Fact]
    public async Task A_node_that_comes_back_with_the_same_process_keeps_the_words_and_a_new_process_clears_them()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));
        _ = await DescribeAsync(billing, "a1", "Checking VAT");

        // The connection drops; the node comes back and announces the same process and its work again.
        await node.DisposeAsync();
        await using (var back = await JoinAsync(broker))
        {
            await LaunchAsync(back, "t-1");
            await ReportAsync(back, Billing, 1, ("a1", "Count to three"));

            Assert.Equal("Checking VAT", Assert.Single((await RowAsync(broker)).RunningTasks).Name);

            // A new process: what was said was about the last one's work.
            await LaunchAsync(back, "t-2");
            await using var again = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-2");
            await ReportAsync(back, Billing, 1, ("a1", "Count to three"));

            Assert.Equal("Count to three", Assert.Single((await RowAsync(broker)).RunningTasks).Name);
        }
    }

    [Fact]
    public async Task Work_ending_takes_the_words_for_what_ended_and_leaves_words_for_work_begun_since()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));
        _ = await DescribeAsync(billing, "a1", "Checking VAT");

        // A task begun as the last ended, described before the node's report of none lands.
        _ = await DescribeAsync(billing, "b2", "Running the tax tests");
        await ReportAsync(node, Billing, 0);
        await ReportAsync(node, Billing, 2, ("a1", "Count to three"), ("b2", "Wait"));

        var row = await RowAsync(broker);

        Assert.Equal(["Count to three", "Running the tax tests"], row.RunningTasks.Select(t => t.Name));
    }

    [Fact]
    public async Task A_task_that_leaves_the_list_takes_its_words_while_other_work_runs_on()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, Billing, 2, ("a1", "Count to three"), ("b2", "Serve the site"));
        _ = await DescribeAsync(billing, "a1", "Checking VAT");
        _ = await DescribeAsync(billing, "b2", "Serving the site for review");

        // a1 ends while b2 runs on: its words go with it, though the count never reaches none.
        await ReportAsync(node, Billing, 1, ("b2", "Serve the site"));
        await ReportAsync(node, Billing, 2, ("a1", "Count to three"), ("b2", "Serve the site"));

        Assert.Equal(["Count to three", "Serving the site for review"], (await RowAsync(broker)).RunningTasks.Select(t => t.Name));
    }

    [Fact]
    public async Task While_the_node_is_away_nothing_is_shown()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));
        _ = await DescribeAsync(billing, "a1", "Checking VAT");
        await node.DisposeAsync();

        // The node's leaving is handled as its stream ends, which the broker hears a moment later.
        var row = await RowAsync(broker);

        for (var tries = 0; tries < 50 && (row.BackgroundTasks > 0 || row.RunningTasks.Count > 0); tries++)
        {
            await Task.Delay(100, Ct);
            row = await RowAsync(broker);
        }

        Assert.Equal(0, row.BackgroundTasks);
        Assert.Empty(row.RunningTasks);
    }

    [Fact]
    public async Task The_oldest_words_go_past_the_bound()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        for (var i = 0; i <= Parley.Core.BackgroundTaskText.MostTasks; i++)
        {
            _ = await DescribeAsync(billing, $"t{i}", $"Task {i}");
        }

        await ReportAsync(node, Billing, 2, ("t0", "Runner zero"), ("t1", "Runner one"));

        Assert.Equal(["Runner zero", "Task 1"], (await RowAsync(broker)).RunningTasks.Select(t => t.Name));
    }

    [Theory]
    [InlineData("has space", "A name")]
    [InlineData("", "A name")]
    [InlineData("a1", "")]
    [InlineData("a1", "   ")]
    public async Task What_the_agent_sent_is_refused_in_words_that_say_nothing_about_the_task(string id, string name)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        var reply = await DescribeAsync(billing, id, name);

        Assert.False(reply.Kept);
        Assert.NotEmpty(reply.Refused);

        foreach (var word in new[] { "running", "not running", "unknown", "no such", "ended", "finished", "background work ", "turn" })
        {
            Assert.DoesNotContain(word, reply.Refused, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_link_Parley_does_not_show_is_dropped_and_the_rest_kept()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        var reply = await DescribeAsync(billing, "a1", "Checking VAT", link: "javascript:alert(1)");

        Assert.True(reply.Kept);
        Assert.Contains("named host", reply.LinkDropped, StringComparison.Ordinal);

        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));

        var task = Assert.Single((await RowAsync(broker)).RunningTasks);
        Assert.Equal("Checking VAT", task.Name);
        Assert.Equal(string.Empty, task.Link);
    }

    [Fact]
    public async Task Words_past_their_bound_are_kept_cut_and_the_answer_says_so()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        var reply = await DescribeAsync(billing, "a1", new string('n', 100), new string('d', 400));

        Assert.True(reply.Kept);
        Assert.True(reply.NameCut);
        Assert.True(reply.DescriptionCut);
    }

    [Fact]
    public void The_answer_has_these_fields_and_no_other()
    {
        // A field added to say "kept, but your node has not listed it yet" would tell an agent whether its task runs,
        // which is turn information (SPEC.md §10.1); its name need hold no word the turn guards forbid.
        var fields = Wire.DescribeBackgroundTaskReply.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name);

        Assert.Equal(["kept", "refused", "name_cut", "description_cut", "link_dropped"], fields);
    }

    [Fact]
    public async Task The_answer_is_the_same_whether_the_task_is_unlisted_listed_or_ended()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        var unlisted = await DescribeAsync(billing, "a1", "Checking VAT", link: "ftp://x");
        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));
        var listed = await DescribeAsync(billing, "a1", "Checking VAT", link: "ftp://x");
        await ReportAsync(node, Billing, 0);
        var ended = await DescribeAsync(billing, "a1", "Checking VAT", link: "ftp://x");

        Assert.Equal(unlisted, listed);
        Assert.Equal(unlisted, ended);
    }

    [Fact]
    public async Task The_person_seat_describes_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var seat = Parley.Core.PersonSeat.On(Parley.Core.ProjectName.Create("test")).Value;
        await using var person = await AttachedClient.AttachAsync(broker, seat, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);

        var reply = await DescribeAsync(person, "a1", "Checking VAT");

        Assert.False(reply.Kept);
        AssertSaysNothingAboutTheTask(reply.Refused);
    }

    [Fact]
    public async Task A_caller_that_is_not_attached_describes_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        // Orders holds no session; the call names it as every Agents call names its caller.
        var reply = await billing.Client.DescribeBackgroundTaskAsync(
            new Wire.DescribeBackgroundTaskRequest { Agent = Orders, TaskId = "a1", Name = "Checking VAT" }, cancellationToken: Ct);

        Assert.False(reply.Kept);
        AssertSaysNothingAboutTheTask(reply.Refused);
    }

    private static void AssertSaysNothingAboutTheTask(string refused)
    {
        Assert.NotEmpty(refused);

        foreach (var word in new[] { "running", "not running", "unknown", "no such", "ended", "finished", "turn" })
        {
            Assert.DoesNotContain(word, refused, StringComparison.OrdinalIgnoreCase);
        }
    }
}
