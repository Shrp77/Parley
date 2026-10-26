using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a live roadmap item's conversation is closed only by the person, never by its participants agreeing (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// The owner's decision after review round 29. A closed conversation never reopens and an item has only the one, so
/// agents agreeing to close it while a part was blocked left the item nowhere to carry on.
/// </remarks>
public sealed class OnlyThePersonClosesAnItemTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly AgentName _person;

    public OnlyThePersonClosesAnItemTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-26T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active');
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
                new RoadmapItemDraft(null, "Call it from the report", Orders, []),
            ]),
            null, null, T0, Ct);
        var content = (await _console.ListProposalsAsync(ProposalState.Waiting, Ct)).Single().Content;
        return (await _console.ConfirmProposalAsync(proposed.Id!.Value, content, null, T0, Ct)).RoadmapId!.Value;
    }

    private async Task<long> AskAsync(AgentName from, AgentName to, int minute, string conversation = "rm1-1")
    {
        var sent = await _broker.RequestAsync(
            from, to, Requests.Model, Requests.Request("Add CSV export"), T0.AddMinutes(minute), conversation, Ct);
        Assert.True(sent.Succeeded, sent.Rejection.ToString());
        return sent.Exchange!.Id;
    }

    private async Task AnswerAsync(long exchange, AgentName by, int minute, string outcome)
    {
        var done = await _broker.ResultAsync(
            exchange, by, Requests.Model, Requests.Result((PayloadShape.WholeRequest, outcome)), T0.AddMinutes(minute), Ct);
        Assert.True(done.Succeeded, done.ToString());
    }

    private Task<string> StateAsync(string conversation) =>
        _connection.ExecuteScalarAsync<string>("SELECT state FROM conversations WHERE id = @Id;", new { Id = conversation })!;

    [Fact]
    public async Task Agents_cannot_agree_to_close_a_blocked_items_conversation()
    {
        _ = await RoadmapAsync();
        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:missing_information");

        var said = await _broker.ConcludeAsync("rm1-1", Billing, finished: true, T0.AddMinutes(2), Ct);

        Assert.Equal(ClosureError.PersonCloses, said.Error);
        Assert.False(said.Concluded);
        Assert.Equal("OPEN", await StateAsync("rm1-1"));
    }

    [Fact]
    public async Task Agents_cannot_start_a_poll_in_an_items_conversation_while_work_is_open()
    {
        _ = await RoadmapAsync();
        _ = await AskAsync(_person, Billing, 0);

        var said = await _broker.ConcludeAsync("rm1-1", Billing, finished: true, T0.AddMinutes(1), Ct);

        Assert.Equal(ClosureError.PersonCloses, said.Error);
        Assert.Equal("OPEN", await StateAsync("rm1-1"));
        Assert.Equal(0, await _connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM conversation_participants WHERE conversation_id = 'rm1-1' AND polled_utc IS NOT NULL;"));
    }

    [Fact]
    public async Task Saying_not_finished_in_an_items_conversation_is_not_refused()
    {
        _ = await RoadmapAsync();
        _ = await AskAsync(_person, Billing, 0);

        Assert.True((await _broker.ConcludeAsync("rm1-1", Billing, finished: false, T0.AddMinutes(1), Ct)).Succeeded);
    }

    [Fact]
    public async Task A_non_participant_learns_nothing_about_an_items_conversation()
    {
        // The participation check comes first, so the refusal never tells an outsider the conversation is an item's.
        _ = await RoadmapAsync();
        _ = await AskAsync(_person, Billing, 0);

        Assert.Equal(
            ClosureError.NotAParticipant,
            (await _broker.ConcludeAsync("rm1-1", Orders, finished: true, T0.AddMinutes(1), Ct)).Error);
    }

    [Fact]
    public async Task The_person_still_closes_it()
    {
        _ = await RoadmapAsync();
        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:permission");

        Assert.True(await _console.ForceConcludeAsync("rm1-1", null, T0.AddMinutes(2), Ct));
        Assert.Equal("CLOSED", await StateAsync("rm1-1"));
    }

    [Fact]
    public async Task A_poll_begun_in_an_items_conversation_before_the_rule_is_discarded_and_closes_nothing()
    {
        // A live store may hold a poll an agent began before this rule existed. Every participant agreeing must not close
        // it, and the poll is dropped rather than left asking a question whose answer can close nothing.
        _ = await RoadmapAsync();
        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:permission");

        var conversations = new ConversationRepository(_connection);
        await conversations.BeginPollAsync("rm1-1", T0.AddMinutes(2), cancellationToken: Ct);
        _ = await conversations.RecordVoteAsync("rm1-1", Billing, Vote.Done, T0.AddMinutes(2), cancellationToken: Ct);
        Assert.Equal("CLOSING", await StateAsync("rm1-1"));

        Assert.False(await conversations.TryConcludeAsync("rm1-1", T0.AddMinutes(3), cancellationToken: Ct));
        Assert.Equal("OPEN", await StateAsync("rm1-1"));
    }

    [Fact]
    public async Task A_removed_items_conversation_closes_by_agreement_as_any_other()
    {
        var roadmap = await RoadmapAsync();
        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1, "blocked:permission");

        var read = (await _console.GetRoadmapAsync(roadmap, Ct))!;
        var kept = new RoadmapContent(
            read.Title,
            [.. read.Items.Where(i => !i.Removed && i.Number != 1).Select(i => new RoadmapItemDraft(i.Number, i.Text, i.IntendedAgent, []))]);
        Assert.True((await _console.EditRoadmapAsync(roadmap, read.Revision, kept, T0.AddMinutes(2), Ct)).Done);

        var said = await _broker.ConcludeAsync("rm1-1", Billing, finished: true, T0.AddMinutes(3), Ct);

        Assert.True(said.Succeeded, said.Error.ToString());
        Assert.True(said.Concluded);
    }

    [Fact]
    public async Task A_conversation_that_is_no_items_closes_by_agreement()
    {
        _ = await RoadmapAsync();
        _ = await AskAsync(Orders, Billing, 0, conversation: "pricing");

        Assert.True((await _broker.ConcludeAsync("pricing", Billing, finished: true, T0.AddMinutes(1), Ct)).Succeeded);
        Assert.True((await _broker.ConcludeAsync("pricing", Orders, finished: true, T0.AddMinutes(2), Ct)).Concluded);
    }
}
