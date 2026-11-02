using System.Diagnostics;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a conversation's folder replaced by hand with a junction is never entered: not counted through, not written
/// through, and on a destroy removed as a link with its target untouched (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// A link inside a folder was already one entry, never followed. The folder itself was not checked, so the count walked
/// whatever the junction led to, and a placement wrote its file there, outside the location. Windows only: the
/// junction is made with mklink.
/// </remarks>
[Integration]
public sealed class AFolderThatIsALinkIsNeverEnteredTests
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> SwapForJunctionAsync(string folder, string target)
    {
        Directory.Delete(folder, recursive: true);

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{folder}\" \"{target}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        await mklink.WaitForExitAsync(Ct);
        Assert.Equal(0, mklink.ExitCode);

        return folder;
    }

    [Fact]
    public async Task The_count_takes_the_folder_as_one_entry_and_the_sweep_it_starts_deletes_only_the_link()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "makes a junction with mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        // A recorded folder holding no file, so the list finds nothing missing through the link and only counts it.
        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", "one"u8.ToArray(), "bait", Ct);
        _ = await orders.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Orders, Files = { new Wire.FileRef { ConversationId = "bait", Name = "a.md" } } },
            cancellationToken: Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        var outside = Directory.CreateTempSubdirectory("parley-outside-");

        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(outside.FullName, "sub")).FullName;

            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sub, $"x{i}.md"), "not the location's");
            }

            await SwapForJunctionAsync(folder, outside.FullName);

            var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)
                .ResponseAsync.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await broker.Runtime.Sweeper.IdleAsync();

            Assert.Equal(1, listed.FilesNotListed);
            Assert.False(Directory.Exists(folder), "the sweep left the link");
            Assert.Equal(5, Directory.EnumerateFiles(sub).Count());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: false);
            }

            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_placement_into_the_folder_writes_nothing_through_it_and_starts_a_sweep()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "makes a junction with mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", "one"u8.ToArray(), "bait", Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        var outside = Directory.CreateTempSubdirectory("parley-outside-");

        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(outside.FullName, "sub")).FullName;

            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sub, $"x{i}.md"), "not the location's");
            }

            await SwapForJunctionAsync(folder, outside.FullName);

            // A placement that meets the link is refused, and starts the sweep that deletes it.
            var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
                () => FileWire.PlaceAsync(orders.Client, Orders, "b.md", "two"u8.ToArray(), "bait", Ct));
            await broker.Runtime.Sweeper.IdleAsync();

            Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, refused.StatusCode);
            Assert.False(File.Exists(Path.Combine(outside.FullName, "b.md")), "the placement wrote through the link");
            Assert.False(Directory.Exists(folder), "the sweep left the link");
            Assert.Equal(5, Directory.EnumerateFiles(sub).Count());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: false);
            }

            outside.Delete(recursive: true);
        }
    }
}
