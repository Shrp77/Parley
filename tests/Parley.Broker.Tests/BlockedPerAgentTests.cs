using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// An item's blocked facts, counted per agent on the person's request.
/// </summary>
/// <remarks>
/// <b>Found by review, round 21.</b> Counted per agent, an agent's own question to another that ended blocked, worked
/// around, read the item blocked; and the blocker named was the newest anywhere, one since cleared. Only the person's
/// requests count now, and the blocker is one a counted exchange gave.
/// </remarks>
[Integration]
public sealed class BlockedPerAgentTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly AgentName _person;

    public BlockedPerAgentTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-06T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active'),
                ('inventory@test', 'claude-code', 'full', 'active');
            """);

        var agents = new AgentRepository(_connection);
        var project = ProjectName.Create("test");

        _ = agents.EnsureFacilitatorAsync(project).GetAwaiter().GetResult();
        _person = agents.EnsurePersonAsync(project).GetAwaiter().GetResult();

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.ServeFrom(T0);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> RoadmapAsync()
    {
        var proposed = await _broker.ProposeRoadmapAsync(
            Orders,
            new RoadmapContent("Billing export",
            [
                new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
            ]),
            null, null, T0, Ct);
        var content = (await _console.ListProposalsAsync(ProposalState.Waiting, Ct)).Single().Content;
        return (await _console.ConfirmProposalAsync(proposed.Id!.Value, content, null, T0, Ct)).RoadmapId!.Value;
    }

    private async Task<long> AskAsync(AgentName from, AgentName to, int minute, string text = "Add CSV export")
    {
        var sent = await _broker.RequestAsync(from, to, Requests.Model, Requests.Request(text), T0.AddMinutes(minute), "rm1-1", Ct);
        Assert.True(sent.Succeeded, sent.Rejection.ToString());
        return sent.Exchange!.Id;
    }

    private async Task AnswerAsync(long exchange, AgentName by, int minute, string outcome = "done")
    {
        var done = await _broker.ResultAsync(
            exchange, by, Requests.Model, Requests.Result((PayloadShape.WholeRequest, outcome)), T0.AddMinutes(minute), Ct);
        Assert.True(done.Succeeded, done.ToString());
    }

    private async Task<ItemFacts> FactsAsync(long roadmapId) =>
        (await _console.GetRoadmapAsync(roadmapId, Ct))!.Items.Single(i => i.Number == 1).Facts!;

    [Fact]
    public async Task An_agent_answering_the_persons_later_line_first_does_not_hide_its_blocker_on_the_earlier()
    {
        // Found by review, round 22: latest was the highest id, so the agent chose the order. Answering "any update?"
        // first and then ending the work blocked read done. Latest is the one that closed last.
        var roadmap = await RoadmapAsync();

        var work = await AskAsync(_person, Orders, 0);
        var update = await AskAsync(_person, Orders, 1, "Any update?");

        await AnswerAsync(update, Orders, 2);
        await AnswerAsync(work, Orders, 3, "blocked:permission");

        var facts = await FactsAsync(roadmap);
        Assert.Equal(1, facts.BlockedExchanges);
        Assert.Equal("permission", facts.Blocker);
    }

    [Theory]
    [InlineData("answered")]
    [InlineData("acknowledged")]
    [InlineData("done")]
    public async Task Any_answer_after_a_block_clears_the_agents_part(string outcome)
    {
        // The owner's decision after review round 29: done, answered and acknowledged all roll up to complete, and the
        // roadmap reads them alike. An item that is a question, blocked until the person supplied what was missing and
        // then answered, read blocked for good when only done cleared a block.
        var roadmap = await RoadmapAsync();

        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:missing_information");
        await AnswerAsync(await AskAsync(_person, Billing, 2, "The price is the one on the order."), Billing, 3, outcome);

        var facts = await FactsAsync(roadmap);
        Assert.True(facts.BlockedExchanges == 0, $"billing answered '{outcome}' after its block and the item still reads blocked: {facts}");
        Assert.Null(facts.Blocker);
    }

    [Fact]
    public async Task A_request_the_person_closed_before_it_was_answered_does_not_clear_a_block()
    {
        // A request closed by the person's close is no answer from the agent, so the block before it still decides.
        var roadmap = await RoadmapAsync();

        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:permission");
        _ = await AskAsync(_person, Billing, 2, "Any way round it?");
        Assert.True(await _console.ForceConcludeAsync("rm1-1", null, T0.AddMinutes(3), Ct));

        var facts = await FactsAsync(roadmap);
        Assert.Equal(1, facts.BlockedExchanges);
        Assert.Equal("permission", facts.Blocker);
    }

    [Fact]
    public async Task Two_closes_under_a_millisecond_apart_still_take_the_later_as_latest()
    {
        var roadmap = await RoadmapAsync();

        var work = await AskAsync(_person, Orders, 1);
        var update = await AskAsync(_person, Orders, 2, "Any update?");

        var answered = T0.AddMinutes(3);
        Assert.True((await _broker.ResultAsync(update, Orders, Requests.Model, Requests.Result((PayloadShape.WholeRequest, "done")), answered, Ct)).Succeeded);
        Assert.True((await _broker.ResultAsync(work, Orders, Requests.Model, Requests.Result((PayloadShape.WholeRequest, "blocked:permission")), answered.AddTicks(4_000), Ct)).Succeeded);

        Assert.Equal(1, (await FactsAsync(roadmap)).BlockedExchanges);
    }

    [Fact]
    public async Task The_blocker_named_is_the_one_still_blocking()
    {
        var roadmap = await RoadmapAsync();

        var toOrders = await AskAsync(_person, Orders, 0);
        var toBilling = await AskAsync(_person, Billing, 0);
        await AnswerAsync(toOrders, Orders, 1, "blocked:permission");
        await AnswerAsync(toBilling, Billing, 2, "blocked:missing_information");

        // Billing is asked again and completes: only orders' part is blocked now, and it named permission.
        await AnswerAsync(await AskAsync(_person, Billing, 3), Billing, 4);

        var facts = await FactsAsync(roadmap);

        Assert.Equal(1, facts.BlockedExchanges);
        Assert.True(facts.Blocker == "permission", $"the item reads blocked on '{facts.Blocker}', a blocker that was cleared; orders' was permission. {facts}");
    }

    [Fact]
    public async Task A_third_agent_added_later_who_completes_leaves_the_blocked_one_blocked()
    {
        var roadmap = await RoadmapAsync();

        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:missing_information");
        await AnswerAsync(await AskAsync(_person, Orders, 2), Orders, 3);
        await AnswerAsync(await AskAsync(_person, Inventory, 4), Inventory, 5);

        var facts = await FactsAsync(roadmap);
        Assert.Equal(1, facts.BlockedExchanges);
        Assert.Equal("missing_information", facts.Blocker);
    }

    [Fact]
    public async Task A_re_ask_of_the_blocked_agent_still_open_reads_open_not_blocked()
    {
        var roadmap = await RoadmapAsync();

        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:missing_information");
        var again = await AskAsync(_person, Billing, 2);

        var facts = await FactsAsync(roadmap);
        Assert.Equal(1, facts.OpenExchanges);
        Assert.Equal([again], facts.OpenIds);
    }

    [Fact]
    public async Task An_agents_own_sub_request_that_ended_blocked_and_was_worked_around_does_not_block_the_item()
    {
        var roadmap = await RoadmapAsync();

        // The person asks billing; billing asks inventory inside the item's conversation; inventory ends blocked;
        // billing works around it and completes the person's request.
        var toBilling = await AskAsync(_person, Billing, 0);
        var toInventory = await AskAsync(Billing, Inventory, 1);
        await AnswerAsync(toInventory, Inventory, 2, "blocked:missing_information");
        await AnswerAsync(toBilling, Billing, 3);

        var facts = await FactsAsync(roadmap);
        Assert.True(facts.BlockedExchanges == 0, $"the person's request completed but the item reads blocked on a sub-request: {facts}");
    }
}
