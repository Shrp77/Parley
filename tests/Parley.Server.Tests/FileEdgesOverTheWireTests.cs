using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The edges of files crossing the wire: nothing in a file, a stream that breaks, a report nobody can stand behind, a
/// download given up part way, and what a location larger than one call looks like from both sides (SPEC.md §9.5,
/// §10.1, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <see cref="FilesOverTheWireTests"/> rather than in it</b>, which holds each operation's ordinary case;
/// these are the cases where an implementation that handled the ordinary one would still be wrong.
/// </para>
/// <para>
/// Each refusal is checked by what is left afterwards — in the list, in the inbox, on the node — as well as by what was
/// said, because a refusal that left a file behind is the failure worth finding.
/// </para>
/// </remarks>
[Integration]
public sealed class FileEdgesOverTheWireTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_file_with_nothing_in_it_is_placed_listed_and_fetched_under_the_hash_of_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "empty.md", [], "pricing", Ct);

        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.Equal(0, placed.SizeBytes);
        Assert.Equal(ContentHash.Of(Array.Empty<byte>()).Value, placed.Sha256);

        var fetched = await FileWire.FetchAndReportAsync(orders.Client, Orders, "empty.md", "pricing", Ct);

        Assert.Equal(placed.Sha256, fetched.Sha256);
        Assert.Equal(0, fetched.SizeBytes);
    }

    [Fact]
    public async Task A_placement_into_a_closed_conversation_is_refused_and_leaves_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await RequestAsync(orders, Billing, "pricing");

        var closed = await broker.ConnectAsOperator().ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = "pricing", Reason = "settled on a call" }, cancellationToken: Ct);
        Assert.True(closed.Changed);

        var refused = await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);

        Assert.NotEqual(Wire.FileRejection.None, refused.Rejection);
        Assert.False(string.IsNullOrEmpty(refused.Detail));
        Assert.Equal(0, (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);
    }

    [Fact]
    public async Task A_placement_whose_stream_breaks_leaves_nothing_and_its_name_free()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        using (var breaking = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var call = billing.Client.PlaceFile(cancellationToken: breaking.Token);

            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "notes.md", ConversationId = "pricing" } },
                Ct);
            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame { Chunk = Google.Protobuf.ByteString.CopyFrom(new byte[FileLimits.ChunkBytes]) }, Ct);

            await breaking.CancelAsync();
            call.Dispose();
        }

        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("whole"), "pricing", Ct).WaitAsync(Patience, Ct);

        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct);
        Assert.Equal(5, Assert.Single(listed.Files).SizeBytes);
    }

    [Fact]
    public async Task A_name_too_long_for_its_conversation_is_refused_saying_how_long_a_name_it_allows()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "seed.md", Text("seed"), "pricing", Ct);

        var refused = await FileWire.PlaceAsync(billing.Client, Billing, new string('a', 250) + ".md", Text("x"), "pricing", Ct);

        Assert.Equal(Wire.PlaceOutcome.DoesNotFit, refused.Outcome);
        Assert.Equal(SharedFileName.MaxPathCharacters - "pricing".Length - 1, refused.CharactersLeft);
        Assert.Equal(1, (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);
    }

    [Fact]
    public async Task A_report_arriving_after_the_agents_last_session_ended_is_refused()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);

        Wire.SharedFileView streamed;

        await using (var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            streamed = await FetchOnlyAsync(orders, "notes.md", "pricing");
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);

        await using var returned = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var reported = await ReportAsync(returned, streamed);

        Assert.False(reported.Ok);
        Assert.Equal(Wire.FileRejection.NotFetched, reported.Rejection);
    }

    [Fact]
    public async Task Two_fetches_with_a_removal_and_a_new_placement_between_are_each_reportable_with_the_hash_each_streamed()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);
        var first = await FetchOnlyAsync(orders, "notes.md", "pricing");

        // A placed file is never changed; the name is free again once its file is removed.
        _ = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "notes.md" } } },
            cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("gross"), "pricing", Ct);
        var second = await FetchOnlyAsync(orders, "notes.md", "pricing");

        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.True((await ReportAsync(orders, first)).Ok);
        Assert.True((await ReportAsync(orders, second)).Ok);
    }

    [Fact]
    public async Task A_download_given_up_part_way_releases_its_hold_so_a_removal_goes_ahead()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        // Large enough that the broker is still writing, held back by the transport's flow control, when the reader
        // stops: a file that fitted in the buffers would be sent whole and release its hold whatever the code did.
        var content = new byte[64 * 1024 * 1024];
        new Random(15).NextBytes(content);

        await FileWire.PlaceAsync(billing.Client, Billing, "capture.txt", content, "pricing", Ct);

        using (var abandoning = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var call = orders.Client.FetchFile(
                new Wire.FetchFileRequest { Agent = Orders, Name = "capture.txt", ConversationId = "pricing" },
                cancellationToken: abandoning.Token);

            Assert.True(await call.ResponseStream.MoveNext(Ct));
            Assert.Equal(Wire.FetchFileFrame.FrameOneofCase.Header, call.ResponseStream.Current.FrameCase);
            Assert.True(await call.ResponseStream.MoveNext(Ct));

            await abandoning.CancelAsync();
            call.Dispose();
        }

        var removed = await billing.Client.RemoveOwnFilesAsync(
                new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "capture.txt" } } },
                cancellationToken: Ct)
            .ResponseAsync.WaitAsync(Patience, Ct);

        Assert.Single(removed.Removed);
    }

    [Fact]
    public async Task More_than_two_hundred_notices_come_back_over_two_checks_oldest_first_and_no_removal_starts_anything()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var names = Enumerable.Range(0, 250).Select(i => "f" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".md").ToList();

        foreach (var name in names)
        {
            await FileWire.PlaceAsync(billing.Client, Billing, name, Text("v1"), "pricing", Ct);
        }

        await using (var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            foreach (var name in names)
            {
                await FileWire.FetchAndReportAsync(orders.Client, Orders, name, "pricing", Ct);
            }
        }

        await broker.WaitUntilDetachedAsync(Orders, Patience);

        foreach (var name in names)
        {
            var removed = await billing.Client.RemoveOwnFilesAsync(
                new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = name } } },
                cancellationToken: Ct);
            Assert.Single(removed.Removed);
        }

        // A file's going is told at the next inbox check and asks for nothing: an agent that is not running is not started.
        Assert.Equal(0, await node.CountCommandsAsync(TimeSpan.FromMilliseconds(500)));

        await using var returned = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        // A limit bounds messages, never notices.
        var first = await returned.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders, Limit = 1 }, cancellationToken: Ct);

        Assert.Equal(FileLimits.NoticesPerCollection, first.FileNotices.Count);
        Assert.Equal(50, first.FileNoticesWaiting);
        Assert.Equal(first.FileNotices.Select(n => n.GoneUtc).Order(StringComparer.Ordinal), first.FileNotices.Select(n => n.GoneUtc));

        var second = await returned.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.Equal(50, second.FileNotices.Count);
        Assert.Equal(0, second.FileNoticesWaiting);
        Assert.Equal(250, first.FileNotices.Concat(second.FileNotices).Select(n => n.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_location_of_two_hundred_and_fifty_files_is_reached_whole_on_both_services_each_page_totalling_its_own_conversations()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        for (var i = 0; i < 250; i++)
        {
            var conversation = i < 150 ? "pricing" : "refunds";
            await FileWire.PlaceAsync(billing.Client, Billing, "f" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".md", Text("x"), conversation, Ct);
        }

        var operators = broker.ConnectAsOperator();
        var byPerson = new List<Wire.SharedFileView>();
        string? next = null;

        do
        {
            var page = await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize, PageToken = next ?? string.Empty }, cancellationToken: Ct);

            Assert.Equal(250, page.TotalFiles);
            Assert.Equal(
                page.Files.Select(f => f.ConversationId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                page.Totals.Select(t => t.ConversationId).Order(StringComparer.Ordinal));
            Assert.All(page.Totals, t => Assert.Equal(t.ConversationId == "pricing" ? 150 : 100, t.Files));

            byPerson.AddRange(page.Files);
            next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (next is not null);

        Assert.Equal(250, byPerson.Select(f => f.ConversationId + "/" + f.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(250, byPerson.Count);

        var byAgent = new List<Wire.SharedFileView>();
        next = null;

        do
        {
            var page = await billing.Client.ListFilesAsync(
                new Wire.ListFilesRequest { Agent = Billing, PageToken = next ?? string.Empty }, cancellationToken: Ct);

            Assert.True(page.Files.Count <= FileLimits.DefaultPageSize);
            Assert.Equal(250, page.TotalFiles);

            byAgent.AddRange(page.Files);
            next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (next is not null);

        Assert.Equal(250, byAgent.Select(f => f.ConversationId + "/" + f.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_persons_removal_refuses_more_than_two_hundred_names_whole_and_lists_the_conversations_of_an_ambiguous_one()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "schema.md", Text("p"), "pricing", Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "schema.md", Text("r"), "refunds", Ct);

        var operators = broker.ConnectAsOperator();

        var tooMany = new Wire.RemoveFilesRequest();
        tooMany.Files.Add(new Wire.FileRef { ConversationId = "pricing", Name = "schema.md" });
        tooMany.Files.AddRange(Enumerable.Range(0, FileLimits.NamesPerRemoval).Select(i => new Wire.FileRef { Name = $"n{i}.md" }));

        var refusedWhole = await operators.RemoveFilesAsync(tooMany, cancellationToken: Ct);

        Assert.Equal(Wire.FileRejection.TooManyNames, refusedWhole.Rejection);
        Assert.Empty(refusedWhole.Removed);

        var ambiguous = await operators.RemoveFilesAsync(
            new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { Name = "schema.md" } } }, cancellationToken: Ct);

        var refused = Assert.Single(ambiguous.Refused);
        Assert.Equal(Wire.FileRejection.AmbiguousName, refused.Rejection);
        Assert.Equal(["pricing", "refunds"], refused.Conversations);
        Assert.Equal(2, (await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);
    }

    [Fact]
    public async Task Closing_and_pruning_messages_leave_files_and_pruning_files_leaves_the_conversation_and_its_messages()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await RequestAsync(orders, Billing, "pricing");
        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);

        var operators = broker.ConnectAsOperator();

        Assert.True((await operators.ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = "pricing", Reason = "settled on a call" }, cancellationToken: Ct)).Changed);

        // The retention policy decides what this removes, and a fresh store holds nothing old enough; what is checked
        // is that applying it is an operation on messages and never on the file location.
        await operators.PruneAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.Equal(1, (await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);

        var pruned = await operators.PruneFilesAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.Equal(1, pruned.FilesRemoved);

        var conversations = await operators.ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: Ct);
        var pricing = Assert.Single(conversations.Conversations, c => c.ConversationId == "pricing");
        Assert.Contains(Orders, pricing.Participants);
        Assert.Contains(Billing, pricing.Participants);

        var inbox = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);
        Assert.NotEmpty(inbox.Messages);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("a..b")]
    [InlineData("pricing/refunds")]
    public async Task A_conversation_id_the_store_would_refuse_is_refused_as_a_sentence_and_stores_nothing(string conversation)
    {
        // The adapter refuses these ids before sending, so no agent reaching the broker through it ever sends one;
        // over the wire, the store's CHECK must not throw and leave the agent told only that the handler had failed.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var refused = await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), conversation, Ct);

        Assert.Equal(Wire.FileRejection.ConversationNotAssignable, refused.Rejection);
        Assert.Contains("'..'", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(0, (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);
        Assert.Empty(Directory.EnumerateFileSystemEntries(broker.Runtime.Files.Root));
    }

    [Fact]
    public async Task A_conversation_named_with_a_single_dot_gets_a_folder_inside_the_location()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        Assert.Equal(Wire.PlaceOutcome.Placed, (await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), ".", Ct)).Outcome);

        var listed = Assert.Single((await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
        var root = Path.GetFullPath(broker.Runtime.Files.Root).TrimEnd(Path.DirectorySeparatorChar);
        var folder = Path.GetFullPath(Path.Combine(root, listed.Folder));

        Assert.NotEqual(".", listed.Folder);
        Assert.Equal(root, Path.GetDirectoryName(folder), StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(folder, "notes.md")));
    }

    [Theory]
    [InlineData("COM", 0xB9, ".md")]
    [InlineData("LPT", 0xB2, ".txt")]
    public async Task A_name_some_windows_reserve_with_a_superscript_digit_is_renamed_to_one_nothing_reserves(string stem, int superscript, string extension)
    {
        // Older Windows reserves COM¹ to LPT³ as devices; Windows 11 and every other platform write them as files. A
        // superscript is outside the set, so the name becomes COM_.md, which no
        // Windows reserves, and it must land as exactly the bytes sent.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var name = stem + (char)superscript + extension;
        var content = Text("device probe");

        var placed = await FileWire.PlaceAsync(billing.Client, Billing, name, content, "pricing", Ct);
        var safe = stem + "_" + extension;

        Assert.Equal(Wire.FileRejection.None, placed.Rejection);
        Assert.Equal(safe, placed.Name);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), safe), Ct));
        Assert.Equal(placed.Sha256, (await FileWire.FetchAndReportAsync(orders.Client, Orders, safe, "pricing", Ct)).Sha256);
    }

    [Fact]
    public async Task A_prune_racing_placements_leaves_the_disk_and_the_list_agreeing()
    {
        // Thirty rounds of five placements and a prune at once. What must hold afterwards
        // is not who won but that no file is on disk unlisted — counted as put there by hand, though Parley wrote it —
        // and no row names a file that is not there.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        for (var round = 0; round < 30; round++)
        {
            var work = Enumerable.Range(0, 5)
                .Select(i => Swallow(FileWire.PlaceAsync(billing.Client, Billing, $"r{round}-{i}.md", Text($"{round}-{i}"), "pricing", Ct)))
                .Append(Swallow(operators.PruneFilesAsync(new Wire.Empty(), cancellationToken: Ct).ResponseAsync));

            await Task.WhenAll(work);
        }

        await AssertDiskAndListAgreeAsync(broker);
    }

    [Fact]
    public async Task Destroying_a_conversation_racing_placements_into_it_leaves_the_disk_and_the_list_agreeing()
    {
        // Twenty conversations, each destroyed while three more files are placed into it.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        for (var round = 0; round < 20; round++)
        {
            var conversation = $"c{round}";

            await FileWire.PlaceAsync(billing.Client, Billing, "seed.md", Text("seed"), conversation, Ct);

            var work = Enumerable.Range(0, 3)
                .Select(i => Swallow(FileWire.PlaceAsync(billing.Client, Billing, $"more-{i}.md", Text($"{i}"), conversation, Ct)))
                .Append(Swallow(operators.DestroyAsync(new Wire.ConversationRequest { ConversationId = conversation }, cancellationToken: Ct).ResponseAsync));

            await Task.WhenAll(work);
        }

        await AssertDiskAndListAgreeAsync(broker);
    }

    private static async Task Swallow(Task work)
    {
        try
        {
            await work;
        }
        catch (RpcException)
        {
            // A refusal or an abort is an allowed answer to a race; what is checked is the state afterwards.
        }
    }

    private static async Task AssertDiskAndListAgreeAsync(LoopbackBroker broker)
    {
        var operators = broker.ConnectAsOperator();
        var all = new List<Wire.SharedFileView>();
        var next = string.Empty;

        do
        {
            var page = await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize, PageToken = next }, cancellationToken: Ct);

            all.AddRange(page.Files);
            next = page.NextPageToken;
        }
        while (next.Length > 0);

        var onList = all
            .Select(f => Path.GetFullPath(Path.Combine(broker.Runtime.Files.FolderPath(f.Folder), f.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onDisk = Directory.Exists(broker.Runtime.Files.Root)
            ? Directory.EnumerateFiles(broker.Runtime.Files.Root, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        Assert.True(
            onList.SetEquals(onDisk),
            $"listed and not on disk: [{string.Join(", ", onList.Except(onDisk))}]; on disk and not listed: [{string.Join(", ", onDisk.Except(onList))}]");

        Assert.All(all, f => Assert.True(f.PlacedBy == Billing, $"{f.ConversationId}/{f.Name} is placed by '{f.PlacedBy}'"));
    }

    private static async Task<Wire.SharedFileView> FetchOnlyAsync(AttachedClient agent, string name, string conversation)
    {
        using var call = agent.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = agent.Name, Name = name, ConversationId = conversation },
            cancellationToken: Ct);

        Wire.SharedFileView? header = null;

        await foreach (var frame in call.ResponseStream.ReadAllAsync(Ct))
        {
            Assert.NotEqual(Wire.FetchFileFrame.FrameOneofCase.Refusal, frame.FrameCase);
            header ??= frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Header ? frame.Header : null;
        }

        return header ?? throw new InvalidOperationException("The fixture's fetch sent no header.");
    }

    private static async Task<Wire.FetchedFileReply> ReportAsync(AttachedClient agent, Wire.SharedFileView streamed) =>
        await agent.Client.FetchedFileAsync(
            new Wire.FetchedFileRequest
            {
                Agent = agent.Name,
                ConversationId = streamed.ConversationId,
                Name = streamed.Name,
                Sha256 = streamed.Sha256
            },
            cancellationToken: Ct);

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

        Assert.True(Assert.Single(reply.Outcomes).Ok, $"the fixture's Request into '{conversation}' was refused");
    }
}
