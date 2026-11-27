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

    private static async Task LaunchAsync(JoinedNodeClient node, string token, string agent = Billing, long sequence = 0) =>
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = agent, Token = token, Sequence = sequence }, cancellationToken: Ct);

    private static Task ReportAsync(JoinedNodeClient node, string agent, int tasks, params (string Id, string Label)[] running) =>
        ReportReadAtAsync(node, agent, 0, tasks, running);

    private static async Task ReportReadAtAsync(
        JoinedNodeClient node, string agent, long sequence, int tasks, params (string Id, string Label)[] running)
    {
        var background = new Wire.BackgroundWork { Tasks = tasks, Sequence = sequence };
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
    public async Task A_reading_the_node_sent_late_changes_nothing_and_takes_no_words()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        // A rejoin's reading of one task (read at 1) lands after the runner's newer notice of two (read at 2).
        await ReportReadAtAsync(node, Billing, 2, 2, ("a1", "Count to three"), ("b2", "Serve the site"));
        _ = await DescribeAsync(billing, "b2", "Serving the site for review");
        await ReportReadAtAsync(node, Billing, 1, 1, ("a1", "Count to three"));

        var row = await RowAsync(broker);

        Assert.Equal(2, row.BackgroundTasks);
        Assert.Equal(["Count to three", "Serving the site for review"], row.RunningTasks.Select(t => t.Name));

        // And a newer reading still lands.
        await ReportReadAtAsync(node, Billing, 3, 1, ("b2", "Serve the site"));
        Assert.Equal(["Serving the site for review"], (await RowAsync(broker)).RunningTasks.Select(t => t.Name));
    }

    [Fact]
    public async Task A_reading_sent_before_the_work_ended_does_not_bring_it_back()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportReadAtAsync(node, Billing, 2, 0);
        await ReportReadAtAsync(node, Billing, 1, 1, ("a1", "Count to three"));
        Assert.Equal(0, (await RowAsync(broker)).BackgroundTasks);

        // Nor does one the last process's node sent before a new process was launched under the name.
        await ReportReadAtAsync(node, Billing, 5, 1, ("a1", "Count to three"));
        await LaunchAsync(node, "t-2");
        await using var again = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-2");
        await ReportReadAtAsync(node, Billing, 4, 1, ("a1", "Count to three"));

        Assert.Equal(0, (await RowAsync(broker)).BackgroundTasks);
    }

    [Fact]
    public async Task The_last_processs_reading_that_lands_after_a_launch_is_older_than_the_launch()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1", sequence: 1);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        // The old process read one task at 5 and again at 6; the node launched the new one at 7, and the reading at 6,
        // retried, lands after the launch. Newer than anything the broker had heard, but older than the launch.
        await ReportReadAtAsync(node, Billing, 5, 1, ("a1", "Count to three"));
        await LaunchAsync(node, "t-2", sequence: 7);
        await using var again = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-2");
        await ReportReadAtAsync(node, Billing, 6, 1, ("a1", "Count to three"));

        Assert.Equal(0, (await RowAsync(broker)).BackgroundTasks);

        // The new process's own reading, made after the launch, lands.
        await ReportReadAtAsync(node, Billing, 8, 1, ("b2", "Serve the site"));
        Assert.Equal(1, (await RowAsync(broker)).BackgroundTasks);
    }

    [Fact]
    public async Task A_launch_handled_after_the_new_processs_first_reading_leaves_that_reading()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1", sequence: 1);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        // The node launched t-2 at 7, and its first reading, at 8, reached the broker before the launch report did.
        await ReportReadAtAsync(node, Billing, 8, 2, ("b2", "Serve the site"), ("c3", "Count"));
        await LaunchAsync(node, "t-2", sequence: 7);
        await using var again = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-2");

        Assert.Equal(2, (await RowAsync(broker)).BackgroundTasks);
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

    [Fact]
    public async Task Describing_a_task_again_replaces_what_was_said()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));
        _ = await DescribeAsync(billing, "a1", "Checking VAT", "First words.", "exchange #5");
        _ = await DescribeAsync(billing, "a1", "Checking VAT on gift cards", "Second words.", "exchange #6");

        var task = Assert.Single((await RowAsync(broker)).RunningTasks);

        Assert.Equal("Checking VAT on gift cards", task.Name);
        Assert.Equal("Second words.", task.Description);
        Assert.Equal("exchange #6", task.Link);
    }

    [Fact]
    public async Task Words_said_before_any_launch_was_known_take_the_launch_that_is_reported_next()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);

        // The adapter attaches before its node announces the process (a broker that restarted before the node rejoined),
        // and the agent describes its task meanwhile: no launch is known to say the words under.
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");
        Assert.True((await DescribeAsync(billing, "a1", "Checking VAT")).Kept);

        await LaunchAsync(node, "t-1");
        await ReportAsync(node, Billing, 1, ("a1", "Count to three"));

        Assert.Equal("Checking VAT", Assert.Single((await RowAsync(broker)).RunningTasks).Name);
    }

    [Fact]
    public async Task The_row_says_which_tasks_the_agent_named_itself()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        await ReportAsync(node, Billing, 2, ("a1", "Count to three"), ("b2", "Wait twenty-five seconds"));
        _ = await DescribeAsync(billing, "a1", "Checking VAT");

        var row = await RowAsync(broker);

        Assert.Equal([true, false], row.RunningTasks.Select(t => t.Said));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_agent_that_gave_no_link_is_not_told_its_link_was_dropped(string link)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        var reply = await DescribeAsync(billing, "a1", "Checking VAT", link: link);

        Assert.True(reply.Kept);
        Assert.Equal(string.Empty, reply.LinkDropped);
    }

    [Fact]
    public async Task One_agents_words_on_the_registry_stop_at_its_share_of_the_reply_and_links_count_toward_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinAsync(broker);
        await LaunchAsync(node, "t-1");
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, launchToken: "t-1");

        var most = Parley.Core.BackgroundTaskText.MostTasks;
        var ids = Enumerable.Range(0, most).Select(i => $"t{i}").ToArray();
        await ReportAsync(node, Billing, most, [.. ids.Select(id => (id, "Runner"))]);

        // Every word at its bound, three bytes a character, and a link at its bound: about 1,600 bytes a task, of which a
        // third is the link. Two hundred of them are far past one row's share.
        var name = new string((char)0x4E00, Parley.Core.BackgroundTaskText.LongestName);
        var description = new string((char)0x4E00, Parley.Core.BackgroundTaskText.LongestDescription);
        var link = "https://tickets.example.com/";
        link += new string('a', Parley.Core.BackgroundTaskText.LongestLink - link.Length);

        foreach (var id in ids)
        {
            Assert.True((await DescribeAsync(billing, id, name, description, link)).Kept);
        }

        var row = await RowAsync(broker);
        var share = OperatorsService.MostTaskWordBytes / TaskWordBudget.Shares;

        // Every task still travels, by id, so the count and the list agree; those past the share go as stand-ins.
        Assert.Equal(ids, row.RunningTasks.Select(t => t.Id));
        Assert.Contains(row.RunningTasks, t => t.Name.Length == 0);
        Assert.Contains(row.RunningTasks, t => t.Link == link);
        Assert.True(WordBytes(row) <= share, $"{WordBytes(row)} bytes of words on one row, past its share of {share}");
    }

    [Fact]
    public async Task The_whole_registry_reply_stops_at_its_budget_however_many_agents_say_how_much()
    {
        // One more agent than there are shares, each saying as much as a row may carry and more.
        var roles = Enumerable.Range(0, TaskWordBudget.Shares + 1).Select(i => $"agent{i}").ToArray();
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, roles, Ct, reportsTurns: true);

        var name = new string((char)0x4E00, Parley.Core.BackgroundTaskText.LongestName);
        var description = new string((char)0x4E00, Parley.Core.BackgroundTaskText.LongestDescription);
        var link = "https://tickets.example.com/";
        link += new string('a', Parley.Core.BackgroundTaskText.LongestLink - link.Length);
        var most = Parley.Core.BackgroundTaskText.MostTasks;
        var ids = Enumerable.Range(0, most).Select(i => $"t{i}").ToArray();
        var agents = new List<AttachedClient>();

        try
        {
            foreach (var role in roles)
            {
                var agent = $"{role}@{Here}";
                await LaunchAsync(node, $"t-{role}", agent);
                var client = await AttachedClient.AttachAsync(broker, agent, cancellationToken: Ct, launchToken: $"t-{role}");
                agents.Add(client);
                await ReportAsync(node, agent, most, [.. ids.Select(id => (id, "Runner"))]);

                foreach (var id in ids)
                {
                    _ = await DescribeAsync(client, id, name, description, link);
                }
            }

            var rows = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
                .Where(row => roles.Any(role => row.Name == $"{role}@{Here}"))
                .ToList();

            Assert.Equal(roles.Length, rows.Count);
            Assert.All(rows, row => Assert.Equal(ids, row.RunningTasks.Select(t => t.Id)));
            Assert.True(rows.Sum(WordBytes) <= OperatorsService.MostTaskWordBytes);

            // The budget is spent: the row drawn last finds nothing left, and every one of its tasks is a stand-in.
            Assert.Contains(rows, row => row.RunningTasks.All(t => t.Name.Length == 0 && t.Link.Length == 0));
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    private static long WordBytes(Wire.RegistryRow row) => row.RunningTasks.Sum(t =>
        (long)System.Text.Encoding.UTF8.GetByteCount(t.Name)
        + System.Text.Encoding.UTF8.GetByteCount(t.Description)
        + System.Text.Encoding.UTF8.GetByteCount(t.Link));

    private static void AssertSaysNothingAboutTheTask(string refused)
    {
        Assert.NotEmpty(refused);

        foreach (var word in new[] { "running", "not running", "unknown", "no such", "ended", "finished", "turn" })
        {
            Assert.DoesNotContain(word, refused, StringComparison.OrdinalIgnoreCase);
        }
    }
}
