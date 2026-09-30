using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Files when the broker restarts, when a person edits what the broker is replacing, when a placer cannot object, and when
/// the wire carries nonsense (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Written as probes by the review gate (runs 1.3, 1.4, 2.3 and 2.4) and kept: each passed when written, and each holds a
/// property that would otherwise be held only by reading the code.
/// </remarks>
public sealed class FilesAcrossRestartAndMalformedInputTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Broker_replacements_racing_hand_edits_end_with_the_row_matching_the_disk()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("v0"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);

        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "notes.md");
        var noticesSeen = new List<Wire.FileNotice>();

        var byBroker = Task.Run(async () =>
        {
            for (var i = 1; i <= 20; i++)
            {
                try
                {
                    await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text($"broker {i}"), "pricing", Ct);
                }
                catch (RpcException)
                {
                    // An abort is an allowed answer to a file that kept changing.
                }

                await Task.Delay(15, Ct);
            }
        }, Ct);

        var byHand = Task.Run(async () =>
        {
            for (var i = 1; i <= 20; i++)
            {
                try
                {
                    await File.WriteAllTextAsync(path, $"hand {i}", Ct);
                }
                catch (Exception held) when (held is IOException or UnauthorizedAccessException)
                {
                    // Held by a placement's move or a hash: what a person meets when saving over a busy file.
                }

                await Task.Delay(17, Ct);
            }
        }, Ct);

        var collecting = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

                lock (noticesSeen)
                {
                    noticesSeen.AddRange(inbox.FileNotices);
                }

                await Task.Delay(25, Ct);
            }
        }, Ct);

        await Task.WhenAll(byBroker, byHand, collecting);

        // Past the watcher's quiet period, and then a list, which reconciles before it answers.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        var row = Assert.Single((await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
        var onDisk = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct)));

        Assert.Equal(onDisk, row.Sha256);
        Assert.Equal(Billing, row.PlacedBy);

        var last = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        noticesSeen.AddRange(last.FileNotices);

        Assert.All(noticesSeen, n => Assert.NotEqual(n.Sha256Had, n.Sha256Now));

        if (noticesSeen.Count > 0)
        {
            Assert.Equal(row.Sha256, noticesSeen[^1].Sha256Now);
        }
    }

    [Fact]
    public async Task Across_a_restart_a_pending_notice_survives_and_an_unreported_fetch_is_forgotten()
    {
        var directory = Directory.CreateTempSubdirectory("parley-restart-").FullName;
        var store = Path.Combine(directory, "alpha.db");

        try
        {
            Wire.SharedFileView unreported;
            string v1, v2;

            await using (var first = await Served.StartAsync(store, Ct))
            {
                var billing = await first.AttachAsync(Billing, Ct);
                var orders = await first.AttachAsync(Orders, Ct);

                v1 = (await FileWire.PlaceAsync(billing, Billing, "notes.md", Text("v1"), "pricing", Ct)).Sha256;
                await FileWire.FetchAndReportAsync(orders, Orders, "notes.md", "pricing", Ct);
                v2 = (await FileWire.PlaceAsync(billing, Billing, "notes.md", Text("v2"), "pricing", Ct)).Sha256;

                unreported = await FetchOnlyAsync(orders, "notes.md");
            }

            await using var second = await Served.StartAsync(store, Ct);
            await second.Server.Runtime.Watcher.Scanned.WaitAsync(Within, Ct);

            var returned = await second.AttachAsync(Orders, Ct);

            var inbox = await returned.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
            var notice = Assert.Single(inbox.FileNotices);
            Assert.Equal(v1, notice.Sha256Had);
            Assert.Equal(v2, notice.Sha256Now);

            var reported = await returned.FetchedFileAsync(
                new Wire.FetchedFileRequest { Agent = Orders, ConversationId = unreported.ConversationId, Name = unreported.Name, Sha256 = unreported.Sha256 },
                cancellationToken: Ct);
            Assert.Equal(Wire.FileRejection.NotFetched, reported.Rejection);

            var row = Assert.Single((await second.Operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
            Assert.Equal(v2, row.Sha256);
            Assert.Equal(Billing, row.PlacedBy);

            var staging = store + StoreFiles.FilesIncoming;
            Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(), "staging was not emptied at start");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public async Task Neither_a_file_the_person_copied_in_nor_one_a_retired_agent_placed_can_be_taken_by_another_agent()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await using (var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct))
        {
            await FileWire.PlaceAsync(billing.Client, Billing, "billings.md", Text("b"), "pricing", Ct);
            await RequestAsync(billing, Orders, "pricing");
        }

        await broker.WaitUntilDetachedAsync(Billing, Within);

        var operators = broker.ConnectAsOperator();
        Assert.True((await operators.RetireAsync(new Wire.AgentRequest { Agent = Billing }, cancellationToken: Ct)).Changed);

        await File.WriteAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "copied.md"), "by hand", Ct);
        await FileWire.WaitForAsync(operators, files => files.Any(f => f.Name == "copied.md"), Within, Ct);

        var taken = new List<string>();

        foreach (var name in new[] { "billings.md", "copied.md", "BILLINGS.MD", "Copied.md" })
        {
            var placed = await FileWire.PlaceAsync(orders.Client, Orders, name, Text("orders'"), "pricing", Ct);

            if (placed.Rejection != Wire.FileRejection.NamePlacedByAnother)
            {
                taken.Add($"placing {name}: {placed.Rejection} {placed.Outcome}");
            }

            var removed = await orders.Client.RemoveOwnFilesAsync(
                new Wire.RemoveFilesRequest { Agent = Orders, Files = { new Wire.FileRef { ConversationId = "pricing", Name = name } } },
                cancellationToken: Ct);

            if (removed.Removed.Count > 0)
            {
                taken.Add($"removing {name}");
            }
        }

        var listed = (await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files;

        Assert.True(taken.Count == 0, string.Join("; ", taken));
        Assert.Equal(Billing, Assert.Single(listed, f => f.Name == "billings.md").PlacedBy);
        Assert.Equal(string.Empty, Assert.Single(listed, f => f.Name == "copied.md").PlacedBy);
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "billings.md"), Ct));
    }

    [Fact]
    public async Task Malformed_input_on_the_wire_is_refused_as_invalid_never_unknown_and_stores_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "seed.md", Text("seed"), "pricing", Ct);

        var outcomes = new List<string>();

        async Task Probe(string what, Func<Task<string>> call)
        {
            try
            {
                outcomes.Add($"{what}: answered {await call()}");
            }
            catch (RpcException failed)
            {
                outcomes.Add($"{what}: {failed.StatusCode}");
            }
        }

        await Probe("no header", async () =>
        {
            using var c = billing.Client.PlaceFile(cancellationToken: Ct);
            await c.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(Text("x")) }, Ct);
            await c.RequestStream.CompleteAsync();
            return (await c.ResponseAsync).Rejection.ToString();
        });

        await Probe("oversized piece", async () =>
        {
            using var c = billing.Client.PlaceFile(cancellationToken: Ct);
            await c.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "big.md", ConversationId = "pricing" } }, Ct);
            await c.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(new byte[FileLimits.ChunkBytes + 1]) }, Ct);
            await c.RequestStream.CompleteAsync();
            return (await c.ResponseAsync).Rejection.ToString();
        });

        await Probe("second header", async () =>
        {
            using var c = billing.Client.PlaceFile(cancellationToken: Ct);
            var header = new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "twice.md", ConversationId = "pricing" } };
            await c.RequestStream.WriteAsync(header, Ct);
            await c.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(Text("x")) }, Ct);
            await c.RequestStream.WriteAsync(header, Ct);
            await c.RequestStream.CompleteAsync();
            return (await c.ResponseAsync).Rejection.ToString();
        });

        await Probe("empty name", async () => (await FileWire.PlaceAsync(billing.Client, Billing, string.Empty, Text("x"), "pricing", Ct)).Rejection.ToString());
        await Probe("a path as a name", async () => (await FileWire.PlaceAsync(billing.Client, Billing, @"..\..\escape.md", Text("x"), "pricing", Ct)).Rejection.ToString());
        await Probe("DEL in a name", async () => (await FileWire.PlaceAsync(billing.Client, Billing, "a" + (char)0x7F + ".md", Text("x"), "pricing", Ct)).Rejection.ToString());
        await Probe("negative page size", async () => (await billing.Client.ListFilesAsync(new Wire.ListFilesRequest { Agent = Billing, PageSize = -5 }, cancellationToken: Ct)).Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files");
        await Probe("forged page token", async () => (await billing.Client.ListFilesAsync(new Wire.ListFilesRequest { Agent = Billing, PageToken = "!!not-a-token" }, cancellationToken: Ct)).Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files");
        await Probe("base64 page token of nonsense", async () => (await billing.Client.ListFilesAsync(new Wire.ListFilesRequest { Agent = Billing, PageToken = Convert.ToBase64String(Text("x")) }, cancellationToken: Ct)).Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files");
        await Probe("bad expect hash", async () => { await FetchOnlyAsync(billing.Client, "seed.md", expect: "zz"); return "streamed"; });
        await Probe("bad reported hash", async () => (await billing.Client.FetchedFileAsync(new Wire.FetchedFileRequest { Agent = Billing, ConversationId = "pricing", Name = "seed.md", Sha256 = "nothex" }, cancellationToken: Ct)).Rejection.ToString());
        await Probe("reported name that breaks a rule", async () => (await billing.Client.FetchedFileAsync(new Wire.FetchedFileRequest { Agent = Billing, ConversationId = "pricing", Name = "../x.md", Sha256 = new string('a', 64) }, cancellationToken: Ct)).Rejection.ToString());
        await Probe("removal of an empty name", async () => Assert.Single((await billing.Client.RemoveOwnFilesAsync(new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { Name = string.Empty } } }, cancellationToken: Ct)).Refused).Rejection.ToString());
        await Probe("describe a path", async () => (await broker.ConnectAsOperator().DescribeFileLocationAsync(new Wire.FileLocationRequest { Name = "../../x.md" }, cancellationToken: Ct)).Refusal.Rejection.ToString());
        await Probe("describe a bad conversation", async () => (await broker.ConnectAsOperator().DescribeFileLocationAsync(new Wire.FileLocationRequest { ConversationId = "../.." }, cancellationToken: Ct)).Refusal.Rejection.ToString());
        await Probe("person's list with a forged token", async () => (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest { PageToken = "%%%" }, cancellationToken: Ct)).Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files");

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct);

        Assert.True(
            !outcomes.Any(o => o.EndsWith(": Unknown", StringComparison.Ordinal) || o.EndsWith(": Internal", StringComparison.Ordinal)),
            string.Join('\n', outcomes));
        // A DEL in a name is no longer malformed: it is a character outside the set, so the placement is renamed and
        // stored as a_.md (SPEC.md §9.5). Everything else probed here stores nothing.
        Assert.Equal(["a_.md", "seed.md"], listed.Files.Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    private static async Task<Wire.SharedFileView> FetchOnlyAsync(Wire.Agents.AgentsClient client, string name, string? expect = null)
    {
        using var call = client.FetchFile(
            new Wire.FetchFileRequest { Agent = client == null ? Orders : AgentOf(client), Name = name, ConversationId = "pricing", ExpectSha256 = expect ?? string.Empty },
            cancellationToken: Ct);

        Wire.SharedFileView? header = null;

        await foreach (var frame in call.ResponseStream.ReadAllAsync(Ct))
        {
            header ??= frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Header ? frame.Header : null;
        }

        return header ?? throw new InvalidOperationException("no header");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Wire.Agents.AgentsClient, string> Names = new();

    private static string AgentOf(Wire.Agents.AgentsClient client) => Names.TryGetValue(client, out var name) ? name : Billing;

    private static async Task RequestAsync(AttachedClient from, string to, string conversation)
    {
        var reply = await from.Client.RequestAsync(
            new Wire.OpenRequest
            {
                From = from.Name,
                To = to,
                ResponderModel = "claude-opus-5",
                ConversationId = conversation,
                PayloadJson =
                    """{"ask":"Is tax added net or gross, and why is it so in your repository?"}"""
            },
            cancellationToken: Ct);

        Assert.True(Assert.Single(reply.Outcomes).Ok, "the fixture's Request was refused");
    }

    /// <summary>A broker over a store on disk.</summary>
    private sealed class Served : IAsyncDisposable
    {
        private readonly GrpcChannel _channel;
        private readonly List<AsyncServerStreamingCall<Wire.Event>> _streams = [];

        private Served(ParleyServer server)
        {
            Server = server;
            _channel = GrpcChannel.ForAddress(server.Addresses[0]);
            Operators = new Wire.Operators.OperatorsClient(_channel);
        }

        public ParleyServer Server { get; }

        public Wire.Operators.OperatorsClient Operators { get; }

        public static async Task<Served> StartAsync(string store, CancellationToken cancellationToken)
        {
            var runtime = BrokerRuntime.Open(
                ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

            return new Served(await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", cancellationToken));
        }

        public async Task<Wire.Agents.AgentsClient> AttachAsync(string agent, CancellationToken cancellationToken)
        {
            var client = new Wire.Agents.AgentsClient(_channel);
            var stream = client.Attach(
                new Wire.AttachRequest { Agent = agent, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: cancellationToken);

            if (!await stream.ResponseStream.MoveNext(cancellationToken))
            {
                throw new InvalidOperationException("The attach stream ended before it said anything.");
            }

            _streams.Add(stream);
            Names.AddOrUpdate(client, agent);

            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            await _channel.ShutdownAsync();
            _channel.Dispose();
            await Server.DisposeAsync();
        }
    }
}
