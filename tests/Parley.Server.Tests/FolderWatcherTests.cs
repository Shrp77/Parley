using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Changes made to the file location outside Parley: edits, renames, deletions, hand-copied files, and what changed
/// while no broker was running (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each change is made the way a person makes it</b> — writing, moving and deleting files in the folder — and each
/// assertion is on what the person's list and the fetching agent's inbox then say. Nothing reaches into the watcher.
/// </para>
/// <para>
/// The restart cases open a store on disk and serve it twice, because what they are about is what the second broker
/// finds, and an in-memory store does not survive its first.
/// </para>
/// </remarks>
public sealed class FolderWatcherTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task An_edit_in_the_folder_is_the_files_new_content_keeps_its_placer_and_tells_the_fetcher()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);

        await File.WriteAllTextAsync(PathOf(broker, "pricing", "notes.md"), "gross, as corrected", Ct);

        var edited = ContentHash.Of(Text("gross, as corrected")).Value;
        var listed = await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Any(f => f.Sha256 == edited), Within, Ct);

        Assert.Equal(Billing, Assert.Single(listed).PlacedBy);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        Assert.Equal(edited, Assert.Single(inbox.FileNotices).Sha256Now);
    }

    [Fact]
    public async Task A_burst_of_writes_comes_to_one_notice()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("v0"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);

        var path = PathOf(broker, "pricing", "notes.md");

        for (var i = 1; i <= 10; i++)
        {
            await File.WriteAllTextAsync(path, $"v{i}", Ct);
        }

        var last = ContentHash.Of(Text("v10")).Value;
        await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Any(f => f.Sha256 == last), Within, Ct);

        // Past the watcher's quiet period, so a late look would have had its chance to raise a second notice.
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        Assert.Equal(last, Assert.Single(inbox.FileNotices).Sha256Now);
    }

    [Fact]
    public async Task A_rename_in_the_folder_keeps_the_placer_and_tells_fetchers_the_old_name_is_gone()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);

        // About when the look the folder's own change asked for runs. A rename landing while a look runs used to have that
        // look, holding no report yet, remove the row as gone, and the new name was then found as the person's: Linux CI
        // met it once, and with this wait Windows met it in most runs of the class.
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        File.Move(PathOf(broker, "pricing", "notes.md"), PathOf(broker, "pricing", "renamed.md"));

        var listed = await FileWire.WaitForAsync(
            broker.ConnectAsOperator(), files => files.Any(f => f.Name == "renamed.md") && files.All(f => f.Name != "notes.md"), Within, Ct);

        Assert.Equal(Billing, Assert.Single(listed).PlacedBy);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        var notice = Assert.Single(inbox.FileNotices);
        Assert.Equal("notes.md", notice.Name);
        Assert.True(notice.Gone);
    }

    [Fact]
    public async Task A_deletion_in_the_folder_is_the_file_gone()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);

        File.Delete(PathOf(broker, "pricing", "notes.md"));

        await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Count == 0, Within, Ct);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        Assert.True(Assert.Single(inbox.FileNotices).Gone);
    }

    [Fact]
    public async Task A_file_copied_in_by_hand_is_the_persons_and_one_whose_name_breaks_a_rule_is_ignored()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "seed.md", Text("seed"), "pricing", Ct);

        await File.WriteAllTextAsync(PathOf(broker, "pricing", "setup.exe"), "not text", Ct);
        await File.WriteAllTextAsync(PathOf(broker, "pricing", "con.md"), "reserved", Ct);
        await File.WriteAllTextAsync(PathOf(broker, "pricing", "copied.md"), "by hand", Ct);

        var listed = await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Any(f => f.Name == "copied.md"), Within, Ct);

        Assert.Equal(string.Empty, Assert.Single(listed, f => f.Name == "copied.md").PlacedBy);
        Assert.DoesNotContain(listed, f => f.Name is "setup.exe" or "con.md");
    }

    [Fact]
    public async Task Changes_made_while_no_broker_ran_are_found_at_start_and_a_rename_matched_by_hash_keeps_its_placer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "parley-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "alpha.db");

        try
        {
            await using (var first = await Served.StartAsync(store, Ct))
            {
                await using var attached = await first.AttachAsync(Billing, Ct);

                await FileWire.PlaceAsync(attached.Client, Billing, "renamed-offline.md", Text("same bytes"), "pricing", Ct);
                await FileWire.PlaceAsync(attached.Client, Billing, "edited-offline.md", Text("before"), "pricing", Ct);
                await FileWire.PlaceAsync(attached.Client, Billing, "deleted-offline.md", Text("doomed"), "pricing", Ct);
            }

            var folder = Path.Combine(store + StoreFiles.Files, "pricing");

            File.Move(Path.Combine(folder, "renamed-offline.md"), Path.Combine(folder, "now-called.md"));
            await File.WriteAllTextAsync(Path.Combine(folder, "edited-offline.md"), "after", Ct);
            File.Delete(Path.Combine(folder, "deleted-offline.md"));
            await File.WriteAllTextAsync(Path.Combine(folder, "brand-new.md"), "nobody placed this", Ct);

            await using var second = await Served.StartAsync(store, Ct);

            await second.Server.Runtime.Watcher.Scanned.WaitAsync(Within, Ct);

            var listed = (await second.Operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files;

            Assert.Equal(Billing, Assert.Single(listed, f => f.Name == "now-called.md").PlacedBy);
            Assert.Equal(ContentHash.Of(Text("after")).Value, Assert.Single(listed, f => f.Name == "edited-offline.md").Sha256);
            Assert.Equal(Billing, Assert.Single(listed, f => f.Name == "edited-offline.md").PlacedBy);
            Assert.Equal(string.Empty, Assert.Single(listed, f => f.Name == "brand-new.md").PlacedBy);
            Assert.DoesNotContain(listed, f => f.Name is "renamed-offline.md" or "deleted-offline.md");
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
    public async Task A_rename_to_a_name_the_location_does_not_accept_is_the_file_gone()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);
        await FileWire.FetchAndReportAsync(orders.Client, Orders, "notes.md", "pricing", Ct);

        File.Move(PathOf(broker, "pricing", "notes.md"), PathOf(broker, "pricing", "notes.exe"));

        await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Count == 0, Within, Ct);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        var notice = Assert.Single(inbox.FileNotices);
        Assert.Equal("notes.md", notice.Name);
        Assert.True(notice.Gone);
    }

    [Fact]
    public async Task A_folder_no_conversation_owns_is_ignored()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "seed.md", Text("seed"), "pricing", Ct);

        var stranger = Path.Combine(broker.Runtime.Files.Root, "stranger");
        Directory.CreateDirectory(stranger);
        await File.WriteAllTextAsync(Path.Combine(stranger, "planted.md"), "nobody's", Ct);

        // A sentinel in a folder the location does own, written after the planted file: once it is listed, the look at
        // the planted file has had its chance too.
        await File.WriteAllTextAsync(PathOf(broker, "pricing", "sentinel.md"), "after", Ct);

        var listed = await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Any(f => f.Name == "sentinel.md"), Within, Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        listed = (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files;

        Assert.DoesNotContain(listed, f => f.Name == "planted.md");
        Assert.All(listed, f => Assert.Equal("pricing", f.ConversationId));
    }

    [Fact]
    public async Task A_file_copied_into_the_folder_of_a_conversation_whose_last_file_went_is_that_conversations()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);

        await broker.ConnectAsOperator().RemoveFilesAsync(
            new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { ConversationId = "pricing", Name = "notes.md" } } },
            cancellationToken: Ct);

        var folder = broker.Runtime.Files.FolderPath("pricing");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "copied.md"), "by hand", Ct);

        var listed = await FileWire.WaitForAsync(broker.ConnectAsOperator(), files => files.Any(f => f.Name == "copied.md"), Within, Ct);

        var copied = Assert.Single(listed);
        Assert.Equal("pricing", copied.ConversationId);
        Assert.Equal(string.Empty, copied.PlacedBy);
    }

    [Fact]
    public async Task A_rename_made_while_stopped_is_the_persons_when_several_missing_files_had_its_content()
    {
        var directory = Path.Combine(Path.GetTempPath(), "parley-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "alpha.db");

        try
        {
            await using (var first = await Served.StartAsync(store, Ct))
            {
                await using var attached = await first.AttachAsync(Billing, Ct);

                await FileWire.PlaceAsync(attached.Client, Billing, "one.md", Text("same bytes"), "pricing", Ct);
                await FileWire.PlaceAsync(attached.Client, Billing, "two.md", Text("same bytes"), "pricing", Ct);
            }

            var folder = Path.Combine(store + StoreFiles.Files, "pricing");

            File.Delete(Path.Combine(folder, "one.md"));
            File.Move(Path.Combine(folder, "two.md"), Path.Combine(folder, "which.md"));

            await using var second = await Served.StartAsync(store, Ct);

            await second.Server.Runtime.Watcher.Scanned.WaitAsync(Within, Ct);

            var listed = (await second.Operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files;

            // Either of two files could have become this one, and guessing would hand one agent's file to the other's
            // placer: it is the person's.
            Assert.Equal(string.Empty, Assert.Single(listed).PlacedBy);
            Assert.Equal("which.md", listed[0].Name);
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

    private static string PathOf(LoopbackBroker broker, string folder, string name) =>
        Path.Combine(broker.Runtime.Files.FolderPath(folder), name);

    /// <summary>A broker over a store on disk, and the clients a test needs to talk to it.</summary>
    private sealed class Served : IAsyncDisposable
    {
        private readonly GrpcChannel _channel;

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

        public async Task<Attachment> AttachAsync(string agent, CancellationToken cancellationToken)
        {
            var client = new Wire.Agents.AgentsClient(_channel);
            var stream = client.Attach(
                new Wire.AttachRequest
                {
                    Agent = agent,
                    Adapter = "test",
                    Tier = Wire.AgentTier.Pull,
                    ProtocolVersion = ProtocolVersion.Current
                },
                cancellationToken: cancellationToken);

            if (!await stream.ResponseStream.MoveNext(cancellationToken))
            {
                throw new InvalidOperationException("The attach stream ended before it said anything.");
            }

            return new Attachment(client, stream);
        }

        public async ValueTask DisposeAsync()
        {
            await _channel.ShutdownAsync();
            _channel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed class Attachment(Wire.Agents.AgentsClient client, Grpc.Core.AsyncServerStreamingCall<Wire.Event> stream) : IAsyncDisposable
    {
        public Wire.Agents.AgentsClient Client { get; } = client;

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
