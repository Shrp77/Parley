using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a roadmap is intact after the broker restarts (SPEC.md §5.7, SC-007).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing about a roadmap lives in a broker's memory.</b> Its items, their order, their reserved conversations and
/// what became of the proposal are rows, and where an item stands is derived from rows each time it is read. So a store
/// closed and opened again, as a broker restart does, must answer exactly as it did. A file store, because an in-memory
/// one ends with its connection and would prove nothing.
/// </para>
/// </remarks>
[Integration]
public sealed class RoadmapSurvivesARestartTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-roadmap-restart-" + Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public RoadmapSurvivesARestartTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "parley.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as in ConcurrentPruneTests: clearing the pools is process-global.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_and_edited_roadmap_reads_the_same_after_the_store_is_opened_again()
    {
        long id;
        Roadmap before;

        using (var connection = ParleyDatabase.OpenAndMigrate(_path))
        {
            var agents = new AgentRepository(connection);

            foreach (var name in new[] { Orders, Billing })
            {
                await agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), Ct);
            }

            var roadmaps = new RoadmapRepository(connection);
            var (proposal, problem) = await roadmaps.ProposeAsync(
                Orders,
                null,
                null,
                new RoadmapContent("Billing export",
                [
                    new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                    new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
                    new RoadmapItemDraft(null, "Write it up", null, []),
                ]),
                T0,
                cancellationToken: Ct);

            Assert.Null(problem);

            var waiting = await roadmaps.GetProposalAsync(proposal!.Value, Ct);
            var made = await roadmaps.ConfirmAsync(waiting!.Id, waiting.Content, null, T0, cancellationToken: Ct);

            id = made.RoadmapId!.Value;

            // Edited after confirming, so the revision, the removed item and its kept conversation name are all in play.
            var edit = await roadmaps.EditAsync(
                id,
                1,
                new RoadmapContent("Billing export",
                [
                    new RoadmapItemDraft(1, "Add CSV export", Billing, []),
                    new RoadmapItemDraft(2, "Call it from the report", Orders, [1]),
                ]),
                T0.AddMinutes(1),
                cancellationToken: Ct);

            Assert.Null(edit.Problem);

            before = (await roadmaps.GetAsync(id, Ct))!;
        }

        using var reopened = ParleyDatabase.OpenAndMigrate(_path);
        var after = await new RoadmapRepository(reopened).GetAsync(id, Ct);

        Assert.NotNull(after);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Origin, after.Origin);
        Assert.Equal(
            before.Items.Select(i => (i.Number, i.Position, i.Text, i.IntendedAgent, i.ConversationName, i.Removed, string.Join(",", i.Needs))),
            after.Items.Select(i => (i.Number, i.Position, i.Text, i.IntendedAgent, i.ConversationName, i.Removed, string.Join(",", i.Needs))));
        Assert.Contains(after.Items, i => i.Removed && i.ConversationName == "rm1-3");
    }
}
