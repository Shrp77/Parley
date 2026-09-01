using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What the asker is told when nobody ever answers.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §14 has ghosting close the exchange <em>and</em> tell the asker that no answer is
/// coming. The close was implemented and the notice was not, so an agent could ask a question
/// and never learn anything about it again — the exchange ended, correctly and silently, and the
/// asker waited for an answer nothing would ever send. "Ask and do not block" rests entirely on
/// something eventually arriving.
/// </para>
/// <para>
/// The notice hands back the asker's own <c>consequences</c> because by then hours have passed
/// and the asker may have been compacted or restarted. §4.2 makes that argument about answers;
/// it applies at least as strongly to a non-answer, which arrives with no content of its own to
/// jog a memory.
/// </para>
/// </remarks>
public sealed class GhostNoticeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it does I read it from you; if not I snapshot my own copy and we can diverge.";

    private readonly SqliteConnection _connection;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly Reaper _reaper;

    public GhostNoticeTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
        _messages = new MessageRepository(_connection);
        _reaper = new Reaper(_exchanges, _messages, _connection);
    }

    public void Dispose() => _connection.Dispose();

    private static string Question(string intent, string consequences, int subQuestions = 1) =>
        JsonSerializer.Serialize(new
        {
            intent,
            sub_questions = Enumerable.Range(1, subQuestions)
                .Select(i => new { id = $"q{i}", text = $"is thing {i} gross or net?" }),
            consequences
        });

    /// <summary>Opens an exchange, sends its question, and lets the clock run out.</summary>
    /// <param name="questionJson">The question payload.</param>
    /// <returns>The exchange, now ghosted.</returns>
    private async Task<Exchange> GhostAsync(string questionJson)
    {
        var ct = TestContext.Current.CancellationToken;

        var opened = await _exchanges.TryOpenAsync(Orders, Billing, T0, questionJson,
            cancellationToken: ct);

        Assert.True(opened.Succeeded, $"expected admission, got {opened.Rejection}");

        await _messages.AppendAsync(
            opened.Exchange!.Id, MessageType.Question, Orders, Billing, "test-model",
            questionJson, T0, cancellationToken: ct);

        // Far enough past both windows that one sweep orphans and the next ghosts.
        await _reaper.SweepAsync(T0.AddHours(9), T0, ct);
        var swept = await _reaper.SweepAsync(T0.AddDays(2), T0, ct);

        Assert.Equal(1, swept.Ghosted);

        return (await _exchanges.GetAsync(opened.Exchange.Id, cancellationToken: ct))!;
    }

    private async Task<Message?> NoticeFor(long exchangeId)
    {
        var pending = await _messages.GetPendingAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        return pending.FirstOrDefault(m => m.ExchangeId == exchangeId);
    }

    [Fact]
    public async Task Ghosting_tells_the_asker_and_hands_back_its_own_words()
    {
        var exchange = await GhostAsync(Question("Deciding where the tax rate lives.", Consequences));

        Assert.Equal(ExchangeState.Closed, exchange.State);
        Assert.Equal(Core.Resolution.Ghosted, exchange.Resolution);

        var notice = await NoticeFor(exchange.Id);

        Assert.NotNull(notice);
        Assert.Equal(MessageType.Ghost, notice.Type);
        Assert.Equal(Orders, notice.To);

        using var payload = JsonDocument.Parse(notice.PayloadJson);

        Assert.True(payload.RootElement.GetProperty("ghosted").GetBoolean());
        Assert.Equal(Consequences, payload.RootElement.GetProperty("consequences").GetString());
        Assert.Contains(
            "Deciding where the tax rate lives.",
            payload.RootElement.GetProperty("intent").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_notice_is_attributed_to_the_broker_and_not_to_the_silent_peer()
    {
        // §11.3: the envelope renders responder identity on every delivered message, and an
        // unattributed close would leave the asker unable to tell a broker-authored answer from
        // a peer's. §15 counts exactly these.
        var exchange = await GhostAsync(Question("Deciding where the tax rate lives.", Consequences));
        var notice = await NoticeFor(exchange.Id);

        Assert.Equal("parley:broker", notice!.ResponderModel);

        // From the agent that never answered, because from_agent names a registered agent and
        // the broker is not one. The broker's identity travels in the responder.
        Assert.Equal(Billing, notice.From);
    }

    [Fact]
    public async Task A_ghost_that_is_not_the_brokers_is_refused_by_the_schema()
    {
        // The type is what the storage layer can attribute, which responder identity is not:
        // every row is written by the broker either way, so no CHECK can tell it writing its own
        // name from it recording what an adapter supplied. No agent operation sets a type.
        var ct = TestContext.Current.CancellationToken;

        var opened = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, Question("intent", Consequences), cancellationToken: ct);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => _messages.AppendAsync(
            opened.Exchange!.Id, MessageType.Ghost, Billing, Orders, "some-model-9",
            """{"ghosted":true,"text":"nobody answered"}""", T0, cancellationToken: ct));

        Assert.Contains("parley:broker", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_ever_ghosted_without_a_notice()
    {
        // The invariant the single transaction exists for. An exchange is ghostable exactly
        // once, so a close that committed without its notice would never be revisited — the
        // exchange is no longer orphaned — and the asker would wait for a message nothing will
        // write again.
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            var payload = Question($"Deciding thing {i}.", Consequences);
            var opened = await _exchanges.TryOpenAsync(
                Orders, Billing, T0.AddSeconds(i), payload, cancellationToken: ct);

            await _messages.AppendAsync(
                opened.Exchange!.Id, MessageType.Question, Orders, Billing, "test-model",
                payload, T0.AddSeconds(i), cancellationToken: ct);
        }

        await _reaper.SweepAsync(T0.AddHours(9), T0, ct);
        await _reaper.SweepAsync(T0.AddDays(2), T0, ct);

        var ghostedWithoutNotice = await _connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM exchanges e
             WHERE e.resolution = 'ghosted'
               AND NOT EXISTS (SELECT 1 FROM messages m
                                WHERE m.exchange_id = e.id AND m.type = 'ghost');
            """);

        var noticeWithoutGhost = await _connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM messages m
             WHERE m.type = 'ghost'
               AND NOT EXISTS (SELECT 1 FROM exchanges e
                                WHERE e.id = m.exchange_id AND e.resolution = 'ghosted');
            """);

        Assert.Equal(3, await _connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM messages WHERE type = 'ghost';"));
        Assert.Equal(0, ghostedWithoutNotice);
        Assert.Equal(0, noticeWithoutGhost);
    }

    [Fact]
    public async Task A_question_at_the_cap_still_produces_a_notice_that_fits()
    {
        // The notice adds the broker's own text to the asker's, so a question near the limit
        // cannot be carried back whole. A notice that will not store is a notice nobody
        // receives, so it is trimmed — and what survives is the field §14 says makes this
        // actionable.
        // Sized from the cap rather than to it. The padding was 3,450 characters, chosen to land
        // just under a cap of four thousand, and it silently stopped testing anything the day
        // rule 8.5's number moved — the assertion below caught it, which is the only reason this
        // is a comment rather than a defect.
        var payload = Question(
            new string('i', 120),
            Consequences + " " + new string('c', MessageLimits.MaxPayloadCharacters - 550),
            subQuestions: 3);

        // Close to the cap but under it, which is where the question has to be for the notice to
        // exceed it: the broker's own text is what tips it over.
        Assert.InRange(
            payload.Length, MessageLimits.MaxPayloadCharacters - 300,
            MessageLimits.MaxPayloadCharacters);

        var exchange = await GhostAsync(payload);
        var notice = await NoticeFor(exchange.Id);

        Assert.NotNull(notice);
        Assert.InRange(notice.PayloadJson.Length, 1, MessageLimits.MaxPayloadCharacters);

        using var read = JsonDocument.Parse(notice.PayloadJson);

        Assert.True(read.RootElement.GetProperty("ghosted").GetBoolean());
        Assert.True(read.RootElement.TryGetProperty("consequences", out _));
        Assert.False(read.RootElement.TryGetProperty("sub_questions", out _));
    }

    [Fact]
    public async Task An_exchange_is_not_closed_when_its_notice_cannot_be_written()
    {
        // The half of "one write" that is not about crashes. A deduplicated append writes no row
        // and reports it in the outcome instead of throwing, so a sweep that ignored the outcome
        // would commit the close beside a notice that is not there — "ghosted with nobody told",
        // reached by the one path that does not look like a failure.
        //
        // The notice is planted with the exact payload the reaper will build, which is why
        // GhostNotice is reachable from here: reproducing it by hand would give a test that
        // passes when two copies of one construction agree, and says nothing about the reaper.
        var ct = TestContext.Current.CancellationToken;
        var payload = Question("Deciding where the tax rate lives.", Consequences);

        var opened = await _exchanges.TryOpenAsync(Orders, Billing, T0, payload, cancellationToken: ct);

        await _messages.AppendAsync(
            opened.Exchange!.Id, MessageType.Question, Orders, Billing, "test-model", payload, T0,
            cancellationToken: ct);

        await _reaper.SweepAsync(T0.AddHours(9), T0, ct);

        var planted = await _messages.AppendAsync(
            opened.Exchange.Id, MessageType.Ghost, Billing, Orders, "parley:broker",
            GhostNotice.For(Billing, payload), T0.AddDays(2), cancellationToken: ct);

        Assert.True(planted.Stored, "the probe needs its own notice to have been written");

        var swept = await _reaper.SweepAsync(T0.AddDays(2), T0, ct);

        // Refused, and counted as contention rather than as a ghosting.
        Assert.Equal(0, swept.Ghosted);
        Assert.Equal(1, swept.Contended);

        var exchange = await _exchanges.GetAsync(opened.Exchange.Id, cancellationToken: ct);

        Assert.Equal(ExchangeState.Orphaned, exchange!.State);
        Assert.Equal(1, await _connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM messages WHERE type = 'ghost';"));
    }

    [Fact]
    public async Task An_exchange_that_was_answered_gets_no_notice()
    {
        // The other side, so the sweep is not simply writing one for everything it touches.
        var ct = TestContext.Current.CancellationToken;
        var payload = Question("Deciding where the tax rate lives.", Consequences);

        var opened = await _exchanges.TryOpenAsync(Orders, Billing, T0, payload, cancellationToken: ct);

        await _messages.AppendAsync(
            opened.Exchange!.Id, MessageType.Question, Orders, Billing, "test-model", payload, T0,
            cancellationToken: ct);

        Assert.True(opened.Exchange.Answer(Billing, T0.AddMinutes(5), Core.Resolution.Answered).Succeeded);
        Assert.True(await _exchanges.SaveAsync(opened.Exchange, cancellationToken: ct));

        await _reaper.SweepAsync(T0.AddDays(2), T0, ct);

        Assert.Equal(0, await _connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM messages WHERE type = 'ghost';"));
    }
}
