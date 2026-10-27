using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a file the person puts under the name of an agent's file they deleted is theirs once the half second an edit is
/// read within has passed (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// A file found gone with no rename report is judged gone on a second look a quiet period later, so a rename whose event
/// came late keeps its placer. A file put there in that time read as an edit of the agent's file, which the agent could
/// then remove from disk.
/// </remarks>
public sealed class ANewFileWhereOneWentIsThePersonsTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData(750, 50)]
    [InlineData(900, 100)]
    public async Task The_agent_cannot_remove_it_before_the_second_look_even_after_a_list(int afterMs, int thenMs)
    {
        // A list measures what it finds, and recorded the person's file as an edit of the agent's; and a removal checked
        // only the placer, so the agent deleted the person's bytes before the watcher judged them.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("agent's"), "pricing", Ct);
        await Task.Delay(1500, Ct);

        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        File.Delete(path);
        await Task.Delay(afterMs, Ct);
        await File.WriteAllTextAsync(path, "the person's own, different file", Ct);
        await Task.Delay(thenMs, Ct);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
        var row = Assert.Single(listed.Files);

        _ = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = row.ConversationId, Name = "notes.md" } } },
            cancellationToken: Ct);

        await Task.Delay(2000, Ct);
        Assert.True(File.Exists(path), "the agent deleted the person's bytes");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(800)]
    public async Task The_agents_same_bytes_placed_again_after_a_delete_are_written(int afterMs)
    {
        // The row outlives its file until the watcher judges it gone, and the same bytes again were answered as already
        // current with nothing written; then the row went, and the agent's file was never there.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("agent's"), "pricing", Ct);
        await Task.Delay(1500, Ct);

        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        File.Delete(path);
        await Task.Delay(afterMs, Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("agent's"), "pricing", Ct);
        await Task.Delay(3000, Ct);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);

        Assert.True(File.Exists(path), "the placement was answered and nothing was written");
        Assert.Equal(Billing, Assert.Single(listed.Files).PlacedBy);
    }

    [Theory]
    [InlineData(750)]
    [InlineData(900)]
    [InlineData(1100)]
    public async Task A_persons_new_file_put_after_the_first_look_is_the_persons(int afterMs)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("agent's"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);
        await Task.Delay(1500, Ct);

        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        File.Delete(path);
        await Task.Delay(afterMs, Ct);

        await File.WriteAllTextAsync(path, "the person's own, different file", Ct);
        await Task.Delay(3000, Ct);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
        var row = Assert.Single(listed.Files);

        _ = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = row.ConversationId, Name = "notes.md" } } },
            cancellationToken: Ct);

        Assert.True(string.IsNullOrEmpty(row.PlacedBy), $"after {afterMs}ms the person's new file is held by '{row.PlacedBy}'");
        Assert.True(File.Exists(path), "the agent deleted the person's bytes");
    }
}
