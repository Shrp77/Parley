using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// A file the person puts in a conversation's folder, held open by somebody else when the broker first looks at it.
/// </summary>
/// <remarks>
/// Found from a Windows CI run where the person's new file never appeared in the list. A virus scanner opens a file
/// just written, and a look that met it held gave up; a list reconciles only the files it already records, so nothing
/// looked again until the next change on disk or the next start.
/// </remarks>
public sealed class APersonsFileHeldAtFirstLookTests
{
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_file_held_open_when_first_looked_at_is_listed_once_it_is_let_go()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(billing.Client, Billing, "start.md", "x"u8.ToArray(), "pricing", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "mine.md");

        // Written, then held with no sharing past the broker's first look, as a scanner holds a file it is reading.
        await File.WriteAllTextAsync(path, "the person's", Ct);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        var listed = await FileWire.WaitForAsync(operators, l => l.Any(f => f.Name == "mine.md"), TimeSpan.FromSeconds(10), Ct);

        Assert.Contains(listed, f => f.Name == "mine.md");
    }
}
