using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a download its client stops reading is let go, so it cannot stall a destroy or other agents' placements.
/// </summary>
/// <remarks>
/// <b>Found by review, round 22.</b> Flow control parks a write to a client that stops reading, and the hold that
/// download kept made a destroy, purge or prune wait for good, and with it every placement that waits one out. A
/// download that makes no progress for <see cref="FileLocationStore.StallAfter"/> is now ended; a replacement no
/// longer waits out a hold over everything at all.
/// </remarks>
public sealed class AStalledDownloadIsLetGoTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_fetch_never_read_does_not_stall_an_unrelated_destroy_or_other_agents_placements()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(1);

        // A large file the slow agent will fetch; nothing bounds a file's size.
        var big = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        // An unrelated conversation with a file, which the person destroys; and another with a file inventory replaces.
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        await FileWire.PlaceAsync(inventory.Client, Inventory, "stock.md", Text("12"), "stock", Ct);

        // Orders fetches and reads one frame, then never reads again: HTTP/2 flow control parks the broker's write.
        using var slow = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "big.md", ConversationId = "bait" }, cancellationToken: Ct);
        Assert.True(await slow.ResponseStream.MoveNext(Ct));
        await Task.Delay(500, Ct);

        var destroying = broker.ConnectAsOperator()
            .DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(300, Ct);

        var replacing = FileWire.PlaceAsync(inventory.Client, Inventory, "stock.md", Text("13"), "stock", Ct);
        var firstPlacing = FileWire.PlaceAsync(billing.Client, Billing, "fresh.md", Text("new"), "elsewhere", Ct);

        await Task.WhenAll(
                Task.WhenAny(destroying, Task.Delay(5000, Ct)),
                Task.WhenAny(replacing, Task.Delay(5000, Ct)),
                Task.WhenAny(firstPlacing, Task.Delay(5000, Ct)));

        Assert.True(
            destroying.IsCompleted && replacing.IsCompleted && firstPlacing.IsCompleted,
            $"after 5s with orders' fetch unread: destroy of an unrelated conversation done={destroying.IsCompleted}, "
            + $"inventory's replacement done={replacing.IsCompleted}, billing's first placement done={firstPlacing.IsCompleted}");
    }
}
