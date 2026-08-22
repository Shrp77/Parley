using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Asking a peer that never comes back.
/// </summary>
/// <remarks>
/// The whole of §4.2 rests on something eventually arriving: an agent is told not to block
/// because the answer will reach its inbox. When nobody answers, the exchange used to end
/// correctly and silently — so the one case where an agent most needs to hear something was the
/// one case it heard nothing at all.
/// </remarks>
public sealed class GhostedQuestionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Asserts an inbox holds exactly one message, and says what it holds if not.</summary>
    /// <param name="inbox">The drained inbox, as the tool returned it.</param>
    /// <param name="what">What the caller expected to be waiting.</param>
    /// <param name="state">Extra evidence to append when it is not there, or <see langword="null"/>.</param>
    /// <returns>The single message.</returns>
    /// <remarks>
    /// <para>
    /// <c>Assert.Single</c> reports the count and not the contents, which is the wrong half. A
    /// run of this test failed once, on a loaded machine, and was never reproduced — twenty-seven
    /// further attempts including nine assemblies at once could not bring it back, and the count
    /// alone said nothing about whether the inbox was empty, held a second ghost, or held
    /// something else entirely. Those are three different defects.
    /// </para>
    /// <para>
    /// So the next occurrence carries its evidence. This changes no assertion; it changes what a
    /// failure is worth, which for something seen once in twenty-five runs is the only thing that
    /// can be improved without knowing the cause.
    /// </para>
    /// </remarks>
    private static System.Text.Json.Nodes.JsonNode OnlyMessage(
        System.Text.Json.Nodes.JsonNode inbox, string what, string? state = null)
    {
        var messages = inbox["messages"]!.AsArray();

        Assert.True(
            messages.Count == 1,
            $"expected exactly one message ({what}), got {messages.Count}: " +
            $"{inbox.ToJsonString()}{state}");

        return messages[0]!;
    }

    /// <summary>Reads the whole database back as text, for a failure to carry with it.</summary>
    /// <param name="path">The fixture's database.</param>
    /// <param name="ticks">What each sweep reported, in order.</param>
    /// <returns>A block of text describing every row that bears on ghosting.</returns>
    /// <remarks>
    /// <para>
    /// The inbox alone said only that nothing arrived. Whether nothing arrived because the
    /// exchange never orphaned, because it orphaned and never ghosted, because the ghost was
    /// written and delivered to somebody else, or because it was written and then removed are
    /// four different defects with the same symptom, and the count distinguishes none of them.
    /// </para>
    /// <para>
    /// Read on a connection of its own, deliberately. The sessions' connections are behind their
    /// own gate and borrowing one would mean the diagnostic could block on, or perturb, the thing
    /// it is diagnosing — and this runs at the moment the run has already gone wrong, which is
    /// the worst moment to introduce a second unknown.
    /// </para>
    /// </remarks>
    private static string Snapshot(string path, IEnumerable<string> ticks)
    {
        var text = new System.Text.StringBuilder()
            .AppendLine()
            .AppendLine("  ticks: " + string.Join(" | ", ticks));

        try
        {
            using var connection = Parley.Store.ParleyDatabase.Open(path);
            using var command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT 'exchange ' || id || ' state=' || state ||
                       ' owed_by=' || owed_by ||
                       ' resolution=' || coalesce(resolution, '-') ||
                       ' opened=' || opened_utc || ' expires=' || expires_utc ||
                       ' delivered=' || coalesce(delivered_utc, '-') ||
                       ' closed=' || coalesce(closed_utc, '-') || ' version=' || version
                  FROM exchanges
                UNION ALL
                SELECT 'message ' || id || ' type=' || type ||
                       ' ' || from_agent || '->' || to_agent ||
                       ' created=' || created_utc ||
                       ' delivered=' || coalesce(delivered_utc, '-')
                  FROM messages
                ORDER BY 1;
                """;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                text.AppendLine("  " + reader.GetString(0));
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The diagnostic must not replace the failure it is describing. If the database
            // cannot be read at all that is itself the most interesting fact available, so it is
            // reported rather than thrown.
            text.AppendLine("  (could not read the database: " + ex.GetType().Name + " " + ex.Message + ")");
        }

        return text.ToString();
    }

    [Fact]
    public async Task An_asker_whose_peer_never_answers_is_told_so_through_its_inbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (ordersSession, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding whether to store a tax rate on the order or read it from you.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();

        // Billing collects the question and then goes quiet for good.
        OnlyMessage(await billing.InboxAsync(cancellationToken: ct), "the question");

        // Every sweep is recorded, because which one orphaned and which one ghosted is the first
        // question a failure raises and the tick that did neither is invisible afterwards.
        var ticks = new List<string>();

        for (var hour = 1; hour <= 12; hour++)
        {
            fixture.Clock.Advance(TimeSpan.FromHours(1));

            var swept = await ordersSession.RunAsync(
                (broker, now, c) => broker.TickAsync(now, c), ct);

            ticks.Add(
                $"+{hour}h o={swept.Exchanges.Orphaned} g={swept.Exchanges.Ghosted} " +
                $"c={swept.Exchanges.Contended} pruned={swept.Retention.Removed}/" +
                $"{swept.Retention.HeldByFloor}");
        }

        var notice = OnlyMessage(
            await orders.InboxAsync(cancellationToken: ct), "the ghost notice",
            Snapshot(fixture.DatabasePath, ticks));

        Assert.Equal("ghost", notice!["type"]!.GetValue<string>());
        Assert.Equal(exchangeId, notice["exchange_id"]!.GetValue<long>());

        var envelope = notice["message"]!.GetValue<string>();

        // Wrapped like everything else (§13), attributed to the broker (§11.3), and carrying the
        // asker's own consequences back (§14) — which is what makes it something to act on
        // rather than a bereavement notice.
        Assert.Contains("--- begin peer payload", envelope, StringComparison.Ordinal);
        Assert.Contains("responder: parley:broker", envelope, StringComparison.Ordinal);
        Assert.Contains("No answer came from billing", envelope, StringComparison.Ordinal);
        Assert.Contains("snapshot my own copy", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ghosted_exchange_is_over_and_says_so_when_touched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (ordersSession, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            "billing", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        for (var hour = 1; hour <= 12; hour++)
        {
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            await ordersSession.RunAsync((broker, now, c) => broker.TickAsync(now, c), ct);
        }

        // The peer turning up late is refused rather than reopening a closed conversation.
        var late = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "answered", "Sorry — yes, at issue.")],
            cancellationToken: ct);

        Assert.False(late["ok"]!.GetValue<bool>(), late.ToJsonString());
    }
}
