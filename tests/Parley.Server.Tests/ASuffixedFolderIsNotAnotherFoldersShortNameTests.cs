using System.Text;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a conversation's suffixed folder name never reaches another folder by its 8.3 short name, that no placed name
/// becomes the left-behind mark, and that a notice's cut title is still the agent's own well-formed words.
/// </summary>
/// <remarks>
/// <b>Found by review, round 20.</b> A name such as <c>ABCDEF~2</c> is, on a volume that keeps 8.3 names, the short
/// name of the second long folder beginning <c>abcdef</c>: an agent placing into its conversation replaced another
/// conversation's file, and a destroy deleted or marked a person's own folder. A name whose path reaches anything is
/// now taken, and a folder is deleted or marked only under its own long name. The other cases held.
/// </remarks>
public sealed class ASuffixedFolderIsNotAnotherFoldersShortNameTests : IDisposable
{
    private const string Inventory = "inventory@alpha";
    private const string Billing = "billing@alpha";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-short-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task An_agent_placing_into_a_suffixed_folder_does_not_replace_another_conversations_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names are an NTFS feature");

        await using var server = await StartAsync(Path.Combine(_base, "alpha.db"));
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);
        using var inventory = await AttachAsync(agents, Inventory);
        using var billing = await AttachAsync(agents, Billing);

        // Billing's two conversations, the second of which gets the short name ABCDEF~2 on a volume that makes them.
        await PlaceAsync(agents, Billing, "notes.md", "first", "abcdef-invoices");
        await PlaceAsync(agents, Billing, "notes.md", "billing's own words", "abcdef-payments");
        var victim = Path.Combine(server.Runtime.Files.Root, "abcdef-payments");
        Assert.SkipUnless(Directory.Exists(Path.Combine(server.Runtime.Files.Root, "ABCDEF~2")), "no 8.3 names on this volume");

        // Inventory opens "abcdef", then "ABCDEF", whose folder is suffixed to ABCDEF~2.
        await PlaceAsync(agents, Inventory, "x.md", "x", "abcdef");
        var placed = await FileWire.PlaceAsync(agents, Inventory, "notes.md", "inventory's words"u8.ToArray(), "ABCDEF", Ct);

        Assert.Equal("billing's own words", await File.ReadAllTextAsync(Path.Combine(victim, "notes.md"), Ct));
        Assert.NotEqual(Wire.PlaceOutcome.Replaced, placed.Outcome);
    }

    [Fact]
    public async Task Destroying_a_suffixed_conversation_leaves_the_persons_own_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names are an NTFS feature");

        await using var server = await StartAsync(Path.Combine(_base, "alpha.db"));
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);
        using var inventory = await AttachAsync(agents, Inventory);

        // The person's own folders in the location (SPEC.md section 9.5 says they are left alone).
        Directory.CreateDirectory(Path.Combine(server.Runtime.Files.Root, "abcdef-drafts"));
        var mine = Directory.CreateDirectory(Path.Combine(server.Runtime.Files.Root, "abcdef-letters")).FullName;
        await File.WriteAllTextAsync(Path.Combine(mine, "letter.md"), "the person's own words", Ct);
        Assert.SkipUnless(Directory.Exists(Path.Combine(server.Runtime.Files.Root, "ABCDEF~2")), "no 8.3 names on this volume");

        await PlaceAsync(agents, Inventory, "x.md", "x", "abcdef");
        await PlaceAsync(agents, Inventory, "y.md", "y", "ABCDEF");

        var operators = new Wire.Operators.OperatorsClient(channel);
        var listed = await operators.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
        var listedAsAConversations = listed.Files.Any(f => f.Name == "letter.md");
        Assert.False(listedAsAConversations, "the person's own file was listed as a conversation's: " + string.Join(", ", listed.Files.Select(f => f.ConversationId + "/" + f.Name)));

        _ = await operators.DestroyAsync(new Wire.ConversationRequest { ConversationId = "ABCDEF" }, cancellationToken: Ct);

        Assert.True(File.Exists(Path.Combine(mine, "letter.md")), "destroying ABCDEF deleted the person's own folder abcdef-letters");
        Assert.False(listedAsAConversations, "the person's own file was listed as conversation ABCDEF's");
    }

    [Fact]
    public async Task A_held_destroy_of_a_suffixed_conversation_does_not_mark_the_persons_folder_for_the_next_start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names, and a delete refused by a held file, are Windows'");

        var store = Path.Combine(_base, "alpha.db");
        string mine;

        await using (var server = await StartAsync(store))
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var agents = new Wire.Agents.AgentsClient(channel);
            using var inventory = await AttachAsync(agents, Inventory);

            Directory.CreateDirectory(Path.Combine(server.Runtime.Files.Root, "abcdef-drafts"));
            mine = Directory.CreateDirectory(Path.Combine(server.Runtime.Files.Root, "abcdef-letters")).FullName;
            await File.WriteAllTextAsync(Path.Combine(mine, "letter.md"), "the person's own words", Ct);
            Assert.SkipUnless(Directory.Exists(Path.Combine(server.Runtime.Files.Root, "ABCDEF~2")), "no 8.3 names on this volume");

            await PlaceAsync(agents, Inventory, "x.md", "x", "abcdef");
            await PlaceAsync(agents, Inventory, "y.md", "y", "ABCDEF");

            // The person has their own letter open in an editor that holds it while the operator destroys ABCDEF.
            using (new FileStream(Path.Combine(mine, "letter.md"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = await new Wire.Operators.OperatorsClient(channel)
                    .DestroyAsync(new Wire.ConversationRequest { ConversationId = "ABCDEF" }, cancellationToken: Ct);
            }

            Assert.True(File.Exists(Path.Combine(mine, "letter.md")), "the held letter went at the destroy");
        }

        await using (await StartAsync(store))
        {
            Assert.True(File.Exists(Path.Combine(mine, "letter.md")), "a restart deleted the person's own folder");
        }
    }

    [Theory]
    [InlineData(".parley-destroyed")]
    [InlineData(".PARLEY-DESTROYED")]
    [InlineData(".parley-destroyed.")]
    [InlineData(".parley-destroyed ")]
    [InlineData(".parley-destroyed::$DATA")]
    [InlineData(".parley-destroyed:x.md")]
    [InlineData(".parley-destroyed/x.md")]
    [InlineData("..\\.parley-destroyed")]
    [InlineData(".parley-destroyed.md")]
    [InlineData(".parley-destroyed .md")]
    public async Task No_placed_name_becomes_the_mark(string name)
    {
        var store = Path.Combine(_base, "alpha.db");
        string folder;

        await using (var server = await StartAsync(store))
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var agents = new Wire.Agents.AgentsClient(channel);
            using var inventory = await AttachAsync(agents, Inventory);

            await PlaceAsync(agents, Inventory, "stock.md", "live", "stock");
            _ = await FileWire.PlaceAsync(agents, Inventory, name, "x"u8.ToArray(), "stock", Ct);
            folder = server.Runtime.Files.FolderPath("stock");

            Assert.False(File.Exists(Path.Combine(folder, FileLocationStore.LeftBehind)), $"{name} planted the mark");
            Assert.False(File.Exists(Path.Combine(server.Runtime.Files.Root, FileLocationStore.LeftBehind)), $"{name} escaped the folder");
        }

        await using (await StartAsync(store))
        {
            Assert.True(File.Exists(Path.Combine(folder, "stock.md")));
        }
    }

    [Theory]
    [InlineData("\U0001F600")]
    [InlineData("e\u0301")]
    [InlineData("\U0001F1EC\U0001F1E7")]
    [InlineData("\u0915\u094D\u0937")]
    [InlineData("\u2764\uFE0F")]
    public async Task A_notice_title_cut_at_200_is_the_agents_own_well_formed_one_line_text(string straddling)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@test", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        foreach (var at in new[] { 198, 199 })
        {
            var title = new string('a', at) + straddling + "tail after the cut";
            Assert.True(RoadmapText.IsValid(title, "a title", out var why), why);

            var proposed = await orders.Client.ProposeRoadmapAsync(
                new Wire.ProposeRoadmapRequest
                {
                    Agent = orders.Name,
                    Content = new Wire.RoadmapContent { Title = title, Items = { new Wire.RoadmapItemDraft { Text = "Add CSV export" } } },
                },
                cancellationToken: Ct);
            Assert.Empty(proposed.Problem);
            Assert.True((await operators.DeclineProposalAsync(new Wire.ProposalRequest { Id = proposed.ProposalId }, cancellationToken: Ct)).Done);

            var drained = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = orders.Name }, cancellationToken: Ct);
            var told = Assert.Single(drained.RoadmapNotices, n => n.ProposalId == proposed.ProposalId).Title;

            Assert.StartsWith(told, title, StringComparison.Ordinal);
            Assert.Equal(200, told.EnumerateRunes().Count());
            Assert.DoesNotContain(told.EnumerateRunes(), r => r == Rune.ReplacementChar);
            _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(told);
            Assert.True(RoadmapText.IsValid(told, "a title", out var cut), cut);
        }
    }

    private static async Task PlaceAsync(Wire.Agents.AgentsClient agents, string agent, string name, string words, string conversation)
    {
        var placed = await FileWire.PlaceAsync(agents, agent, name, System.Text.Encoding.UTF8.GetBytes(words), conversation, Ct);
        Assert.True(placed.Outcome is Wire.PlaceOutcome.Placed or Wire.PlaceOutcome.Replaced, $"{name} in {conversation}: {placed}");
    }

    private static async Task<IDisposable> AttachAsync(Wire.Agents.AgentsClient agents, string agent)
    {
        var stream = agents.Attach(
            new Wire.AttachRequest { Agent = agent, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
            cancellationToken: Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        return stream;
    }

    private static async Task<ParleyServer> StartAsync(string store)
    {
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

        return await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
    }
}
