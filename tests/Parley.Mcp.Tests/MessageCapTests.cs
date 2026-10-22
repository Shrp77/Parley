using System.Text.Json.Nodes;
using Dapper;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent sees when it writes too much.
/// </summary>
/// <remarks>
/// <para>
/// The message size cap is a context-poisoning defence rather than a buffer size, and it
/// lives in the schema so no code path can route around it. That also makes the failure a
/// <c>SqliteException</c> carrying SQL — a limit an agent reaches by working normally, reported
/// in a form it cannot act on. Models are verbose.
/// </para>
/// <para>
/// So this pins the shape of the refusal as much as the fact of it: <c>ok: false</c> and a
/// sentence about saying less, the same as every other thing an agent can get wrong.
/// </para>
/// </remarks>
public sealed class MessageCapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Room left for the Request around the ask: the JSON and one key.</summary>
    /// <remarks>
    /// The cap counts the stored payload, so an item's text can never be the whole of it. Generous on
    /// purpose; what is checked near the boundary is searched for rather than assumed from this.
    /// </remarks>
    private const int Wrapper = 400;

    private static string Long(int characters) => new('x', characters);

    private static Task<JsonNode> RequestAsync(ParleyTools tools, string text, CancellationToken ct) =>
        tools.RequestAsync("billing@test", text, cancellationToken: ct);

    private static void AssertRefusedForLength(JsonNode result)
    {
        Assert.False(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("toolong", result["refused"]!.GetValue<string>());
        Assert.Contains(
            MessageLimits.MaxPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result["why"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_request_is_refused_with_something_to_do_about_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        AssertRefusedForLength(await RequestAsync(orders, Long(MessageLimits.MaxPayloadCharacters * 2), ct));

        // And nothing was delivered, so the refusal is not a report on a message that got through.
        Assert.Empty((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());
    }

    [Fact]
    public async Task An_oversized_result_is_refused_and_the_exchange_stays_resolvable()
    {
        // The one where refusing badly would cost most. A Result rejected by the database leaves
        // the agent holding work it has just been told nothing useful about, and its obvious next
        // move is to send the same thing again.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await RequestAsync(orders, "Does an invoice carry the rate at time of sale?", ct);

        var exchangeId = sent["exchange_id"]!.GetValue<long>();
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        AssertRefusedForLength(await billing.ResultAsync(
            exchangeId, "answered", Long(MessageLimits.MaxPayloadCharacters), cancellationToken: ct));

        // Shorter, and it lands. The refusal has to be recoverable or it is just a dead end.
        var second = await billing.ResultAsync(
            exchangeId, "answered", "Yes. invoice_lines.tax_rate is stamped at issue.",
            cancellationToken: ct);

        Assert.True(second["ok"]?.GetValue<bool>() ?? false, second.ToJsonString());
    }

    [Fact]
    public async Task The_guard_never_admits_what_the_database_would_refuse()
    {
        // The property that makes the guard worth having rather than merely present: it has to be
        // conservative in one direction. If it ever admitted a message the CHECK then rejected, an
        // agent would be told its message was fine and get a SqliteException — only near the
        // boundary, so it would look like an intermittent fault rather than a length problem.
        //
        // Searched rather than asserted at one length, because the boundary is not the number in
        // MessageLimits: the cap counts the stored JSON, so the wrapper and any escaping come out
        // of the same budget. Every probe either lands or is refused for length; nothing else.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        var admitted = 0;
        int low = 1, high = MessageLimits.MaxPayloadCharacters + 64;

        while (low <= high)
        {
            var middle = (low + high) / 2;

            // Distinct per probe, so a duplicate refusal is not what ends the search.
            var result = await RequestAsync(orders, Long(middle) + $" {middle}", ct);

            if (result["ok"]?.GetValue<bool>() ?? false)
            {
                admitted = middle;
                low = middle + 1;
            }
            else
            {
                Assert.Equal("toolong", result["refused"]!.GetValue<string>());
                high = middle - 1;
            }
        }

        // Something is admitted, and it is close to the cap rather than far below it — a guard that
        // refused everything would also never admit what the database refuses.
        Assert.InRange(admitted, MessageLimits.MaxPayloadCharacters - Wrapper, MessageLimits.MaxPayloadCharacters);
    }

    [Fact]
    public async Task Text_that_json_escapes_is_measured_after_escaping()
    {
        // Nine hundred emoji is eighteen hundred UTF-16 units and looks comfortably inside the cap,
        // but each one escapes to twelve characters in the stored payload. Measuring the caller's
        // string rather than the JSON would wave this through and let the database refuse it.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        var emoji = string.Concat(Enumerable.Repeat("😀", 900));
        Assert.True(emoji.Length < MessageLimits.MaxPayloadCharacters);

        AssertRefusedForLength(await RequestAsync(orders, emoji, ct));
    }

    [Fact]
    public async Task A_message_just_under_the_cap_is_admitted()
    {
        // The other side, so the guard is not simply refusing everything long.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await RequestAsync(orders, Long(MessageLimits.MaxPayloadCharacters - Wrapper), ct);

        Assert.True(sent["ok"]?.GetValue<bool>() ?? false, sent.ToJsonString());
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());
    }

    [Fact]
    public async Task A_refusal_is_counted_where_the_cap_can_be_argued_from()
    {
        // The cap's evidence comes from here, so the check is that a refusal an agent actually hits
        // reaches the record with the type and the length intact — and that the payload does not,
        // because keeping the text refused for being too long would retain what the cap keeps out.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        var refused = await RequestAsync(orders, Long(MessageLimits.MaxPayloadCharacters + 500), ct);

        Assert.False((bool)refused["ok"]!);
        Assert.Equal("toolong", (string?)refused["refused"]);

        var connection = fixture.Connection;
        var only = Assert.Single(await new SizeRefusalRepository(connection).GetAllAsync(ct));

        Assert.Equal("orders@test", only.Agent.Value);
        Assert.Equal(MessageType.Request, only.Type);
        Assert.Equal(1, only.Refusals);
        Assert.True(only.Worst > MessageLimits.MaxPayloadCharacters);
        Assert.Equal(MessageLimits.MaxPayloadCharacters, only.Cap);

        // Nothing of what was sent survives anywhere.
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task A_refusal_survives_the_bookkeeping_behind_it_failing()
    {
        // The recording writes a row keyed on the agent, so anything that makes that write fail must
        // not reach the agent as an exception: the refusal is the answer it needs. Forced here by
        // removing the registration the row references, which is the shape of every reason this
        // write can fail — a locked database, a full disk, a foreign key.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        fixture.Connection.Execute("PRAGMA foreign_keys = ON; DELETE FROM agents WHERE name = 'orders@test';");

        var refused = await RequestAsync(orders, Long(MessageLimits.MaxPayloadCharacters + 500), ct)
            .WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.False((bool)refused["ok"]!);
        Assert.Equal("toolong", (string?)refused["refused"]);

        // And the write really did fail, so this is not a check that passes while testing nothing.
        Assert.Equal(0, fixture.Connection.ExecuteScalar<long>("SELECT count(*) FROM size_refusals;"));
    }

    [Fact]
    public async Task Refusing_does_not_deadlock_on_the_session_gate()
    {
        // The recording takes the same gate every tool call takes, and a SemaphoreSlim is not
        // reentrant, so a refusal checked from inside the gate rather than before it would hang the
        // server rather than fail it. The timeout turns that into a message.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        var oversized = Long(MessageLimits.MaxPayloadCharacters + 500);

        await RequestAsync(orders, oversized, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        await orders.ClarifyAsync(1, oversized, cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        await orders.ResultAsync(1, "answered", oversized, cancellationToken: ct)
            .WaitAsync(TimeSpan.FromSeconds(15), ct);

        // And the session still works afterwards, which is what says the gate was released.
        var roster = await orders.AgentsAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.NotNull(roster["agents"]);
    }
}
