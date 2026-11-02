using System.Text;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a prune, a removal or a purge deletes only what it removed, never what was placed or recorded while it ran
/// (SPEC.md §9.5, §14).
/// </summary>
/// <remarks>
/// A prune removed the rows and then deleted the bytes after its turn, so a placement of a pruned name made in between,
/// and told Placed, had its bytes deleted. A purge read the folders it would delete before purging, so a conversation
/// the agent joined in between was purged and its folder left on disk with no row naming it, neither deleted nor
/// counted.
/// </remarks>
[Integration]
public sealed class ARemovalTakesOnlyWhatItRemovedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_purge_deletes_the_folder_of_a_conversation_its_agent_joined_while_it_waited()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/b", Ct);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/c", Ct);
        var operators = broker.ConnectAsOperator();

        var e = Assert.Single(
            (await docs.Client.RequestAsync(Requests.Open(docs.Name, billing.Name, "the shared conversation"), cancellationToken: Ct))
            .Outcomes);

        var placed = await FileWire.PlaceAsync(docs.Client, docs.Name, "notes.md", Encoding.UTF8.GetBytes("docs' notes"), e.ConversationId, Ct);
        Assert.Equal(Wire.FileRejection.None, placed.Rejection);

        var folder = await broker.Runtime.UseConnectionAsync(
            (connection, _, ct) => new Parley.Store.SharedFileRepository(connection).GetFolderAsync(e.ConversationId, cancellationToken: ct),
            Ct);
        var folderPath = broker.Runtime.Files.FolderPath(folder!);
        Assert.True(Directory.Exists(folderPath));

        // Hold the gate so the purge's folder read, then the request, then the purge itself queue in that order.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holding = broker.Runtime.UseConnectionAsync(
            async (_, _, _) =>
            {
                entered.SetResult();
                await release.Task;
                return 0;
            },
            Ct);
        await entered.Task;

        var purge = operators.PurgeAsync(new Wire.AgentRequest { Agent = infra.Name }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(500, Ct);

        var request = docs.Client.RequestAsync(
            Requests.Open(docs.Name, infra.Name, "a question for infra", e.ConversationId), cancellationToken: Ct).ResponseAsync;
        await Task.Delay(500, Ct);

        release.SetResult();
        await holding;

        var purged = await purge;
        var asked = await request;

        var listed = await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: Ct);
        var survives = listed.Conversations.Any(c => c.ConversationId == e.ConversationId);

        var files = await operators.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);

        var said = $"purge={purged.Verdict}; request ok={asked.Outcomes[0].Ok} rej={asked.Outcomes[0].Rejection}; "
                   + $"conversation survives={survives}; folder on disk={Directory.Exists(folderPath)}; "
                   + $"listed files={files.Files.Count}; not listed={files.FilesNotListed}; "
                   + $"bytes on disk={(Directory.Exists(folderPath) ? string.Join(",", Directory.EnumerateFiles(folderPath).Select(Path.GetFileName)) : "")}";

        TestContext.Current.SendDiagnosticMessage(said);

        // The conversation went with the purge, so its folder must have gone.
        Assert.True(survives || !Directory.Exists(folderPath), said);
    }

    [Fact]
    public async Task A_prune_never_deletes_the_bytes_of_a_placement_made_after_its_rows_went()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/b", Ct);
        var operators = broker.ConnectAsOperator();

        var e = Assert.Single(
            (await docs.Client.RequestAsync(Requests.Open(docs.Name, billing.Name, "the shared conversation"), cancellationToken: Ct))
            .Outcomes);

        // Many files sorted before the one that matters, so the prune's delete loop takes a while to reach it.
        for (var i = 0; i < 600; i++)
        {
            var filler = await FileWire.PlaceAsync(docs.Client, docs.Name, $"a{i:D4}.md", Encoding.UTF8.GetBytes($"filler {i}"), e.ConversationId, Ct);
            Assert.Equal(Wire.FileRejection.None, filler.Rejection);
        }

        var placed = await FileWire.PlaceAsync(docs.Client, docs.Name, "zz.md", Encoding.UTF8.GetBytes("docs' old notes"), e.ConversationId, Ct);
        Assert.Equal(Wire.FileRejection.None, placed.Rejection);

        var folder = await broker.Runtime.UseConnectionAsync(
            (connection, _, ct) => new Parley.Store.SharedFileRepository(connection).GetFolderAsync(e.ConversationId, cancellationToken: ct),
            Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath(folder!), "zz.md");

        // The person deletes the file by hand; nothing has listed since.
        File.Delete(path);

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holding = broker.Runtime.UseConnectionAsync(
            async (_, _, _) =>
            {
                entered.SetResult();
                await release.Task;
                return 0;
            },
            Ct);
        await entered.Task;

        var prune = operators.PruneFilesAsync(new Wire.Empty(), cancellationToken: Ct).ResponseAsync;
        await Task.Delay(500, Ct);

        var placing = FileWire.PlaceAsync(billing.Client, billing.Name, "zz.md", Encoding.UTF8.GetBytes("billing's new notes"), e.ConversationId, Ct);
        await Task.Delay(500, Ct);

        release.SetResult();
        await holding;

        var again = await placing;
        var pruned = await prune;

        var onDisk = File.Exists(path);
        var listed = await operators.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);

        var said = $"pruned={pruned.FilesRemoved}; placement outcome={again.Outcome} rejection={again.Rejection}; "
                   + $"bytes on disk after={onDisk}; listed after={string.Join(",", listed.Files.Select(f => f.Name))}";
        TestContext.Current.SendDiagnosticMessage(said);

        Assert.True(again.Rejection != Wire.FileRejection.None || onDisk, said);
    }
}
