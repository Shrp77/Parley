using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That each message the inbox hands an agent carries its own id, the one a message delivered twice repeats (SPEC.md §6).
/// </summary>
/// <remarks>
/// <b>Found by review, round 64.</b> The inbox told an agent to recognise a message it already had by its exchange_id,
/// which every message on an exchange shares: a Result carries the id its request returned, so an agent following the
/// words would drop every Result as a duplicate.
/// </remarks>
public sealed class AnInboxEntryCarriesItsOwnIdTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_request_and_its_result_share_an_exchange_and_differ_by_message_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync("billing@test", "Does an invoice carry the rate at time of sale?", cancellationToken: ct);
        var exchangeId = sent["exchange_id"]!.GetValue<long>();

        var request = Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray())!;
        _ = await billing.ResultAsync(exchangeId, "answered", "Yes, at the time of sale.", cancellationToken: ct);
        var result = Assert.Single((await orders.InboxAsync(cancellationToken: ct))["messages"]!.AsArray())!;

        Assert.Equal(exchangeId, request["exchange_id"]!.GetValue<long>());
        Assert.Equal(exchangeId, result["exchange_id"]!.GetValue<long>());
        Assert.NotEqual(request["message_id"]!.GetValue<long>(), result["message_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_message_handed_over_again_after_an_unacknowledged_collection_keeps_its_message_id()
    {
        // Round 58: the tests above held that the ids differ, not that a message delivered twice repeats its own.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        _ = await orders.RequestAsync("billing@test", "Does an invoice carry the rate at time of sale?", cancellationToken: ct);

        // A collection whose reply arrived and was never acknowledged: delivered at least once, so it comes again.
        long first;
        var raw = fixture.Connect();

        using (var call = raw.DrainInbox(cancellationToken: ct))
        {
            await call.RequestStream.WriteAsync(
                new Transport.DrainInboxFrame { Collect = new Transport.DrainInboxRequest { Agent = "billing@test" } }, ct);
            Assert.True(await call.ResponseStream.MoveNext(ct));
            first = Assert.Single(call.ResponseStream.Current.Messages).Message.Id;
        }

        System.Text.Json.Nodes.JsonArray again = [];

        // The broker releases what the call held once it sees the call end.
        for (var i = 0; i < 100 && again.Count == 0; i++)
        {
            await Task.Delay(50, ct);
            again = (await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray();
        }

        Assert.Equal(first, Assert.Single(again)!["message_id"]!.GetValue<long>());

        // And once acknowledged, it is not handed over a third time.
        Assert.Empty((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());
    }

    [Fact]
    public void The_inbox_names_the_message_id_as_how_to_know_a_message_already_had()
    {
        var described = typeof(ParleyTools).GetMethod(nameof(ParleyTools.InboxAsync))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        Assert.Contains("one this tool already gave you with that message_id needs nothing more", described, StringComparison.Ordinal);

        // Round 65: two projects' stores hand out the same ids, and an agent enrolled in both reads both inboxes.
        Assert.Contains("ids are its own", described, StringComparison.Ordinal);
    }
}
