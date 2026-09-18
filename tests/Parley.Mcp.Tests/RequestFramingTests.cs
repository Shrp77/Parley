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
/// <para>
/// <b>The last test here is the one worth reading.</b> An exchange opened under 0.6 is still owed its own
/// item identifiers, and nothing times out, so one can outlive the upgrade indefinitely — while the tool
/// no longer lets a model type an identifier at all. The adapter closes that itself.
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

    /// <summary>Puts a stored 0.6 Request in place of the 0.7 one the fixture just sent.</summary>
    /// <param name="fixture">The running deployment.</param>
    /// <param name="payload">The document as 0.6 wrote it.</param>
    /// <remarks>
    /// Stands in for a message written before migration 011 ran. The trigger is stood down for the one write rather
    /// than the row being produced some other way, because a stored payload is never rewritten in service — which is
    /// the property 011 rests on, so there is no other route to the state an upgraded store is actually in.
    /// </remarks>
    private static void AsThoughWrittenUnder06(ServerFixture fixture, string payload) =>
        fixture.Connection.Execute(
            """
            DROP TRIGGER tr_request_is_well_formed;
            UPDATE messages SET payload_json = @payload WHERE type = 'request';
            CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
            WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
            BEGIN
              SELECT RAISE(ABORT, 'a request carries an ask')
               WHERE json_type(NEW.payload_json, '$.ask') IS NOT 'text'
                  OR length(trim(json_extract(NEW.payload_json, '$.ask'))) = 0;
            END;
            """,
            new { payload });

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
        // The §13 boundary. Checking that a Result covers the right items means reading which ones
        // were asked, and a refusal that listed them for anybody would carry another pair's
        // identifiers to whoever had guessed the exchange id. Identifiers are chosen by agents,
        // so one may well name what a team is working on.
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

    [Fact]
    public async Task An_exchange_opened_under_0_6_is_answered_by_its_own_items_without_the_model_naming_them()
    {
        // **The upgrade's loose end.** A 0.6 Request declared `q1` and `w1`, the broker still requires
        // exactly those, and `parley_result` no longer has an argument for an identifier — so without
        // this the receiver could never close the exchange, and nothing times out to release it.
        //
        // The broker names what it expected on the refusal (§5.3), so the adapter answers again with the
        // one answer recorded against each. Honest for a Request that is now one ask.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Is the rate stamped at time of sale?", cancellationToken: ct);

        var exchangeId = sent["exchange_id"]!.GetValue<long>();

        // Stands in for a message written before migration 011 ran. The trigger is stood down for the one
        // insert rather than the row being edited afterwards, because a stored payload is never updated —
        // which is the property migration 011 rests on, and writing it any other way here would be
        // testing against a store this build cannot produce.
        AsThoughWrittenUnder06(
            fixture,
            """{"intent":"where tax lives","items":[{"id":"q1","kind":"question","text":"gross or net?"},{"id":"w1","kind":"work","text":"stamp refunds"}],"done_when":"the rate is named and refunds carry it"}""");

        await billing.InboxAsync(cancellationToken: ct);

        var answered = await billing.ResultAsync(
            exchangeId, "answered", "Net, and refunds now carry it.", cancellationToken: ct);

        Assert.True(answered["ok"]!.GetValue<bool>(), answered.ToJsonString());

        Assert.Equal(
            "q1:answered|w1:answered",
            fixture.Connection.ExecuteScalar<string>(
                "SELECT group_concat(item || ':' || outcome, '|') FROM (SELECT item, outcome FROM result_items ORDER BY item);"));
    }

    [Fact]
    public async Task An_answer_the_retry_cannot_fit_names_the_real_constraint_rather_than_a_length_nobody_wrote()
    {
        // **Found by the review gate, lens 1 run 1.1.** Recording one answer against ten identifiers writes it ten
        // times, so an answer well under the cap can put the retry far over it — and the first version reported
        // "that message is 30405 characters", a number the agent never wrote. It could only respond by shortening
        // to something arbitrary or abandoning an exchange nothing will time out.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Is the rate stamped at time of sale?", cancellationToken: ct);

        var items = string.Join(
            ",",
            Enumerable.Range(1, 10).Select(i => $$"""{"id":"r{{i}}","kind":"work","text":"x"}"""));

        AsThoughWrittenUnder06(fixture, $$"""{"intent":"x","items":[{{items}}],"done_when":"a finish line long enough"}""");

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(), "done", new string('a', 3000), cancellationToken: ct);

        var why = refused["why"]!.GetValue<string>();

        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Contains("older version of the protocol", why, StringComparison.Ordinal);
        Assert.Contains("r10", why, StringComparison.Ordinal);

        // The length it names is one the agent can act on: what fits, not what the assembled document came to.
        Assert.DoesNotContain("30405", why, StringComparison.Ordinal);

        // **And nothing was written into the refusal record**, found by lens 2 run 2.3. `size_refusals` is what
        // SPEC.md §17 question 16 sets the cap from, and the only value in this protocol chosen from evidence
        // rather than judgement — so one agent's three-thousand-character answer must not become evidence of a
        // thirty-thousand-character one. Nothing the retry assembles is a message an agent composed.
        Assert.Equal(0, fixture.Connection.ExecuteScalar<long>("SELECT count(*) FROM size_refusals;"));
    }
}
