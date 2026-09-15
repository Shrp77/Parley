using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Files crossing between agents over a real loopback socket: placing, fetching, reporting, listing, removing,
/// pruning, notices and where a file is (SPEC.md §9.5, §10.1, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each agent here is a real attached client</b>, because every file operation is refused for a name with no stream
/// open, and a test that called the service directly would skip exactly the check that says who the placer is.
/// </para>
/// <para>
/// Nothing reads the store. What these assert is what a second implementation's client would see on the wire, and
/// the location on disk where the person would look.
/// </para>
/// </remarks>
public sealed class FilesOverTheWireTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_file_placed_by_one_agent_is_fetched_byte_identical_by_another_under_the_same_hash()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var content = Text("public sealed record PriceSnapshot(decimal Net, decimal Tax);");

        var placed = await PlaceAsync(billing, "PriceSnapshot.cs.txt", content);

        Assert.Equal(Wire.FileRejection.None, placed.Rejection);
        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.Equal(ContentHash.Of(content).Value, placed.Sha256);
        Assert.Equal(content.Length, placed.SizeBytes);

        var fetched = await FetchAsync(orders, "PriceSnapshot.cs.txt");

        Assert.Null(fetched.Refusal);
        Assert.Equal(content, fetched.Bytes);
        Assert.Equal(placed.Sha256, fetched.Header!.Sha256);
        Assert.Equal(Billing, fetched.Header.PlacedBy);
    }

    [Fact]
    public async Task A_hundred_megabyte_file_crosses_whole()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var content = new byte[100 * 1024 * 1024];
        new Random(42).NextBytes(content);

        var placed = await PlaceAsync(billing, "capture.txt", content);
        var fetched = await FetchAsync(orders, "capture.txt");

        Assert.Equal(ContentHash.Of(content).Value, placed.Sha256);
        Assert.Equal(content.Length, fetched.Bytes.Length);
        Assert.Equal(ContentHash.Of(content), ContentHash.Of(fetched.Bytes));
    }

    [Fact]
    public async Task An_agent_holding_no_request_starts_a_conversation_by_placing_and_can_place_into_it_again_by_name()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var first = await PlaceAsync(billing, "notes.md", Text("one"), conversation: "tax-inclusive-pricing");
        var second = await PlaceAsync(billing, "more.md", Text("two"), conversation: "tax-inclusive-pricing");
        var unnamed = await PlaceAsync(billing, "elsewhere.md", Text("three"));

        Assert.Equal("tax-inclusive-pricing", first.ConversationId);
        Assert.Equal(Wire.PlaceOutcome.Placed, second.Outcome);
        Assert.NotEqual("tax-inclusive-pricing", unnamed.ConversationId);
        Assert.False(string.IsNullOrEmpty(unnamed.ConversationId));

        var listed = await broker.ConnectAsOperator().ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: Ct);
        var started = Assert.Single(listed.Conversations, c => c.ConversationId == "tax-inclusive-pricing");
        Assert.Contains(Billing, started.Participants);
    }

    [Fact]
    public async Task An_agent_holding_requests_in_several_conversations_must_name_one()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await RequestAsync(orders, Billing, "pricing", "net or gross?");
        await RequestAsync(orders, Billing, "refunds", "rounded how?");

        var refused = await PlaceAsync(billing, "notes.md", Text("x"));

        Assert.Equal(Wire.FileRejection.ConversationNotAssignable, refused.Rejection);
        Assert.Equal(["pricing", "refunds"], refused.AssignableConversations.Order(StringComparer.Ordinal));

        var named = await PlaceAsync(billing, "notes.md", Text("x"), conversation: "refunds");
        Assert.Equal(Wire.PlaceOutcome.Placed, named.Outcome);
    }

    [Theory]
    [InlineData("PriceSnapshot.cs", Wire.FileRejection.ExtensionNotAllowed)]
    [InlineData("../escape.txt", Wire.FileRejection.BadName)]
    [InlineData("con.md", Wire.FileRejection.BadName)]
    public async Task A_name_that_breaks_a_rule_is_refused_before_anything_is_stored(string name, Wire.FileRejection rejection)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var refused = await PlaceAsync(billing, name, Text("x"));

        Assert.Equal(rejection, refused.Rejection);
        Assert.False(string.IsNullOrEmpty(refused.Detail));

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct);
        Assert.Equal(0, listed.TotalFiles);
    }

    [Fact]
    public async Task An_extension_refusal_says_how_to_place_the_file_anyway()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var refused = await PlaceAsync(billing, "PriceSnapshot.cs", Text("x"));

        Assert.Contains("PriceSnapshot.cs.txt", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_agent_placing_the_same_name_is_refused_naming_the_placer_and_the_file_is_unchanged()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var placed = await PlaceAsync(billing, "notes.md", Text("net"), conversation: "pricing");
        await RequestAsync(billing, Orders, "pricing", "does this match yours?");

        var refused = await PlaceAsync(orders, "NOTES.md", Text("gross"));

        Assert.Equal(Wire.FileRejection.NamePlacedByAnother, refused.Rejection);
        Assert.Equal(Billing, refused.PlacedBy);
        Assert.Equal(placed.Sha256, (await FetchAsync(orders, "notes.md")).Header!.Sha256);
    }

    [Fact]
    public async Task Its_placer_replaces_it_and_identical_content_is_already_current()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var first = await PlaceAsync(billing, "notes.md", Text("net"), conversation: "pricing");
        var replaced = await PlaceAsync(billing, "notes.md", Text("gross"), conversation: "pricing");
        var again = await PlaceAsync(billing, "notes.md", Text("gross"), conversation: "pricing");

        Assert.Equal(Wire.PlaceOutcome.Replaced, replaced.Outcome);
        Assert.Equal(first.Sha256, replaced.PreviousSha256);
        Assert.Equal(Wire.PlaceOutcome.AlreadyCurrent, again.Outcome);
    }

    [Fact]
    public async Task Two_agents_placing_one_new_name_at_once_have_exactly_one_winner()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await PlaceAsync(billing, "seed.md", Text("seed"), conversation: "pricing");
        await PlaceAsync(orders, "seed-too.md", Text("seed"), conversation: "pricing-too");

        // Each agent holds nothing, so each names the conversation it is part of: both must be participants of one
        // conversation for the race to be about one name.
        await RequestAsync(billing, Orders, "pricing", "join me here");

        for (var round = 0; round < 5; round++)
        {
            var name = $"race-{round}.md";

            var results = await Task.WhenAll(
                PlaceAsync(billing, name, Text("billing's"), conversation: "pricing"),
                PlaceAsync(orders, name, Text("orders'"), conversation: "pricing"));

            Assert.Single(results, r => r.Outcome == Wire.PlaceOutcome.Placed);
            Assert.Single(results, r => r.Rejection == Wire.FileRejection.NamePlacedByAnother);
        }
    }

    [Fact]
    public async Task A_bare_name_held_by_several_conversations_is_refused_listing_them_and_naming_one_fetches_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await PlaceAsync(billing, "schema.md", Text("pricing"), conversation: "pricing");
        await PlaceAsync(billing, "schema.md", Text("refunds"), conversation: "refunds");

        var ambiguous = await FetchAsync(orders, "schema.md");

        Assert.Equal(Wire.FileRejection.AmbiguousName, ambiguous.Refusal!.Rejection);
        Assert.Equal(["pricing", "refunds"], ambiguous.Refusal.Conversations);
        Assert.Empty(ambiguous.Bytes);

        Assert.Equal(Text("refunds"), (await FetchAsync(orders, "schema.md", conversation: "refunds")).Bytes);
    }

    [Fact]
    public async Task A_fetch_expecting_a_hash_the_file_no_longer_has_is_refused_giving_the_hash_it_has()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var first = await PlaceAsync(billing, "notes.md", Text("net"), conversation: "pricing");
        var second = await PlaceAsync(billing, "notes.md", Text("gross"), conversation: "pricing");

        var refused = await FetchAsync(orders, "notes.md", expect: first.Sha256);

        Assert.Equal(Wire.FileRejection.Changed, refused.Refusal!.Rejection);
        Assert.Equal(second.Sha256, refused.Refusal.Sha256Now);
        Assert.Empty(refused.Bytes);
    }

    [Fact]
    public async Task A_fetch_of_nothing_is_refused_saying_so()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        Assert.Equal(Wire.FileRejection.NoSuchFile, (await FetchAsync(orders, "absent.md")).Refusal!.Rejection);
    }

    [Fact]
    public async Task A_report_of_a_fetch_the_broker_did_not_stream_is_refused_and_one_it_did_is_recorded_once()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var placed = await PlaceAsync(billing, "notes.md", Text("net"), conversation: "pricing");

        var unasked = await ReportAsync(orders, placed);
        Assert.Equal(Wire.FileRejection.NotFetched, unasked.Rejection);

        await FetchAsync(orders, "notes.md");

        var wrongHash = await orders.Client.FetchedFileAsync(
            new Wire.FetchedFileRequest { Agent = Orders, ConversationId = "pricing", Name = "notes.md", Sha256 = new string('0', 64) },
            cancellationToken: Ct);
        Assert.Equal(Wire.FileRejection.NotFetched, wrongHash.Rejection);

        Assert.True((await ReportAsync(orders, placed)).Ok);
        Assert.Equal(Wire.FileRejection.NotFetched, (await ReportAsync(orders, placed)).Rejection);
    }

    [Fact]
    public async Task An_agent_that_reported_a_fetch_finds_one_notice_when_the_file_changes_and_nothing_wakes()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var placed = await PlaceAsync(billing, "notes.md", Text("net"), conversation: "pricing");
        await FetchAsync(orders, "notes.md");
        await ReportAsync(orders, placed);

        await PlaceAsync(billing, "notes.md", Text("gross"), conversation: "pricing");
        var latest = await PlaceAsync(billing, "notes.md", Text("gross, rounded"), conversation: "pricing");

        var waiting = await orders.Client.WaitingAsync(new Wire.WaitingRequest { Agent = Orders }, cancellationToken: Ct);
        Assert.Equal(1, waiting.FileNoticesWaiting);
        Assert.Equal(0, waiting.MessagesWaiting);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders, Limit = 1 }, cancellationToken: Ct);

        var notice = Assert.Single(inbox.FileNotices);
        Assert.Equal(placed.Sha256, notice.Sha256Had);
        Assert.Equal(latest.Sha256, notice.Sha256Now);
        Assert.False(notice.Gone);
        Assert.Equal(0, inbox.FileNoticesWaiting);
        Assert.Empty(inbox.Messages);
    }

    [Fact]
    public async Task Removing_a_fetched_file_leaves_a_gone_notice()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var placed = await PlaceAsync(billing, "notes.md", Text("net"), conversation: "pricing");
        await FetchAsync(orders, "notes.md");
        await ReportAsync(orders, placed);

        var removed = await billing.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { Name = "notes.md" } } },
            cancellationToken: Ct);

        Assert.Single(removed.Removed);

        var inbox = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);
        Assert.True(Assert.Single(inbox.FileNotices).Gone);
    }

    [Fact]
    public async Task An_agent_removes_only_what_it_placed_and_the_person_removes_anything()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await PlaceAsync(billing, "billings.md", Text("b"), conversation: "pricing");
        await PlaceAsync(orders, "orders.md", Text("o"), conversation: "refunds");

        var mixed = await orders.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest
            {
                Agent = Orders,
                Files = { new Wire.FileRef { Name = "orders.md" }, new Wire.FileRef { Name = "billings.md" } }
            },
            cancellationToken: Ct);

        Assert.Equal("orders.md", Assert.Single(mixed.Removed).Name);
        var refused = Assert.Single(mixed.Refused);
        Assert.Equal(Wire.FileRejection.NamePlacedByAnother, refused.Rejection);
        Assert.Equal(Billing, refused.PlacedBy);

        var byPerson = await broker.ConnectAsOperator().RemoveFilesAsync(
            new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { Name = "billings.md" }, new Wire.FileRef { Name = "absent.md" } } },
            cancellationToken: Ct);

        Assert.Equal("billings.md", Assert.Single(byPerson.Removed).Name);
        Assert.Equal(Wire.FileRejection.NoSuchFile, Assert.Single(byPerson.Refused).Rejection);
    }

    [Fact]
    public async Task A_removal_naming_more_than_two_hundred_files_is_refused_whole()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await PlaceAsync(billing, "keep.md", Text("k"), conversation: "pricing");

        var request = new Wire.RemoveFilesRequest { Agent = Billing };
        request.Files.Add(new Wire.FileRef { Name = "keep.md" });
        request.Files.AddRange(Enumerable.Range(0, FileLimits.NamesPerRemoval).Select(i => new Wire.FileRef { Name = $"n{i}.md" }));

        var refused = await billing.Client.RemoveOwnFilesAsync(request, cancellationToken: Ct);

        Assert.Equal(Wire.FileRejection.TooManyNames, refused.Rejection);
        Assert.Empty(refused.Removed);
        Assert.Equal(1, (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);
    }

    [Fact]
    public async Task The_list_is_paged_and_only_the_person_is_given_placement_counts()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        for (var i = 0; i < 60; i++)
        {
            await PlaceAsync(billing, $"file-{i:D2}.md", Text($"{i}"), conversation: "pricing");
        }

        var agentPage = await billing.Client.ListFilesAsync(new Wire.ListFilesRequest { Agent = Billing }, cancellationToken: Ct);
        var operatorPage = await broker.ConnectAsOperator().ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = 500 }, cancellationToken: Ct);

        Assert.Equal(FileLimits.DefaultPageSize, agentPage.Files.Count);
        Assert.Equal(60, agentPage.TotalFiles);
        Assert.False(string.IsNullOrEmpty(agentPage.NextPageToken));
        Assert.Empty(agentPage.Placements);

        Assert.Equal(60, operatorPage.Files.Count);
        Assert.Equal(60, Assert.Single(operatorPage.Placements).Placements);
    }

    [Fact]
    public async Task A_prune_empties_the_location_and_frees_the_space()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        var a = await PlaceAsync(billing, "a.md", Text("aaaa"), conversation: "pricing");
        await PlaceAsync(billing, "b.md", Text("bb"), conversation: "refunds");

        var pruned = await broker.ConnectAsOperator().PruneFilesAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.Equal(2, pruned.FilesRemoved);
        Assert.Equal(6, pruned.BytesFreed);
        Assert.Empty(Directory.EnumerateFiles(broker.Runtime.Files.Root, "*", SearchOption.AllDirectories));
        Assert.NotEqual(string.Empty, a.ConversationId);
    }

    [Fact]
    public async Task Destroying_a_conversation_deletes_its_folder_and_leaves_every_other()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await PlaceAsync(billing, "a.md", Text("a"), conversation: "pricing");
        await PlaceAsync(billing, "b.md", Text("b"), conversation: "refunds");

        await broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct);

        Assert.False(Directory.Exists(broker.Runtime.Files.FolderPath("pricing")));
        Assert.True(File.Exists(Path.Combine(broker.Runtime.Files.FolderPath("refunds"), "b.md")));
    }

    [Fact]
    public async Task Purging_an_agent_deletes_the_folders_of_the_conversations_it_took_part_in()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        await using (var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct))
        {
            await PlaceAsync(inventory, "stock.md", Text("s"), conversation: "stock");
        }

        await broker.WaitUntilDetachedAsync(Inventory, TimeSpan.FromSeconds(10));

        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await PlaceAsync(billing, "b.md", Text("b"), conversation: "refunds");

        await broker.ConnectAsOperator().PurgeAsync(new Wire.AgentRequest { Agent = Inventory }, cancellationToken: Ct);

        Assert.False(Directory.Exists(broker.Runtime.Files.FolderPath("stock")));
        Assert.True(Directory.Exists(broker.Runtime.Files.FolderPath("refunds")));
    }

    [Fact]
    public async Task Where_a_file_is_names_this_machine_and_its_path_and_an_ambiguous_name_lists_its_conversations()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await PlaceAsync(billing, "schema.md", Text("p"), conversation: "pricing");
        await PlaceAsync(billing, "schema.md", Text("r"), conversation: "refunds");
        await PlaceAsync(billing, "only.md", Text("o"), conversation: "refunds");

        var operators = broker.ConnectAsOperator();

        var file = await operators.DescribeFileLocationAsync(new Wire.FileLocationRequest { Name = "only.md" }, cancellationToken: Ct);
        Assert.True(file.Ok);
        Assert.Equal(MachineName.Current, file.Machine);
        Assert.True(File.Exists(file.Path));

        var folder = await operators.DescribeFileLocationAsync(new Wire.FileLocationRequest { ConversationId = "refunds" }, cancellationToken: Ct);
        Assert.True(Directory.Exists(folder.Path));

        var ambiguous = await operators.DescribeFileLocationAsync(new Wire.FileLocationRequest { Name = "schema.md" }, cancellationToken: Ct);
        Assert.False(ambiguous.Ok);
        Assert.Equal(["pricing", "refunds"], ambiguous.Refusal.Conversations);
    }

    [Fact]
    public async Task A_name_with_no_stream_open_can_do_nothing_with_files()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var client = broker.Connect();

        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListFilesAsync(new Wire.ListFilesRequest { Agent = Orders }, cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
    }

    private static async Task<Wire.PlaceFileReply> PlaceAsync(
        AttachedClient agent, string name, byte[] content, string? conversation = null)
    {
        using var call = agent.Client.PlaceFile(cancellationToken: Ct);

        try
        {
            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame
                {
                    Header = new Wire.PlaceFileHeader { Agent = agent.Name, Name = name, ConversationId = conversation ?? string.Empty }
                },
                Ct);

            for (var offset = 0; offset < content.Length; offset += FileLimits.ChunkBytes)
            {
                await call.RequestStream.WriteAsync(
                    new Wire.PlaceFileFrame
                    {
                        Chunk = ByteString.CopyFrom(content, offset, Math.Min(FileLimits.ChunkBytes, content.Length - offset))
                    },
                    Ct);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (Exception closed) when (closed is RpcException or InvalidOperationException)
        {
            // The broker answered before reading every piece: a refusal that did not need the bytes.
        }

        return await call.ResponseAsync;
    }

    private static async Task<(Wire.SharedFileView? Header, byte[] Bytes, Wire.FileRefusal? Refusal)> FetchAsync(
        AttachedClient agent, string name, string? conversation = null, string? expect = null)
    {
        using var call = agent.Client.FetchFile(
            new Wire.FetchFileRequest
            {
                Agent = agent.Name,
                Name = name,
                ConversationId = conversation ?? string.Empty,
                ExpectSha256 = expect ?? string.Empty
            },
            cancellationToken: Ct);

        Wire.SharedFileView? header = null;
        Wire.FileRefusal? refusal = null;
        using var bytes = new MemoryStream();

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

        return (header, refusal is null ? bytes.ToArray() : [], refusal);
    }

    private static async Task<Wire.FetchedFileReply> ReportAsync(AttachedClient agent, Wire.PlaceFileReply placed) =>
        await agent.Client.FetchedFileAsync(
            new Wire.FetchedFileRequest
            {
                Agent = agent.Name,
                ConversationId = placed.ConversationId,
                Name = placed.Name,
                Sha256 = placed.Sha256
            },
            cancellationToken: Ct);

    private static async Task RequestAsync(AttachedClient from, string to, string conversation, string question)
    {
        var reply = await from.Client.RequestAsync(
            new Wire.OpenRequest
            {
                From = from.Name,
                To = to,
                ResponderModel = "claude-opus-5",
                ConversationId = conversation,
                PayloadJson =
                    $$"""{"intent":"deciding where tax is added","items":[{"id":"q1","kind":"question","text":"{{question}}"}],"done_when":"you have said which, and why it is so in your repository"}"""
            },
            cancellationToken: Ct);

        Assert.True(Assert.Single(reply.Outcomes).Ok, $"the fixture's Request into '{conversation}' was refused");
    }
}
