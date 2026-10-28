using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// First placements racing each other, and a fetch's second resolve under its hold.
/// </summary>
/// <remarks>
/// <b>Found by review, round 22.</b> A fetch whose second resolve failed (the agent cancelling it, say) kept its read hold
/// for good, and the next destroy or purge, and everything behind it, waited for ever. A failed resolve now lets the
/// hold go. The other cases held.
/// </remarks>
public sealed class FirstPlacementsAndTheSecondResolveTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("parley-r22-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Text(text)));

    private static async Task<AsyncClientStreamingCall<Wire.PlaceFileFrame, Wire.PlaceFileReply>> StreamAsync(
        AttachedClient agent, string name, string words, string conversation)
    {
        var place = agent.Client.PlaceFile(cancellationToken: Ct);
        await place.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = agent.Name, Name = name, ConversationId = conversation } }, Ct);
        await place.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(Text(words)) }, Ct);
        return place;
    }

    [Theory]
    [InlineData("a.md", "b.md")]
    [InlineData("notes.md", "notes.md")]
    public async Task Two_first_placements_into_one_conversation_at_once_share_one_folder(string first, string second)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await billing.Client.RequestAsync(Requests.Open(Billing, Orders, "what are the terms", "pricing"), cancellationToken: Ct);

        using var one = await StreamAsync(billing, first, "one", "pricing");
        using var two = await StreamAsync(billing, second, "two", "pricing");
        await Task.Delay(300, Ct);

        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        await one.RequestStream.CompleteAsync();
        await two.RequestStream.CompleteAsync();
        await Task.Delay(300, Ct);
        release.SetResult();
        await occupied;

        var a = await one.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var b = await two.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var folders = Directory.GetDirectories(broker.Runtime.Files.Root).Select(Path.GetFileName).ToArray();
        Assert.True(folders.Length == 1, $"{a.Outcome}/{b.Outcome}; folders: {string.Join(", ", folders)}");

        // Two names both land; one name with two contents has one winner, and the other is refused (SPEC.md §9.5).
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Single(new[] { a, b }, r => r.Rejection == Wire.FileRejection.None);
            Assert.Single(new[] { a, b }, r => r.Rejection == Wire.FileRejection.NameHoldsOtherContent);
        }
        else
        {
            Assert.All(new[] { a, b }, r => Assert.Equal(Wire.FileRejection.None, r.Rejection));
        }

        // What is on disk is what the store says.
        var listed = await FileWire.WaitForAsync(broker.ConnectAsOperator(), _ => true, TimeSpan.FromSeconds(5), Ct);
        foreach (var file in listed)
        {
            var path = broker.Runtime.Files.FilePath(folders[0]!, SharedFileName.Create(file.Name));
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))));
        }
    }

    [Fact]
    public async Task A_first_placement_racing_one_that_found_the_folder_leaves_disk_and_store_agreeing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await billing.Client.RequestAsync(Requests.Open(Billing, Orders, "what are the terms", "pricing"), cancellationToken: Ct);

        // X looks first: no folder. Then another file makes the folder, and Y looks: folder found, no notes.md.
        using var x = await StreamAsync(billing, "notes.md", "from x", "pricing");
        await Task.Delay(300, Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "other.md", Text("o"), "pricing", Ct);
        using var y = await StreamAsync(billing, "notes.md", "from y", "pricing");
        await Task.Delay(300, Ct);

        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        await x.RequestStream.CompleteAsync();
        await y.RequestStream.CompleteAsync();
        await Task.Delay(300, Ct);
        release.SetResult();
        await occupied;

        var a = await x.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var b = await y.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(new[] { a, b }, r => r.Rejection == Wire.FileRejection.None);
        Assert.Single(new[] { a, b }, r => r.Rejection == Wire.FileRejection.NameHoldsOtherContent);

        var file = (await FileWire.WaitForAsync(broker.ConnectAsOperator(), l => l.Any(f => f.Name == "notes.md"), TimeSpan.FromSeconds(5), Ct)).Single(f => f.Name == "notes.md");
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hand_edit_landing_between_the_two_resolves_is_refused_naming_the_bytes_streamed(bool expecting)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("v1"), "pricing", Ct);
        var name = SharedFileName.Create("notes.md");
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");

        // The fetch resolves v1, then waits on the change's turn; the person saves over it by hand inside it, which a
        // placed file never is through Parley, so what streams is not the file and is refused (SPEC.md §9.5).
        var turn = await broker.Runtime.Files.ChangeAsync("pricing", name, Ct);
        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest
            {
                Agent = Orders, Name = "notes.md", ConversationId = "pricing", ExpectSha256 = expecting ? Sha("v1") : string.Empty
            },
            cancellationToken: Ct);
        var frames = ReadAllAsync(call);
        await Task.Delay(300, Ct);
        await File.WriteAllTextAsync(path, "version two, longer", Ct);
        turn.Dispose();

        var (header, refusal) = await frames.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.NotNull(refusal);
        Assert.Equal(Wire.FileRejection.Changed, refusal!.Rejection);
        Assert.Equal(Sha("version two, longer"), refusal.Sha256Now);
        Assert.False(broker.Runtime.Files.IsHeld("pricing", name));
    }

    [Fact]
    public async Task A_removal_landing_between_the_two_resolves_is_refused_and_holds_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("v1"), "pricing", Ct);
        var name = SharedFileName.Create("notes.md");

        var turn = await broker.Runtime.Files.ChangeAsync("pricing", name, Ct);
        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "notes.md", ConversationId = "pricing" }, cancellationToken: Ct);
        var frames = ReadAllAsync(call);
        await Task.Delay(300, Ct);
        var removing = billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { Name = "notes.md", ConversationId = "pricing" } } }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(100, Ct);
        turn.Dispose();

        var (header, refusal) = await frames.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await removing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Either the fetch slipped in first and served v1 whole, or it was refused; never a header and no bytes.
        Assert.True(header is null ? refusal is not null : refusal is null, $"header {header}, refusal {refusal}");
        Assert.False(broker.Runtime.Files.IsHeld("pricing", name));
    }

    [Fact]
    public async Task A_placement_refused_as_another_folders_short_name_releases_its_turn_and_staged_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names are an NTFS feature");
        var dir = _base;
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), Path.Combine(dir, "alpha.db"), Parley.Broker.BrokerOptions.Default, TimeProvider.System);
        await using var server = await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);
        using var inventory = await AttachAsync(agents, "inventory@alpha");
        using var billing = await AttachAsync(agents, Billing);
        var root = server.Runtime.Files.Root;

        await FileWire.PlaceAsync(agents, "inventory@alpha", "x.md", Text("x"), "abcdef", Ct);
        var asked = await agents.RequestAsync(
            new Wire.OpenRequest
            {
                From = "inventory@alpha", To = Billing, ResponderModel = "claude-opus-5", ConversationId = "ABCDEF",
                PayloadJson = """{"ask":"Is tax added net or gross?"}""",
            },
            cancellationToken: Ct);
        Assert.True(Assert.Single(asked.Outcomes).Ok);
        Assert.Equal(Wire.PlaceOutcome.DoesNotFit, (await FileWire.PlaceAsync(agents, "inventory@alpha", new string('a', 250) + ".md", Text("x"), "ABCDEF", Ct)).Outcome);
        using var orders = await AttachAsync(agents, Orders);
        await FileWire.PlaceAsync(agents, Orders, "notes.md", Text("first"), "abcdef-invoices", Ct);
        await FileWire.PlaceAsync(agents, Orders, "notes.md", Text("own"), "abcdef-payments", Ct);
        Assert.SkipUnless(Directory.Exists(Path.Combine(root, "ABCDEF~2")), "no 8.3 names on this volume");

        for (var i = 0; i < 2; i++)
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () =>
                await FileWire.PlaceAsync(agents, "inventory@alpha", "planted.md", Text("p"), "ABCDEF", Ct));
            Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        }

        Assert.Empty(Directory.EnumerateFiles(server.Runtime.Files.Incoming));
        using (await server.Runtime.Files.ChangeEverythingAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
        }

        using (await server.Runtime.Files.ChangeAsync("ABCDEF~2", SharedFileName.Create("planted.md"), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
        }
    }

    private static async Task<IDisposable> AttachAsync(Wire.Agents.AgentsClient agents, string agent)
    {
        var stream = agents.Attach(
            new Wire.AttachRequest { Agent = agent, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
            cancellationToken: Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        return stream;
    }

    [Fact]
    public async Task A_fetch_cancelled_during_its_second_resolve_releases_its_hold()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("v1"), "pricing", Ct);
        var name = SharedFileName.Create("notes.md");

        // The fetch resolves once, then waits on this turn for its hold.
        var turn = await broker.Runtime.Files.ChangeAsync("pricing", name, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "notes.md", ConversationId = "pricing" }, cancellationToken: cancel.Token);
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in call.ResponseStream.ReadAllAsync(cancel.Token))
                {
                }
            }
            catch (Exception ended) when (ended is RpcException or OperationCanceledException)
            {
            }
        }, Ct);
        await Task.Delay(300, Ct);

        // The store is busy when the hold is taken, so the second resolve waits for it; the agent gives up meanwhile.
        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        turn.Dispose();

        // Polled rather than waited a fixed time: a slow runner took longer than one to reach the hold.
        var waited = Stopwatch.StartNew();

        while (!broker.Runtime.Files.IsHeld("pricing", name) && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(broker.Runtime.Files.IsHeld("pricing", name), "the fetch did not take its hold before the second resolve");
        await cancel.CancelAsync();
        await Task.Delay(300, Ct);
        release.SetResult();
        await occupied;
        await reading;
        await Task.Delay(300, Ct);

        Assert.False(broker.Runtime.Files.IsHeld("pricing", name), "a cancelled fetch kept its hold for good");
        var destroying = broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        var finished = await Task.WhenAny(destroying, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.True(finished == destroying, "a destroy waits for ever on the hold the cancelled fetch never released");
    }

    private static async Task<(Wire.SharedFileView? Header, Wire.FileRefusal? Refusal)> ReadAllAsync(
        AsyncServerStreamingCall<Wire.FetchFileFrame> call)
    {
        Wire.SharedFileView? header = null;
        Wire.FileRefusal? refusal = null;

        await foreach (var frame in call.ResponseStream.ReadAllAsync(Ct))
        {
            if (frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Header)
            {
                header = frame.Header;
            }
            else if (frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal)
            {
                refusal = frame.Refusal;
            }
        }

        return (header, refusal);
    }
}
