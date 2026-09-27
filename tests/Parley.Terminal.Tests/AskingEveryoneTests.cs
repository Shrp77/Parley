using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Asking every agent in a project what it owns, in one act.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cost is the subject of these tests, because it is the thing a person cannot take back.</b> One
/// turn per agent is spent the moment the sends go out, so the count and the words naming it have to be
/// on the screen <em>before</em> the first Request and an answer of no has to send nothing at all.
/// </para>
/// <para>
/// <b>Each agent gets its own conversation, and that is asserted by reading what the broker recorded.</b>
/// A shared conversation would put nineteen unrelated arguments in one transcript and leave no surface
/// able to find the one it wanted — and it is the shape the existing bulk send has, so it is the mistake
/// nearest to hand.
/// </para>
/// </remarks>
public sealed class AskingEveryoneTests
{
    private static readonly DateTimeOffset When = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentRecord Agent(string name, string adapter = "mcp") =>
        new(AgentName.Create(name), adapter, AgentTier.Pull, "/src/thing", null, null, false, AgentStatus.Active);

    [Fact]
    public void The_cost_is_named_with_the_count_before_anything_is_sent()
    {
        // FR-007. Must fail against a description that names no number, or that lists nineteen agents and
        // pushes the cost off the screen -- 0.25.1 records a report doing exactly that at the half saying
        // what to do about it.
        var many = Enumerable.Range(0, 19)
            .Select(i => AgentName.Create($"role{i}@test"))
            .ToList();

        var said = Ownership.Costing(many);

        Assert.Contains("19", said, StringComparison.Ordinal);
        Assert.Contains("costs that agent a turn", said, StringComparison.Ordinal);
        Assert.Contains("Nothing is written by asking", said, StringComparison.Ordinal);

        // Named up to a handful and counted after, so the set is recognisable without the list running away.
        Assert.Contains("role0@test", said, StringComparison.Ordinal);
        Assert.Contains("15 more", said, StringComparison.Ordinal);
        Assert.DoesNotContain("role18@test", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_handful_is_named_in_full_rather_than_counted()
    {
        // The abbreviation must not fire when there is nothing to abbreviate: "and 0 more" is the kind of
        // sentence that reads as a bug.
        var said = Ownership.Costing([AgentName.Create("orders@test"), AgentName.Create("billing@test")]);

        Assert.Contains("orders@test", said, StringComparison.Ordinal);
        Assert.Contains("billing@test", said, StringComparison.Ordinal);
        Assert.DoesNotContain("more", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answering_no_sends_nothing()
    {
        // **The whole point of stating a cost is that it can be declined**, so this is the test that makes
        // the dialog more than decoration. Must fail against a version that sends and then reports, which
        // is the shape of every "are you sure" nobody can actually answer.
        using var connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(connection);

        await agents.UpsertAsync(Agent("orders@test"), Ct);

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var said = string.Empty;
        var queued = 0;

        await Ownership.AskEveryAsync(
            driving.App,
            new SeatVoice(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat),
            shell.Held,
            [AgentName.Create("orders@test")],
            When,
            _ => queued++,
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        var dialog = (ChoiceDialog)driving.Find(Ownership.AskEveryTitle);

        // Pressed rather than called through a seam. 0.30.4 records the worst defect of 017 being that
        // no test had ever pressed Enter, in a file whose own summary said it was there to catch that.
        driving.Row(dialog.Choices, Ownership.Leave);
        driving.Enter();

        Assert.Equal(0, queued);
        Assert.Equal(Ownership.Left, said);

        // And the broker was never asked, which is the assertion that would survive the report being
        // reworded: nothing is open against anybody.
        var waiting = await broker.Operators.DescribeWaitingAsync(
            new Parley.Transport.Empty(), cancellationToken: Ct);

        Assert.Empty(waiting.Edges);
    }

    [Fact]
    public async Task Every_ask_is_sent_and_each_one_opens_its_own_conversation()
    {
        // **Asserted by what the broker recorded rather than by timing.** Three agents, three exchanges,
        // three different conversations -- and each conversation named for the agent it is about, which is
        // what lets a surface find one discussion later without a marker in anybody's payload.
        //
        // Must fail against reusing one conversation for all of them, which is what the existing bulk send
        // does and is therefore the nearest wrong answer.
        using var connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(connection);

        foreach (var name in new[] { "orders@test", "billing@test", "inventory@test" })
        {
            await agents.UpsertAsync(Agent(name), Ct);
        }

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var asking = new[] { "orders@test", "billing@test", "inventory@test" }
            .Select(AgentName.Create)
            .ToList();

        var work = new List<Task>();
        var queued = 0;

        await Ownership.AskEveryAsync(
            driving.App,
            new SeatVoice(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat),
            shell.Held,
            asking,
            When,
            run =>
            {
                queued++;
                work.Add(run(Ct));
            },
            _ => Task.CompletedTask);

        var dialog = (ChoiceDialog)driving.Find(Ownership.AskEveryTitle);

        driving.Row(dialog.Choices, Ownership.AskThem);
        driving.Enter();

        await Task.WhenAll(work);

        // **One queued act for all three**, which is the ordering rule read the way it applies here: an
        // ask has no wait to defer, so what would be wrong is asking one agent per queued item and
        // letting anything at all happen between them.
        Assert.Equal(1, queued);

        var waiting = await broker.Operators.DescribeWaitingAsync(
            new Parley.Transport.Empty(), cancellationToken: Ct);

        Assert.Equal(3, waiting.Edges.Count);

        foreach (var agent in asking)
        {
            var edge = Assert.Single(waiting.Edges, e => e.WaitedOn == agent.Value);
            var export = await broker.Operators.ExportAsync(
                new Parley.Transport.ExportRequest
                {
                    ConversationId = OwnershipAsk.Conversation(agent, When),
                },
                cancellationToken: Ct);

            // The conversation named for this agent holds this agent's exchange, and nobody else's.
            Assert.Single(export.Exchanges);
            Assert.Equal(edge.ExchangeId, export.Exchanges[0].ExchangeId);
        }
    }

    [Fact]
    public async Task Asking_with_no_seat_says_so_rather_than_sending_as_the_operator()
    {
        // The same rule the single ask keeps: §13 rests on a receiver being able to tell who spoke, and an
        // operator instruction in place of a message would put the question in front of every agent with
        // nothing in any transcript and nothing owed back.
        using var driving = Driving.Open();
        using var directory = Dead();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        var said = string.Empty;
        var queued = 0;

        await Ownership.AskEveryAsync(
            driving.App,
            null,
            null,
            [AgentName.Create("orders@test")],
            When,
            _ => queued++,
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        Assert.Equal(Ownership.NoSeat, said);
        Assert.Equal(0, queued);
    }

    [Fact]
    public async Task A_project_with_nobody_registered_says_so_rather_than_refusing()
    {
        // An empty registry is an ordinary first afternoon, not a fault. Must fail against reporting a
        // refusal, which would send somebody looking for a broken broker -- and against reporting the
        // no-seat sentence, which would be true of a different problem.
        using var connection = ParleyDatabase.OpenInMemory();

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var said = string.Empty;
        var queued = 0;

        await Ownership.AskEveryAsync(
            driving.App,
            new SeatVoice(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat),
            shell.Held,
            [],
            When,
            _ => queued++,
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        Assert.Equal(Ownership.NobodyToAsk, said);
        Assert.NotEqual(Ownership.NoSeat, said);
        Assert.Equal(0, queued);
    }

    private static BrokerDirectory Dead() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "unused.db", "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static BrokerDirectory Wired(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }
}
