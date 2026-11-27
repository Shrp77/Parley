using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a destroyed conversation's folder another program kept on disk is said, raised while held and deleted once let
/// go, without waiting for the broker to start again (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Found by the Windows nightly of 2a55820: a destroy's folder survived it, and the delete that failed was ignored. Nothing
/// was logged, tried again or raised, though the sweep's own remarks promise what it cannot delete is raised as a problem
/// for the person. Windows only: a Unix file cannot be held against a delete.
/// </remarks>
[Integration]
public sealed class AFolderLeftByADestroyIsSweptAtOnceTests
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_folder_a_held_file_kept_is_said_raised_and_deleted_once_let_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows lets another program hold a file against a delete");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var placed = await FileWire.PlaceAsync(orders.Client, Orders, "a.md", "one"u8.ToArray(), "bait", Ct);
        Assert.Equal(Wire.FileRejection.None, placed.Rejection);

        var folder = broker.Runtime.Files.FolderPath("bait");
        var said = new List<string>();
        broker.Runtime.Sweeper.Left = entry =>
        {
            lock (said)
            {
                said.Add(entry);
            }
        };

        var operators = broker.ConnectAsOperator();

        using (new FileStream(Path.Combine(folder, "a.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = await operators.DestroyAsync(new Wire.ConversationRequest { ConversationId = "bait" }, cancellationToken: Ct);

            // The sweep the destroy asked for, while the file is still held.
            await broker.Runtime.Sweeper.IdleAsync();

            Assert.True(Directory.Exists(folder), "the file was not held, so this is not the case it holds");

            lock (said)
            {
                Assert.Equal(["bait"], said);
            }

            var problem = Assert.Single(
                (await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
            Assert.Equal(Wire.FileProblemKind.Locked, problem.Kind);
            Assert.StartsWith("bait", problem.Path, StringComparison.Ordinal);
        }

        // Let go: the next sweep, whatever asks for it, deletes it.
        await broker.Runtime.Sweeper.SweepAsync(Ct);

        Assert.False(Directory.Exists(folder), Contents(folder));
    }

    private static string Contents(string folder) =>
        Directory.Exists(folder)
            ? "left on disk: " + string.Join(", ", Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            : "gone";
}
