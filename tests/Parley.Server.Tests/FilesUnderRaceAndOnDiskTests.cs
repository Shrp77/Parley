using System.Diagnostics;
using System.Security.Cryptography;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Files under races and on a real disk: a fetch against removals and new placements of its name, a report across sessions, names the disk would
/// equate, a junction under a destroy, and refusals that must be sentences (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Each holds a property no other test reaches, so a change that breaks one of them says so.
/// </remarks>
public sealed class FilesUnderRaceAndOnDiskTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_fetch_racing_removals_and_new_placements_streams_bytes_matching_its_own_header_or_is_refused()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        var random = new Random(7);

        await FileWire.PlaceAsync(billing.Client, Billing, "big.txt", Bytes(random, 3 * 1024 * 1024), "pricing", Ct);

        var mismatches = new List<string>();
        var served = 0;

        for (var round = 0; round < 15; round++)
        {
            var fetching = FetchAsync(orders, "big.txt");
            // A placed file is never changed (SPEC.md §9.5): the name takes new bytes only once removed and placed again.
            var next = Bytes(random, (2 + round % 3) * 1024 * 1024);
            var replacing = Task.Run(async () =>
            {
                _ = await billing.Client.RemoveOwnFilesAsync(
                    new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "big.txt" } } },
                    cancellationToken: Ct);
                return await FileWire.PlaceAsync(billing.Client, Billing, "big.txt", next, "pricing", Ct);
            }, Ct);

            await Task.WhenAll(fetching, replacing);

            var (header, bytes, refusal) = await fetching;

            if (refusal is not null)
            {
                continue;
            }

            served++;

            var streamed = Convert.ToHexStringLower(SHA256.HashData(bytes));

            if (streamed != header!.Sha256 || bytes.Length != header.SizeBytes)
            {
                mismatches.Add($"round {round}: header {header.Sha256[..8]} of {header.SizeBytes}, bytes {streamed[..8]} of {bytes.Length}");
            }
        }

        Assert.True(served > 0, "every fetch was refused, so nothing was compared");
        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task A_report_still_lands_when_one_of_two_sessions_ends()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var first = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);

        Wire.SharedFileView header;

        await using (await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct))
        {
            header = (await FetchAsync(first, "notes.md")).Header!;
            Assert.Equal(2, broker.Runtime.SessionsFor(AgentName.Create(Orders)));
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (broker.Runtime.SessionsFor(AgentName.Create(Orders)) > 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(1, broker.Runtime.SessionsFor(AgentName.Create(Orders)));

        var reported = await first.Client.FetchedFileAsync(
            new Wire.FetchedFileRequest { Agent = Orders, ConversationId = header.ConversationId, Name = header.Name, Sha256 = header.Sha256 },
            cancellationToken: Ct);

        Assert.True(reported.Ok, $"refused {reported.Rejection}");
    }

    [Fact]
    public void No_two_names_with_different_keys_are_one_file_on_this_disk()
    {
        var directory = Directory.CreateTempSubdirectory("parley-keys-").FullName;
        var collisions = new List<string>();
        var tried = 0;

        try
        {
            for (var code = 0x41; code < 0xFFFF; code++)
            {
                var c = (char)code;

                if (!char.IsLetter(c) || char.IsSurrogate(c))
                {
                    continue;
                }

                var name = "k" + c + ".md";

                if (SharedFileName.Check(name) != SharedFileNameProblem.None)
                {
                    continue;
                }

                tried++;

                try
                {
                    using var created = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
                }
                catch (IOException)
                {
                    // The disk says a file of this name exists. The entry it matched, as stored:
                    var existing = Path.GetFileName(Assert.Single(Directory.GetFiles(directory, name)));

                    if (SharedFileName.KeyOf(existing) != SharedFileName.KeyOf(name))
                    {
                        collisions.Add($"U+{code:X4}: '{name}' is '{existing}' on disk; keys '{SharedFileName.KeyOf(name)}' and '{SharedFileName.KeyOf(existing)}'");
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        // Every letter a name can hold, which is the ASCII ones since the set replaced the list (SPEC.md §9.5). This probe
        // tried every letter in the basic plane while names could be any of them, and still walks them all, so a set
        // widened again is probed again without anybody remembering to.
        Assert.Equal(52, tried);
        Assert.True(collisions.Count == 0, string.Join('\n', collisions.Take(25)) + $"\n({collisions.Count} in all, of {tried})");
    }

    [Fact]
    public async Task Destroying_a_conversation_whose_folder_holds_a_junction_leaves_what_it_points_at()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("junctions are a Windows probe");
        }

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "notes.md", Text("net"), "pricing", Ct);

        var outside = Directory.CreateTempSubdirectory("parley-outside-").FullName;
        var kept = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(kept, "not the location's", Ct);

        try
        {
            var link = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "elsewhere");

            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                   {
                       UseShellExecute = false,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true
                   })!)
            {
                await mklink.WaitForExitAsync(Ct);
                Assert.True(mklink.ExitCode == 0, "mklink failed");
            }

            Assert.True(File.Exists(Path.Combine(link, "keep.txt")), "the junction does not lead where it was made to");

            await broker.ConnectAsOperator().DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct);

            Assert.True(File.Exists(kept), "destroying the conversation deleted a file outside the location through a junction");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Every_refusal_about_a_name_says_what_is_wrong_in_words()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        await FileWire.PlaceAsync(billing.Client, Billing, "schema.md", Text("p"), "pricing", Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "schema.md", Text("r"), "refunds", Ct);

        var operators = broker.ConnectAsOperator();
        var empty = new List<string>();

        void Check(string what, string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                empty.Add(what);
            }
        }

        Check("describe, ambiguous", (await operators.DescribeFileLocationAsync(new Wire.FileLocationRequest { Name = "schema.md" }, cancellationToken: Ct)).Refusal.Detail);
        Check("describe, missing", (await operators.DescribeFileLocationAsync(new Wire.FileLocationRequest { Name = "absent.md" }, cancellationToken: Ct)).Refusal.Detail);
        Check("person's removal, ambiguous", Assert.Single((await operators.RemoveFilesAsync(new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { Name = "schema.md" } } }, cancellationToken: Ct)).Refused).Detail);
        Check("person's removal, missing", Assert.Single((await operators.RemoveFilesAsync(new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { Name = "absent.md" } } }, cancellationToken: Ct)).Refused).Detail);
        Check("agent's removal, placed by another", Assert.Single((await orders.Client.RemoveOwnFilesAsync(new Wire.RemoveFilesRequest { Agent = Orders, Files = { new Wire.FileRef { ConversationId = "pricing", Name = "schema.md" } } }, cancellationToken: Ct)).Refused).Detail);
        Check("fetch, ambiguous", (await FetchAsync(orders, "schema.md", conversation: null)).Refusal?.Detail);
        Check("fetch, missing", (await FetchAsync(orders, "absent.md", conversation: null)).Refusal?.Detail);

        Assert.True(empty.Count == 0, "no words: " + string.Join("; ", empty));
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static async Task<(Wire.SharedFileView? Header, byte[] Bytes, Wire.FileRefusal? Refusal)> FetchAsync(
        AttachedClient agent, string name, string? conversation = "pricing")
    {
        using var call = agent.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = agent.Name, Name = name, ConversationId = conversation ?? string.Empty },
            cancellationToken: Ct);

        Wire.SharedFileView? header = null;
        Wire.FileRefusal? refusal = null;
        using var bytes = new MemoryStream();

        try
        {
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
        }
        catch (RpcException failed)
        {
            refusal = new Wire.FileRefusal { Detail = "call failed: " + failed.Status };
        }

        return (header, bytes.ToArray(), refusal);
    }
}
