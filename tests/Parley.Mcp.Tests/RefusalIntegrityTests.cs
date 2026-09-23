using Dapper;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What a refused call leaves behind, and what a fully-featured accepted one does.
/// </summary>
/// <remarks>
/// Two properties that only make sense together. A guard layer is easy to get wrong in
/// opposite directions — writing something before deciding to refuse, or refusing a call that
/// was always valid — and a suite that only ever checks refusals will catch the first and none
/// of the second.
/// </remarks>
public sealed class RefusalIntegrityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);


    [Fact]
    public async Task A_refused_call_writes_nothing_at_all()
    {
        // Every refusal shape in one deployment, then a straight count. The failure this guards
        // against has happened here before: an admission that wrote the exchange and then refused
        // the message left an exchange open with nothing in it, visible to nobody and resolvable
        // by no one. Guards run before the broker call for that reason, and this says they still do.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        const string One = "Does an invoice carry the rate at time of sale?";

        await orders.RequestAsync("Billing", One, cancellationToken: ct);
        await orders.RequestAsync("billing@test", string.Empty, cancellationToken: ct);
        await orders.RequestAsync("billing@test", "   ", cancellationToken: ct);
        await orders.RequestAsync("billing@test", One, "../bad", cancellationToken: ct);
        await orders.RequestAsync("billing@test", One, responderModel: "parley:broker", cancellationToken: ct);
        await orders.RequestAsync("billing@test", One, responderModel: "not a model", cancellationToken: ct);
        await orders.RequestAsync("orders@test", One, cancellationToken: ct);
        await orders.RequestAsync(
            "billing@test", new string('x', MessageLimits.MaxPayloadCharacters + 1000), cancellationToken: ct);

        var connection = fixture.Connection;

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM exchanges;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM conversations;"));

        // One exception, and it is deliberate: a refusal for length is counted. That is not
        // protocol state — no exchange, no message, no conversation, nothing another agent can
        // observe — and it is the only record anyone will ever have of whether the cap is set
        // anywhere near right. The row holds how long the payload was and never the payload.
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM size_refusals;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM size_refusals WHERE type <> 'request';"));
    }

    [Fact]
    public async Task Everything_the_tools_offer_at_once_is_still_admitted()
    {
        // The guards are layered, so the risk is not that one of them is wrong but that together
        // they refuse something the protocol allows. This uses every optional feature in one
        // exchange: a named conversation, a slash in a model identifier, a human responder, a
        // blocker and a source.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);

        var sent = await orders.RequestAsync(
            to: "billing@test",
            ask: "Is the tax rate stamped at time of sale? If so, stamp it on refunds too.",
            conversationId: "tax-rate",
            responderModel: "some-org/some-model-9",
            cancellationToken: ct);

        Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());

        var exchangeId = sent["exchange_id"]!.GetValue<long>();
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        var resolved = await billing.ResultAsync(
            exchangeId,
            "blocked",
            "Stamped at issue in invoice_lines.tax_rate. Refunds are written by a job I have no access to; "
            + "need write on refunds_job.",
            blocker: "permission",
            sources: ["inventory@test"],
            responderModel: "human:on-call",
            cancellationToken: ct);

        Assert.True(resolved["ok"]!.GetValue<bool>(), resolved.ToJsonString());
    }

    [Fact]
    public async Task Naming_yourself_as_a_source_is_refused()
    {
        // A source is another agent whose Result this one relied on. Naming yourself says nothing
        // a requester can follow, and storage refuses it as well — with a trigger name.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Is the tax rate stamped at time of sale?", cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(),
            "answered", "Yes.", sources: ["billing@test"],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("malformed", refused["refused"]!.GetValue<string>());
    }

    [Fact]
    public async Task Naming_an_agent_that_does_not_exist_as_a_source_is_refused_rather_than_throwing()
    {
        // It used to arrive as a SqliteException from the trigger that holds sources to the
        // registry. An agent that means a real peer and misspells it needs the roster, not SQL.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Is the tax rate stamped at time of sale?", cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(),
            "answered", "Inventory says yes.", sources: ["inventory@test"],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("malformed", refused["refused"]!.GetValue<string>());
        Assert.Contains("parley_agents", refused["why"]!.GetValue<string>(), StringComparison.Ordinal);

        // And still resolvable, so the refusal is a correction rather than a dead end.
        var second = await billing.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(),
            "answered", "Yes, at issue.",
            cancellationToken: ct);

        Assert.True(second["ok"]!.GetValue<bool>(), second.ToJsonString());
    }
}
