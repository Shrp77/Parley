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
    public void The_inbox_names_the_message_id_as_how_to_know_a_message_already_had()
    {
        var described = typeof(ParleyTools).GetMethod(nameof(ParleyTools.InboxAsync))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        Assert.Contains("one whose message_id you already have needs nothing more", described, StringComparison.Ordinal);
    }
}
