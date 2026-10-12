using System.Text.Json.Nodes;
using Dapper;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What the adapter asks an agent for, and what it writes on the agent's behalf.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §5.1 — a Request is the ask — and §5.3 — a Result answers it. Until 0.7 this file checked an
/// intent and a twenty-character finish line, both of which a model had to compose before it could ask
/// anybody anything, and both of which reached it as SQL when it got them wrong. They are gone; what is
/// left is that something was actually asked.
/// </para>
/// </remarks>
public sealed class RequestFramingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static void AssertMalformed(JsonNode result, params string[] mustMention)
    {
        Assert.False(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("malformed", result["refused"]!.GetValue<string>());

        foreach (var fragment in mustMention)
        {
            Assert.Contains(fragment, result["why"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_request_that_asks_nothing_is_refused(string ask)
    {
        // The only framing rule left. It is refused here rather than in the schema for the reason the
        // others were: storage answers with a trigger name, and an agent that asked for nothing has not
        // made a syntax mistake.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        AssertMalformed(await orders.RequestAsync("billing@test", ask, cancellationToken: ct), "say what you want");
    }

    [Fact]
    public async Task A_short_ask_is_sent_rather_than_held_to_a_length_nobody_set()
    {
        // 0.7's argument, as a check: the sender is not the one who can say how far an ask goes, so there
        // is no floor. A receiver that cannot scope this sends a Clarify.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync("billing@test", "gross or net?", cancellationToken: ct);

        Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());
    }

    [Fact]
    public async Task A_result_closes_the_exchange_without_anybody_naming_an_item()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Is the rate stamped at time of sale?", cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var answered = await billing.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(), "answered", "Yes, at issue.", cancellationToken: ct);

        Assert.True(answered["ok"]!.GetValue<bool>(), answered.ToJsonString());

        // And it is stored under the one spelling §5.3 reserves, rather than an identifier invented here.
        Assert.Equal(
            "request",
            fixture.Connection.ExecuteScalar<string>("SELECT item FROM result_items;"));
    }

    [Fact]
    public async Task A_bystander_learns_nothing_about_an_exchange_it_is_not_in()
    {
        // The §13 boundary. A refusal that said anything about the Request would carry another pair's
        // words to whoever had guessed the exchange id.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        var (_, inventory) = await fixture.LaunchAsync("inventory@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Is the rate stamped at sale, for migrate-payments-elsewhere?",
            cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await inventory.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(), "answered", "Nothing to do with me.", cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("notaparticipant", refused["refused"]!.GetValue<string>());
        Assert.DoesNotContain("migrate-payments-elsewhere", refused.ToJsonString(), StringComparison.Ordinal);
    }

}
