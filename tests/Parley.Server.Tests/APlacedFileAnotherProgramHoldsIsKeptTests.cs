using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a placed file another program holds open is raised as a problem and kept, never taken for gone (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Found by review, lens 3 run 115. The one test named for a held file held only a stray nobody placed, so a sweep that
/// read a held placed file as missing, deleting its record and telling its placer it was gone, failed no test. What is
/// held here is the file, its listing and its placer's silence, not only that a problem was raised. Windows only: a
/// Unix file cannot be held against a reader.
/// </remarks>
[Integration]
public sealed class APlacedFileAnotherProgramHoldsIsKeptTests
{
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_placed_file_another_program_holds_is_raised_and_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows lets another program hold a file against a reader");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", System.Text.Encoding.UTF8.GetBytes("net 30"), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md");

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await broker.Runtime.Sweeper.SweepAsync(Ct);
        }

        var operators = broker.ConnectAsOperator();
        var problem = Assert.Single(
            (await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems);
        Assert.Contains("terms.md", problem.Path, StringComparison.Ordinal);

        // Let go and swept again: the file is still there, still listed, and its placer was never told it was gone.
        await broker.Runtime.Sweeper.SweepAsync(Ct);

        Assert.True(File.Exists(path), "the sweep deleted a file another program only held");
        Assert.Single((await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
        Assert.Empty((await billing.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).FileNotices);
    }
}
