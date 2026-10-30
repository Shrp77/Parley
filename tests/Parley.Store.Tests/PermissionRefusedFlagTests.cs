using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That work stopped by a refused permission is flagged at once, and only then (SPEC.md §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>A refused command is flagged at once</b>: an agent's command may be refused for want of approval and its turn end
/// without a word, and the earliest anybody would otherwise be told is the four-hour "no progress" threshold. A refusal is something
/// only a person can get past, so there is nothing to be patient with.
/// </para>
/// <para>
/// Derived, never stored: each case is a fact in <c>invocations</c> or <c>messages</c>, and the flag is whatever those say
/// when it is read.
/// </para>
/// </remarks>
[Integration]
public sealed class PermissionRefusedFlagTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly FlagRepository _flags;
    private readonly MessageRepository _messages;
    private readonly InvocationRepository _turns;

    public PermissionRefusedFlagTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing);
        _flags = new FlagRepository(_connection);
        _messages = new MessageRepository(_connection);
        _turns = new InvocationRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Orders asks billing at T0; billing picks it up at T0 + 1m.</summary>
    private async Task<Exchange> PickedUpAsync()
    {
        var (exchange, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await _messages.MarkDeliveredAsync(request.Id, T0.AddMinutes(1), Ct);

        return exchange;
    }

    /// <summary>A turn of billing's, from T0 + 2m for a minute, with so many refusals.</summary>
    private Task TurnAsync(int denials, DateTimeOffset? started = null) =>
        _turns.RecordAsync(
            Billing, 1, 1, started ?? T0.AddMinutes(2), TimeSpan.FromMinutes(1), 0, false, permissionDenials: denials,
            cancellationToken: Ct);

    [Fact]
    public async Task A_turn_that_ended_owing_work_with_a_refusal_is_flagged_at_once()
    {
        var exchange = await PickedUpAsync();
        await TurnAsync(denials: 1);

        // Three minutes in, nowhere near the four-hour threshold.
        var flag = Assert.Single((await _flags.ReadAsync(T0.AddMinutes(3), Ct)).Exchanges);

        Assert.Equal(FlagKind.PermissionRefused, flag.Kind);
        Assert.Equal(exchange.Id, flag.ExchangeId);
        Assert.Equal(Billing, flag.WaitingOn);
        Assert.Equal(Orders, flag.Waiting);
        Assert.Equal(T0.AddMinutes(3), flag.SinceUtc);
    }

    [Fact]
    public async Task A_turn_that_ended_owing_work_without_a_refusal_waits_for_the_ordinary_threshold()
    {
        // It may be pausing for a good reason; the threshold is patience with that.
        await PickedUpAsync();
        await TurnAsync(denials: 0);

        Assert.Empty((await _flags.ReadAsync(T0.AddMinutes(3), Ct)).Exchanges);
    }

    [Fact]
    public async Task Saying_something_after_the_turn_clears_it()
    {
        var exchange = await PickedUpAsync();
        await TurnAsync(denials: 1);

        // Billing asks orders something, after the refused turn: it is talking, not stuck.
        var asked = T0.AddMinutes(5);
        Assert.True(exchange.Clarify(Billing, asked).Succeeded);
        Assert.True(await new ExchangeRepository(_connection).SaveAsync(exchange, cancellationToken: Ct));
        await _messages.AppendAsync(
            exchange.Id, MessageType.Clarify, Billing, Orders, Work.Model, """{"text":"may I run the build?"}""", asked,
            cancellationToken: Ct);

        Assert.DoesNotContain(
            (await _flags.ReadAsync(asked.AddMinutes(1), Ct)).Exchanges,
            flag => flag.Kind == FlagKind.PermissionRefused);
    }

    [Fact]
    public async Task Closing_the_exchange_clears_it()
    {
        var exchange = await PickedUpAsync();
        await TurnAsync(denials: 1);

        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(10), cancellationToken: Ct);

        Assert.Empty((await _flags.ReadAsync(T0.AddMinutes(11), Ct)).Exchanges);
    }

    [Fact]
    public async Task A_later_clean_turn_clears_it_and_the_ordinary_threshold_applies_again()
    {
        // Read off the most recent turn: the refusal is no longer what is stopping it.
        await PickedUpAsync();
        await TurnAsync(denials: 1);
        await TurnAsync(denials: 0, started: T0.AddMinutes(10));

        Assert.Empty((await _flags.ReadAsync(T0.AddMinutes(12), Ct)).Exchanges);
    }

    [Fact]
    public async Task A_refused_turn_from_before_the_pickup_is_not_about_this_work()
    {
        // The turn ended before billing took this request, so its refusal was about something else.
        var (exchange, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await TurnAsync(denials: 2, started: T0);
        await _messages.MarkDeliveredAsync(request.Id, T0.AddMinutes(5), Ct);

        Assert.DoesNotContain(
            (await _flags.ReadAsync(T0.AddMinutes(6), Ct)).Exchanges,
            flag => flag.Kind == FlagKind.PermissionRefused && flag.ExchangeId == exchange.Id);
    }
}
