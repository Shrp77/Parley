using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Having Parley ask every agent in a project again what it owns, in one act.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cost is the subject of these tests, because it is the thing a person cannot take back.</b> One turn per agent
/// is spent the moment the asks go out, so the count and the words naming it have to be on the screen <em>before</em>
/// the first one and an answer of no has to ask nothing at all.
/// </para>
/// <para>
/// <b>No seat anywhere in this file, and that is 022.</b> 020 sent these as the person's own Requests and refused
/// without a seat, which is the refusal the owner met; Parley asks now. Each question is counted from what the broker
/// recorded, and each is in a conversation of its own.
/// </para>
/// </remarks>
public sealed class AskingEveryoneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentRecord Agent(string name, string adapter = "mcp") =>
        new(AgentName.Create(name), adapter, AgentTier.Pull, "/src/thing", null, null, false, AgentStatus.Active);

    [Fact]
    public void The_cost_is_named_with_the_count_before_anything_is_sent()
    {
        // FR-007. Must fail against a description that names no number, or that lists nineteen agents and pushes the
        // cost off the screen -- 0.25.1 records a report doing exactly that at the half saying what to do about it.
        var many = Enumerable.Range(0, 19)
            .Select(i => AgentName.Create($"role{i}@test"))
            .ToList();

        var said = Ownership.Costing(many);

        Assert.Contains("19", said, StringComparison.Ordinal);
        Assert.Contains("costs that agent a turn", said, StringComparison.Ordinal);
        Assert.Contains("Parley asks each of them", said, StringComparison.Ordinal);

        // Named up to a handful and counted after, so the set is recognisable without the list running away.
        Assert.Contains("role0@test", said, StringComparison.Ordinal);
        Assert.Contains("15 more", said, StringComparison.Ordinal);
        Assert.DoesNotContain("role18@test", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_handful_is_named_in_full_rather_than_counted()
    {
        // "and 0 more" is the kind of sentence that reads as a bug.
        var said = Ownership.Costing([AgentName.Create("orders@test"), AgentName.Create("billing@test")]);

        Assert.Contains("orders@test", said, StringComparison.Ordinal);
        Assert.Contains("billing@test", said, StringComparison.Ordinal);
        Assert.DoesNotContain(" more", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answering_no_asks_nothing()
    {
        // **The whole point of stating a cost is that it can be declined.** Must fail against a version that asks and
        // then reports.
        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(Agent("orders@test"), Ct);

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);

        var said = string.Empty;
        var queued = 0;

        await Ownership.AskEveryAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(directory.Projects[0])),
            [AgentName.Create("orders@test")],
            _ => queued++,
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        var dialog = (ChoiceDialog)driving.Find(Ownership.AskEveryTitle);

        // Pressed rather than called through a seam: 0.30.4's worst defect was a key no test had pressed.
        driving.Row(dialog.Choices, Ownership.Leave);
        driving.Enter();

        Assert.Equal(0, queued);
        Assert.Equal(Ownership.Left, said);

        var report = await broker.Operators.FacilitationAsync(new Parley.Transport.Empty(), cancellationToken: Ct);

        Assert.Empty(report.Questions);
    }

    [Fact]
    public async Task Every_agent_is_asked_by_Parley_with_no_seat_in_a_conversation_of_its_own()
    {
        // Must fail against 020's seat requirement -- there is no seat here -- and against one conversation for all.
        using var connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(connection);

        foreach (var name in new[] { "orders@test", "billing@test", "inventory@test" })
        {
            await agents.UpsertAsync(Agent(name), Ct);
        }

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);

        var asking = new[] { "orders@test", "billing@test", "inventory@test" }.Select(AgentName.Create).ToList();
        var work = new List<Task>();
        var said = string.Empty;

        await Ownership.AskEveryAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(directory.Projects[0])),
            asking,
            run => work.Add(run(Ct)),
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        var dialog = (ChoiceDialog)driving.Find(Ownership.AskEveryTitle);

        driving.Row(dialog.Choices, Ownership.AskThem);
        driving.Enter();

        await Task.WhenAll(work);

        var report = await broker.Operators.FacilitationAsync(new Parley.Transport.Empty(), cancellationToken: Ct);

        Assert.Equal(3, report.Questions.Count);
        Assert.Equal(3, report.Questions.Select(q => q.ConversationId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(report.Questions, q => Assert.Equal(Parley.Transport.QuestionState.Asked, q.State));
        Assert.Contains("Parley asked 3", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_with_nobody_registered_says_so_rather_than_refusing()
    {
        // An empty registry is an ordinary first afternoon, not a fault.
        using var connection = ParleyDatabase.OpenInMemory();

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);

        var said = string.Empty;
        var queued = 0;

        await Ownership.AskEveryAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(directory.Projects[0])),
            [],
            _ => queued++,
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        Assert.Equal(Ownership.NobodyToAsk, said);
        Assert.Equal(0, queued);
    }

    private static BrokerDirectory Wired(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));
}
