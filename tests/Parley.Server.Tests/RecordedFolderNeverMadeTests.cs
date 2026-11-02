using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a folder recorded but never made cannot later write into another folder by its short name, and that a file or
/// a junction named like a conversation is passed over and left as it is.
/// </summary>
/// <remarks>
/// <b>Found by review, round 21.</b> A placement refused for its length still recorded its folder, made nothing, and on
/// a volume that keeps 8.3 names a later folder took the name as its short name: the next placement wrote one agent's
/// file into another conversation's folder, listed there as the person's. A write now goes only into the folder
/// itself. The file and junction cases held.
/// </remarks>
[Integration]
public sealed class RecordedFolderNeverMadeTests : IDisposable
{
    private const string Inventory = "inventory@alpha";
    private const string Billing = "billing@alpha";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-r21-").FullName;

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
    public async Task A_folder_recorded_by_a_refused_placement_is_not_later_another_folders_short_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names are an NTFS feature");

        await using var server = await StartAsync(Path.Combine(_base, "alpha.db"));
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);
        using var inventory = await AttachAsync(agents, Inventory);
        using var billing = await AttachAsync(agents, Billing);
        var root = server.Runtime.Files.Root;

        // Inventory takes "abcdef", then asks ABCDEF for a name too long for a suffixed folder: refused, but the
        // folder ABCDEF~2 is recorded and never made.
        await PlaceAsync(agents, Inventory, "x.md", "x", "abcdef");
        var asked = await agents.RequestAsync(
            new Wire.OpenRequest
            {
                From = Inventory,
                To = Billing,
                ResponderModel = "claude-opus-5",
                ConversationId = "ABCDEF",
                PayloadJson = """{"ask":"Is tax added net or gross?"}""",
            },
            cancellationToken: Ct);
        Assert.True(Assert.Single(asked.Outcomes).Ok, "the Request into ABCDEF was refused");
        var tooLong = new string('a', 250) + ".md";
        var refused = await FileWire.PlaceAsync(agents, Inventory, tooLong, "x"u8.ToArray(), "ABCDEF", Ct);
        Assert.Equal(Wire.PlaceOutcome.DoesNotFit, refused.Outcome);
        Assert.False(Directory.Exists(Path.Combine(root, "ABCDEF~2")), "the refused placement made the folder");

        // Orders' two conversations arrive later; the second takes the short name ABCDEF~2.
        using var orders = await AttachAsync(agents, "orders@alpha");
        await PlaceAsync(agents, "orders@alpha", "notes.md", "first", "abcdef-invoices");
        await PlaceAsync(agents, "orders@alpha", "notes.md", "orders' own words", "abcdef-payments");
        var victim = Path.Combine(root, "abcdef-payments");
        Assert.SkipUnless(Directory.Exists(Path.Combine(root, "ABCDEF~2")), "no 8.3 names on this volume");

        // Inventory places into ABCDEF again, with a name that fits: refused, and nothing reaches orders' folder.
        var refusedHere = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
            await FileWire.PlaceAsync(agents, Inventory, "planted.md", "inventory's words"u8.ToArray(), "ABCDEF", Ct));

        Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, refusedHere.StatusCode);
        Assert.False(File.Exists(Path.Combine(victim, "planted.md")), "inventory's file was written into orders' folder");
        Assert.Equal(Wire.PlaceOutcome.Placed, (await FileWire.PlaceAsync(
            agents, "orders@alpha", "later.md", "orders' own"u8.ToArray(), "abcdef-payments", Ct)).Outcome);
    }

    [Fact]
    public async Task A_file_named_like_a_conversation_is_passed_over_and_kept()
    {
        await using var server = await StartAsync(Path.Combine(_base, "alpha.db"));
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);
        using var inventory = await AttachAsync(agents, Inventory);
        var root = server.Runtime.Files.Root;

        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "orders"), "the person's file", Ct);

        var placed = await FileWire.PlaceAsync(agents, Inventory, "x.md", "x"u8.ToArray(), "orders", Ct);

        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
        Assert.Equal("the person's file", await File.ReadAllTextAsync(Path.Combine(root, "orders"), Ct));
        Assert.True(File.Exists(Path.Combine(root, "orders~2", "x.md")));
    }

    [Fact]
    public async Task A_junction_named_like_a_conversation_is_passed_over_and_its_target_untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are Windows'");

        var store = Path.Combine(_base, "alpha.db");
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "x.md"), "outside", Ct);

        await using (var server = await StartAsync(store))
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var agents = new Wire.Agents.AgentsClient(channel);
            using var inventory = await AttachAsync(agents, Inventory);
            var root = server.Runtime.Files.Root;
            Directory.CreateDirectory(root);
            Junction(Path.Combine(root, "stock-long-name"), outside);
            Junction(Path.Combine(root, "stock"), outside);

            // Both the junction's own name and (where 8.3 names exist) its short name STOCK-~1.
            var one = await FileWire.PlaceAsync(agents, Inventory, "x.md", "agent"u8.ToArray(), "stock", Ct);
            var two = await FileWire.PlaceAsync(agents, Inventory, "x.md", "agent"u8.ToArray(), "STOCK", Ct);

            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "x.md"), Ct));
            Assert.Single(Directory.EnumerateFiles(outside));

            var operators = new Wire.Operators.OperatorsClient(channel);
            _ = await operators.DestroyAsync(new Wire.ConversationRequest { ConversationId = "stock" }, cancellationToken: Ct);
            _ = await operators.DestroyAsync(new Wire.ConversationRequest { ConversationId = "STOCK" }, cancellationToken: Ct);
            Assert.True(File.Exists(Path.Combine(outside, "x.md")), $"{one.Outcome}/{two.Outcome}: a destroy reached the junction's target");
        }

        await using (await StartAsync(store))
        {
            Assert.True(File.Exists(Path.Combine(outside, "x.md")), "a restart reached the junction's target");
        }
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static async Task PlaceAsync(Wire.Agents.AgentsClient agents, string agent, string name, string words, string conversation)
    {
        var placed = await FileWire.PlaceAsync(agents, agent, name, System.Text.Encoding.UTF8.GetBytes(words), conversation, Ct);
        Assert.True(placed.Outcome is Wire.PlaceOutcome.Placed or Wire.PlaceOutcome.AlreadyCurrent, $"{name} in {conversation}: {placed}");
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
