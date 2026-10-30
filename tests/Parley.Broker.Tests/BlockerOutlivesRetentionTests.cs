using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// An item's blocker, and its reworded mark, after retention has removed its conversation's messages.
/// </summary>
/// <remarks>
/// <b>Found by review, round 28.</b> Retention removes messages and keeps exchange rows and result items (§9.4). The
/// blocker and the reworded mark were read through messages, so the default tick turned "closed while blocked" into
/// "closed" once the conversation's messages went (then minutes after a close; since closing stopped shortening the idle
/// period, once it has passed).
/// </remarks>
[Integration]
public sealed class BlockerOutlivesRetentionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly AgentName _person;

    public BlockerOutlivesRetentionTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-06T00:00:00.0000000Z');
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
    public async Task A_closed_item_keeps_its_blocker_after_retention()
    {
        var roadmap = await RoadmapAsync();

        // One part done, one blocked, and the person closes the conversation, the only way an item's closes.
        await AnswerAsync(await AskAsync(_person, Orders, 0), Orders, 1);
        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 2, "blocked:permission");
        Assert.True(await _console.ForceConcludeAsync("rm1-1", null, T0.AddMinutes(3), Ct));

        var before = await FactsAsync(roadmap);
        Assert.Equal("permission", before.Blocker);

        // The default policy, a month on: the age cap reaches every message, delivered or not.
        var pruned = await _console.PruneAsync(RetentionPolicy.Default, T0.AddDays(31), Ct);
        Assert.True(pruned.Removed > 0, $"nothing was pruned: {pruned}");

        var after = await FactsAsync(roadmap);
        Assert.True(after.BlockedExchanges == 1, $"the blocked part was lost: {after}");
        Assert.True(
            after.Blocker == "permission",
            $"retention dropped the blocker: the item now reads closed with no blocker, the block hidden. {after}");
    }

    [Fact]
    public async Task A_closed_item_read_by_everyone_keeps_its_blocker_once_the_default_tick_prunes_it()
    {
        var roadmap = await RoadmapAsync();

        var toOrders = await AskAsync(_person, Orders, 0);
        var toBilling = await AskAsync(_person, Billing, 0);
        _ = await _broker.DrainInboxAsync(Orders, T0.AddMinutes(1), cancellationToken: Ct);
        _ = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct);
        await AnswerAsync(toOrders, Orders, 2);
        await AnswerAsync(toBilling, Billing, 3, "blocked:permission");

        // The person reads both results, as any surface does on opening the conversation, and then closes it.
        _ = await _broker.DrainInboxAsync(_person, T0.AddMinutes(4), cancellationToken: Ct);
        Assert.True(await _console.ForceConcludeAsync("rm1-1", null, T0.AddMinutes(5), Ct));
        Assert.Equal("permission", (await FactsAsync(roadmap)).Blocker);

        // The default policy, the broker's own tick, once the conversation has been quiet for the idle period.
        var tick = await _broker.TickAsync(T0.AddDays(3).AddMinutes(10), Ct);
        Assert.True(tick.Retention.Removed > 0, $"the tick removed nothing, so the blocker was never tested: {tick.Retention}");

        var after = await FactsAsync(roadmap);
        Assert.True(
            after.Blocker == "permission",
            $"once its messages were pruned, the default retention tick dropped the blocker ({tick.Retention}): {after}");
    }

    [Fact]
    public async Task An_open_item_keeps_its_blocker_after_retention()
    {
        var roadmap = await RoadmapAsync();

        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 2, "blocked:missing_information");

        // Open conversation, nothing open in it: past the age cap, every message goes.
        _ = await _console.PruneAsync(RetentionPolicy.Default, T0.AddDays(31), Ct);

        var after = await FactsAsync(roadmap);
        Assert.Equal(1, after.BlockedExchanges);
        Assert.True(after.Blocker == "missing_information", $"retention dropped the blocker of an open item: {after}");
    }

    [Fact]
    public async Task A_reworded_item_still_says_so_after_retention()
    {
        var roadmap = await RoadmapAsync();

        await AnswerAsync(await AskAsync(_person, Billing, 0), Billing, 1);

        var read = (await _console.GetRoadmapAsync(roadmap, Ct))!;
        var edited = new RoadmapContent(
            read.Title,
            [.. read.Items.Where(i => !i.Removed).OrderBy(i => i.Position).Select(i => new RoadmapItemDraft(
                i.Number, i.Number == 1 ? "Add CSV and TSV export" : i.Text, i.IntendedAgent, i.Needs))]);
        Assert.True((await _console.EditRoadmapAsync(roadmap, read.Revision, edited, T0.AddMinutes(5), Ct)).Done);

        Assert.True((await FactsAsync(roadmap)).TextEditedSinceStarted);

        _ = await _console.PruneAsync(RetentionPolicy.Default, T0.AddDays(31), Ct);

        Assert.True((await FactsAsync(roadmap)).TextEditedSinceStarted, "retention erased that the item was reworded since it began");
    }
}
