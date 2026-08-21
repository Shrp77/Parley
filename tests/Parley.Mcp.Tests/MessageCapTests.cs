using System.Text.Json.Nodes;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent sees when it writes too much.
/// </summary>
/// <remarks>
/// <para>
/// The 2,000-character cap is a context-poisoning defence rather than a buffer size, and it
/// lives in the schema so no code path can route around it. That makes it unroutable-around and
/// it also makes the failure a <c>SqliteException</c> carrying SQL — which is a limit an agent
/// reaches by working normally, reported in a form it cannot act on. Models are verbose.
/// </para>
/// <para>
/// So this pins the shape of the refusal as much as the fact of it: <c>ok: false</c> and a
/// sentence about saying less, the same as every other thing an agent can get wrong.
/// </para>
/// </remarks>
public sealed class MessageCapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static string Long(int characters) => new('x', characters);

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
    public async Task An_oversized_notification_is_refused_with_something_to_do_about_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        AssertRefusedForLength(await orders.NotifyAsync(
            "billing", Long(MessageLimits.MaxPayloadCharacters * 2), cancellationToken: ct));

        // And nothing was delivered, so the refusal is not a report on a message that got through.
        Assert.Empty((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());
    }

    [Fact]
    public async Task An_oversized_question_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        await fixture.LaunchAsync("billing", ct);

        AssertRefusedForLength(await orders.AskAsync(
            to: "billing",
            intent: "Deciding where the tax rate lives.",
            subQuestions: [new SubQuestion("q1", Long(MessageLimits.MaxPayloadCharacters))],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct));
    }

    [Fact]
    public async Task An_oversized_answer_is_refused_and_the_exchange_stays_answerable()
    {
        // The one where refusing badly would cost most. An answer rejected by the database
        // leaves the agent holding a debt it has just been told nothing useful about, and its
        // obvious next move is to send the same thing again.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding where the tax rate lives.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        AssertRefusedForLength(await billing.AnswerAsync(
            exchangeId,
            [new Resolution("q1", "answered", Long(MessageLimits.MaxPayloadCharacters))],
            cancellationToken: ct));

        // Shorter, and it lands. The refusal has to be recoverable or it is just a dead end.
        var second = await billing.AnswerAsync(
            exchangeId,
            [new Resolution("q1", "answered", "Yes. invoice_lines.tax_rate is stamped at issue.")],
            cancellationToken: ct);

        Assert.True(second["ok"]?.GetValue<bool>() ?? false, second.ToJsonString());
    }

    [Fact]
    public async Task The_guard_never_admits_what_the_database_would_refuse()
    {
        // The property that makes the guard worth having rather than merely present: it has to
        // be conservative in one direction. If it ever admitted a message the CHECK then
        // rejected, an agent would be told its message was fine and get a SqliteException — the
        // worst of both, and only for messages near the boundary, so it would look like an
        // intermittent fault rather than a length problem.
        //
        // Searched rather than asserted at one length, because the boundary is not the number in
        // MessageLimits: the cap counts the stored JSON, so the wrapper and any escaping come
        // out of the same budget. Non-ASCII is where that bites — JSON escapes it, so a
        // character an agent thinks of as one can cost six or twelve.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        await fixture.LaunchAsync("billing", ct);

        var admitted = 0;
        int low = 1, high = MessageLimits.MaxPayloadCharacters + 64;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            var result = await orders.NotifyAsync(
                "billing", new string('x', middle) + $" {middle}", cancellationToken: ct);

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

        // Something is admitted, and it is close to the cap rather than far below it — a guard
        // that refused everything would also never admit what the database refuses.
        Assert.InRange(admitted, MessageLimits.MaxPayloadCharacters - 64, MessageLimits.MaxPayloadCharacters);
    }

    [Fact]
    public async Task Text_that_json_escapes_is_measured_after_escaping()
    {
        // Nine hundred emoji is eighteen hundred UTF-16 units and looks comfortably inside the
        // cap, but each one escapes to twelve characters in the stored payload. Measuring the
        // caller's string rather than the JSON would wave this through and let the database
        // refuse it.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        await fixture.LaunchAsync("billing", ct);

        var emoji = string.Concat(Enumerable.Repeat("😀", 900));
        Assert.True(emoji.Length < MessageLimits.MaxPayloadCharacters);

        AssertRefusedForLength(await orders.NotifyAsync("billing", emoji, cancellationToken: ct));
    }

    [Fact]
    public async Task A_message_just_under_the_cap_is_admitted()
    {
        // The other side, so the guard is not simply refusing everything long. The cap counts the
        // stored payload rather than the caller's text, so the wrapper has to be left room —
        // telling an agent its message fits and then failing would be worse than not checking.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        var sent = await orders.NotifyAsync(
            "billing", Long(MessageLimits.MaxPayloadCharacters - 100), cancellationToken: ct);

        Assert.True(sent["ok"]?.GetValue<bool>() ?? false, sent.ToJsonString());
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());
    }
}
