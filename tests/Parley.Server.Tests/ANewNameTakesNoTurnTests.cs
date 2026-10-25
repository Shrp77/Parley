using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a placement of a new name into a folder that exists, which takes no turn, still races safely (review, round 25).
/// </summary>
/// <remarks>
/// Kept from review round 25, where each held: two placements of one new name, a new name landing inside a destroy or
/// a prune, the person's file refused to an agent before any turn, and a change queued behind one given up.
/// </remarks>
public sealed class ANewNameTakesNoTurnTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static async Task<AsyncClientStreamingCall<Wire.PlaceFileFrame, Wire.PlaceFileReply>> BeginAsync(
        AttachedClient agent, string name, string words, string conversation)
    {
        var place = agent.Client.PlaceFile(cancellationToken: Ct);
        await place.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = agent.Name, Name = name, ConversationId = conversation } }, Ct);
        await place.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(Text(words)) }, Ct);
        return place;
    }

    private static string[] OnDisk(LoopbackBroker broker, string folder)
    {
        var path = broker.Runtime.Files.FolderPath(folder);
        return Directory.Exists(path) ? [.. Directory.GetFileSystemEntries(path).Select(p => Path.GetFileName(p)!)] : [];
    }

    [Fact]
    public async Task Two_placements_of_one_new_name_by_one_agent_leave_one_file_and_store_and_disk_agree()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(orders.Client, Orders, "start.md", Text("x"), "bait", Ct);

        // Both looked before either committed: neither saw new.md, so neither took a turn.
        using var first = await BeginAsync(orders, "new.md", "first words", "bait");
        using var second = await BeginAsync(orders, "new.md", "second words!", "bait");
        await Task.Delay(300, Ct);

        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        await Task.Delay(100, Ct);

        await first.RequestStream.CompleteAsync();
        await second.RequestStream.CompleteAsync();
        await Task.Delay(300, Ct);
        release.SetResult();
        await occupied;

        var a = await first.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var b = await second.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
        var row = Assert.Single(listed.Files, f => f.Name == "new.md");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(broker.Runtime.Files.FolderPath("bait"), "new.md"), Ct);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        Assert.True(
            new[] { a.Outcome, b.Outcome }.Order().SequenceEqual(new[] { Wire.PlaceOutcome.Placed, Wire.PlaceOutcome.Replaced }.Order()),
            $"outcomes {a.Outcome}, {b.Outcome}");
        Assert.Equal(row.Sha256, sha);
        Assert.Equal(["new.md", "start.md"], OnDisk(broker, "bait").Order().ToArray());
    }

    [Fact]
    public async Task A_new_name_landing_between_a_destroys_folder_read_and_its_transaction_goes_with_the_folder()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "start.md", Text("x"), "pricing", Ct);
        using var place = await BeginAsync(billing, "new.md", "net 30", "pricing");
        await Task.Delay(300, Ct);

        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        await Task.Delay(100, Ct);

        // The destroy's folder read queues first, the placement's commit second, the destroy's transaction third.
        var destroying = broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(300, Ct);
        await place.RequestStream.CompleteAsync();
        await Task.Delay(300, Ct);
        release.SetResult();
        await occupied;

        var placed = await place.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var destroyed = await destroying.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var left = OnDisk(broker, "pricing");

        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.Contains(destroyed.Destroyed, t => t.Table == "shared_files" && t.Rows == 2);
        Assert.True(
            left.Length == 0 || left.Contains(".parley-destroyed"),
            $"placement {placed.Outcome}, destroy {destroyed.Verdict}; left on disk, unmarked: {string.Join(", ", left)}");

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
        Assert.Empty(listed.Files);
    }

    [Fact]
    public async Task A_new_name_placed_while_a_destroy_waits_on_a_download_goes_with_the_folder()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "start.md", Text("x"), "pricing", Ct);
        var slow = await broker.Runtime.Files.HoldForReadingAsync("pricing", SharedFileName.Create("start.md"), Ct);

        var destroying = broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(200, Ct);

        // Inside the hold: the new name takes no turn, so it is not held back.
        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "new.md", Text("net 30"), "pricing", Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await slow.DisposeAsync();
        var destroyed = await destroying.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var left = OnDisk(broker, "pricing");

        Assert.True(
            left.Length == 0 || left.Contains(".parley-destroyed"),
            $"placement {placed.Outcome}, destroy {destroyed.Verdict}; left on disk, unmarked: {string.Join(", ", left)}");
        Assert.Contains(destroyed.Destroyed, t => t.Table == "shared_files" && t.Rows == 2);
    }

    [Fact]
    public async Task A_new_name_placed_while_a_prune_waits_on_a_download_is_listed_exactly_when_it_is_on_disk()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "start.md", Text("x"), "pricing", Ct);
        var slow = await broker.Runtime.Files.HoldForReadingAsync("pricing", SharedFileName.Create("start.md"), Ct);

        var pruning = broker.ConnectAsOperator().PruneFilesAsync(new Wire.Empty(), cancellationToken: Ct).ResponseAsync;
        await Task.Delay(200, Ct);

        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "new.md", Text("net 30"), "pricing", Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await slow.DisposeAsync();
        var pruned = await pruning.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
        var left = OnDisk(broker, "pricing");

        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.True(
            listed.Files.Select(f => f.Name).Order().SequenceEqual(left.Order()),
            $"placement {placed.Outcome}, pruned {pruned.FilesRemoved}; listed [{string.Join(", ", listed.Files.Select(f => f.Name))}], on disk [{string.Join(", ", left)}]");
    }

    [Fact]
    public async Task The_persons_file_is_refused_to_an_agent_before_any_turn_and_removed_for_the_person()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(billing.Client, Billing, "start.md", Text("x"), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "mine.md");
        await File.WriteAllTextAsync(path, "the person's", Ct);
        await FileWire.WaitForAsync(operators, l => l.Any(f => f.Name == "mine.md"), TimeSpan.FromSeconds(10), Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromMilliseconds(300);
        var reading = await broker.Runtime.Files.HoldForReadingAsync("pricing", SharedFileName.Create("mine.md"), Ct);
        var mine = new Wire.FileRef { ConversationId = "pricing", Name = "mine.md" };

        var refused = await billing.Client.RemoveOwnFilesAsync(new Wire.RemoveFilesRequest { Agent = Billing, Files = { mine } }, cancellationToken: Ct)
            .ResponseAsync.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Task.Delay(600, Ct);

        Assert.Empty(refused.Removed);
        Assert.Equal(Wire.FileRejection.NamePlacedByAnother, Assert.Single(refused.Refused).Rejection);
        Assert.Equal(string.Empty, refused.Refused[0].PlacedBy);
        Assert.False(broker.Runtime.Files.IsOverdue("pricing", SharedFileName.Create("mine.md")), "the refused removal made the person's download overdue");

        var removing = operators.RemoveFilesAsync(new Wire.RemoveFilesRequest { Files = { mine } }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(200, Ct);
        Assert.False(removing.IsCompleted, "the person's removal did not wait for the download");
        await reading.DisposeAsync();

        var removed = await removing.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Single(removed.Removed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_new_name_racing_the_agents_own_removal_of_that_name_is_never_told_it_is_the_persons()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(orders.Client, Orders, "start.md", Text("x"), "bait", Ct);

        var told = new List<string>();

        for (var round = 0; round < 60; round++)
        {
            var name = $"y{round}.md";

            // The slow placement looks first, while no file has the name, so it takes no turn.
            using var slow = await BeginAsync(orders, name, "second", "bait");
            await Task.Delay(30, Ct);
            await FileWire.PlaceAsync(orders.Client, Orders, name, Text("first"), "bait", Ct);

            var removing = orders.Client.RemoveOwnFilesAsync(
                new Wire.RemoveFilesRequest { Agent = Orders, Files = { new Wire.FileRef { ConversationId = "bait", Name = name } } },
                cancellationToken: Ct).ResponseAsync;
            await Task.Delay(round % 5, Ct);
            await slow.RequestStream.CompleteAsync();

            var placed = await slow.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await removing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            if (placed.Outcome == Wire.PlaceOutcome.PlacedByAnother)
            {
                told.Add($"{name}: '{placed.PlacedBy}' {placed.Detail}");
            }
        }

        Assert.True(told.Count == 0, $"{told.Count} of 60 told the name was another's: {string.Join(" | ", told.Take(3))}");
    }

    [Fact]
    public async Task A_change_queued_behind_one_given_up_still_makes_the_reader_overdue()
    {
        var root = Path.Combine(Path.GetTempPath(), "parley-queued-" + Guid.NewGuid().ToString("N"));
        using var location = new FileLocationStore(root, root + "-incoming", deleteOnDispose: true);
        var name = SharedFileName.Create("notes.md");

        await using (var staged = location.Stage())
        {
            await staged.WriteAsync(Text("a"), Ct);
            await staged.FinishAsync(Ct);
            staged.CommitTo("pricing", name);
        }

        location.StallAfter = TimeSpan.FromMilliseconds(200);
        var reader = await location.HoldForReadingAsync("pricing", name, Ct);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = location.ChangeAsync("pricing", name, cancel.Token);
        var second = location.ChangeAsync("pricing", name, Ct);
        await Task.Delay(50, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        await Task.Delay(500, Ct);
        Assert.False(second.IsCompleted);
        Assert.True(location.IsOverdue("pricing", name), "the queued change's waiting did not make the reader overdue");

        await reader.DisposeAsync();
        (await second.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        Assert.False(location.IsOverdue("pricing", name));
    }
}
