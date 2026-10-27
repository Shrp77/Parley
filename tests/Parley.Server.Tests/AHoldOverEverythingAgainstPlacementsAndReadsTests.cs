using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a hold over the whole location (destroy, purge, prune) is not slipped past by a read or a placement.
/// </summary>
/// <remarks>
/// <b>Found by review, round 21.</b> A conversation's first placement had no turn to take, so it committed between a
/// destroy reading the folder and destroying it, and the folder stayed on disk unmarked; and a download that waited out
/// a destroy whose folder a viewer held open served the destroyed file. A first placement now takes a turn the hold
/// waits for or out, and a download resolves its file again once it holds it. The other cases held.
/// </remarks>
public sealed class AHoldOverEverythingAgainstPlacementsAndReadsTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_download_that_waited_out_a_destroy_is_refused_and_holds_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net 30"), "pricing", Ct);
        var folder = broker.Runtime.Files.FolderPath("pricing");
        var name = SharedFileName.Create("notes.md");

        // A slow download, standing in for one in progress, so the destroy waits and a second fetch queues behind it.
        var slow = await broker.Runtime.Files.HoldForReadingAsync("pricing", name, Ct);
        var destroying = broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(200, Ct);
        var fetching = FetchAsync(orders, "notes.md");
        await Task.Delay(200, Ct);
        Assert.False(fetching.IsCompleted, "the second fetch did not wait for the destroy");

        await slow.DisposeAsync();
        await destroying.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var (header, bytes, refusal) = await fetching.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.NotNull(refusal);
        Assert.NotEqual(Wire.FileRejection.None, refusal!.Rejection);
        Assert.Null(header);
        Assert.Empty(bytes);
        Assert.False(broker.Runtime.Files.IsHeld("pricing", name), "a refused fetch kept its hold");
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_download_that_waited_out_a_destroy_whose_folder_stayed_open_does_not_serve_the_destroyed_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("only Windows refuses to delete a file held open");
        }

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("destroyed words"), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        var name = SharedFileName.Create("notes.md");

        var slow = await broker.Runtime.Files.HoldForReadingAsync("pricing", name, Ct);
        var destroying = broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(200, Ct);
        var fetching = FetchAsync(orders, "notes.md");
        await Task.Delay(200, Ct);
        Assert.False(fetching.IsCompleted, "the second fetch did not wait for the destroy");

        // The person has it open in a viewer that shares reading, so the destroy cannot delete it and marks the folder.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await slow.DisposeAsync();
            var destroyed = await destroying.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Contains(destroyed.Destroyed, t => t.Table == "shared_files");

            var (header, bytes, refusal) = await fetching.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.True(
                refusal is { Rejection: not Wire.FileRejection.None },
                $"a fetch that waited out the destroy served the destroyed conversation's file: {System.Text.Encoding.UTF8.GetString(bytes)}");
            Assert.Null(header);
        }
    }

    [Fact]
    public async Task A_first_placement_landing_inside_a_destroy_leaves_no_folder_behind()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        // A conversation with no files yet, so it has no folder.
        await billing.Client.RequestAsync(Requests.Open(Billing, Orders, "what are the terms", "pricing"), cancellationToken: Ct);

        // The first file's bytes are on their way; the look before staging has been done.
        using var place = billing.Client.PlaceFile(cancellationToken: Ct);
        await place.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "notes.md", ConversationId = "pricing" } }, Ct);
        await place.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(Text("net 30")) }, Ct);
        await Task.Delay(300, Ct);

        // Hold the store's connection so the order the destroy and the placement reach it is the one under test.
        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        await Task.Delay(100, Ct);

        var destroying = broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(300, Ct);
        await place.RequestStream.CompleteAsync();
        await Task.Delay(300, Ct);

        release.SetResult();
        await occupied;

        var placed = await place.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var destroyed = await destroying.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var folder = broker.Runtime.Files.FolderPath("pricing");
        var left = Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ToArray() : [];

        Assert.True(
            left.Length == 0 || left.Contains(".parley-pending-removal"),
            $"placement {placed.Outcome}, destroy {destroyed.Verdict} ({string.Join(", ", destroyed.Destroyed.Select(t => $"{t.Table}={t.Rows}"))}); "
            + $"left on disk, unmarked: {string.Join(", ", left)}");
    }

    [Fact]
    public async Task A_read_cancelled_while_it_waits_out_a_hold_holds_nothing()
    {
        using var location = NewLocation(out _);
        await WriteAsync(location, "pricing", "notes.md", "a");
        var name = SharedFileName.Create("notes.md");

        var everything = await location.ChangeEverythingAsync(Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reading = location.HoldForReadingAsync("pricing", name, cancel.Token);
        await Task.Delay(50, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        everything.Dispose();
        Assert.False(location.IsHeld("pricing", name));
        using var change = await location.ChangeAsync("pricing", name, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_hold_cancelled_while_it_enters_gates_lets_reads_and_the_next_hold_in()
    {
        using var location = NewLocation(out _);
        await WriteAsync(location, "pricing", "a.md", "a");
        await WriteAsync(location, "pricing", "b.md", "b");
        var a = SharedFileName.Create("a.md");
        var b = SharedFileName.Create("b.md");

        // Both have gates; a reader on b keeps the hold from finishing.
        await (await location.HoldForReadingAsync("pricing", a, Ct)).DisposeAsync();
        var reader = await location.HoldForReadingAsync("pricing", b, Ct);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var holding = location.ChangeEverythingAsync(cancel.Token);
        await Task.Delay(50, Ct);
        Assert.False(holding.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holding);

        // a's gate was entered and must be open again; a read of a never-read file is let in.
        await using (await location.HoldForReadingAsync("pricing", a, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
        }

        await WriteAsync(location, "pricing", "c.md", "c");
        await using (await location.HoldForReadingAsync("pricing", SharedFileName.Create("c.md"), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
        }

        var next = location.ChangeEverythingAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(next.IsCompleted, "the next hold did not wait for the reader of b");
        await reader.DisposeAsync();
        (await next.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
    }

    [Fact]
    public async Task Two_holds_over_everything_take_turns_and_a_read_waits_for_both()
    {
        using var location = NewLocation(out _);
        await WriteAsync(location, "pricing", "notes.md", "a");
        var name = SharedFileName.Create("notes.md");

        var first = await location.ChangeEverythingAsync(Ct);
        var second = location.ChangeEverythingAsync(Ct);
        var reading = location.HoldForReadingAsync("pricing", name, Ct);
        await Task.Delay(50, Ct);
        Assert.False(second.IsCompleted, "two holds over everything at once");
        Assert.False(reading.IsCompleted);

        first.Dispose();

        // The read may slip in between the two, in which case the second hold waits for it to end; or it waits for the
        // second. Never does it run inside a hold.
        var firstDone = await Task.WhenAny(second, reading).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        if (firstDone == reading)
        {
            Assert.False(second.IsCompleted, "a read ran inside the second hold");
            await (await reading).DisposeAsync();
            (await second.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
            return;
        }

        var held = await second;

        await Task.Delay(50, Ct);
        Assert.False(reading.IsCompleted, "a read ran inside the second hold");
        held.Dispose();
        await (await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct)).DisposeAsync();
    }

    private static FileLocationStore NewLocation(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "parley-hold-" + Guid.NewGuid().ToString("N"));
        return new FileLocationStore(root, root + "-incoming", deleteOnDispose: true);
    }

    private static async Task WriteAsync(FileLocationStore location, string folder, string name, string content)
    {
        await using var staged = location.Stage();
        await staged.WriteAsync(Text(content), Ct);
        await staged.FinishAsync(Ct);
        staged.CommitTo(folder, SharedFileName.Create(name), FileLocationStoreTests.Recorded(location.FilePath(folder, SharedFileName.Create(name))));
    }

    private static async Task<(Wire.SharedFileView? Header, byte[] Bytes, Wire.FileRefusal? Refusal)> FetchAsync(
        AttachedClient agent, string name, string conversation = "pricing")
    {
        using var call = agent.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = agent.Name, Name = name, ConversationId = conversation },
            cancellationToken: Ct);

        Wire.SharedFileView? header = null;
        Wire.FileRefusal? refusal = null;
        using var bytes = new MemoryStream();

        // A call that fails is not a refusal: it throws here, so a server fault fails the test rather than passing as one.
        await foreach (var frame in call.ResponseStream.ReadAllAsync(Ct))
        {
            switch (frame.FrameCase)
            {
                case Wire.FetchFileFrame.FrameOneofCase.Header:
                    header = frame.Header;
                    break;
                case Wire.FetchFileFrame.FrameOneofCase.Chunk:
                    frame.Chunk.WriteTo(bytes);
                    break;
                case Wire.FetchFileFrame.FrameOneofCase.Refusal:
                    refusal = frame.Refusal;
                    break;
            }
        }

        return (header, bytes.ToArray(), refusal);
    }
}
