using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The flags of SPEC.md §6: how work that has stopped reaches the person running the deployment.
/// </summary>
/// <remarks>
/// <para>
/// Nothing times out, so this read is the only thing standing between a Request nobody is working on
/// and it staying open for ever unnoticed. Both of its failure directions are silent: a flag that never
/// fires hides a vanished agent, and one that fires on ordinary work teaches the person to ignore the list.
/// </para>
/// <para>
/// So the cases here are the boundaries between the two stages — collected or not — and the ways progress
/// shows up somewhere other than on the exchange itself.
/// </para>
/// </remarks>
[Integration]
public sealed class FlagRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Uncollected = ExchangePolicy.Default.UncollectedAfter;
    private static readonly TimeSpan Stalled = ExchangePolicy.Default.StalledAfter;

    private readonly SqliteConnection _connection;
    private readonly FlagRepository _flags;
    private readonly MessageRepository _messages;

    public FlagRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing, Inventory);
        _flags = new FlagRepository(_connection);
        _messages = new MessageRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<FlagReport> At(DateTimeOffset now) => _flags.ReadAsync(now, Ct);

    [Fact]
    public async Task Nothing_is_flagged_on_a_quiet_deployment()
    {
        Assert.True((await At(T0.AddYears(1))).IsEmpty);
    }

    [Fact]
    public async Task A_request_nobody_collected_is_flagged_once_its_threshold_passes()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);

        Assert.Empty((await At(T0 + Uncollected - TimeSpan.FromSeconds(1))).Exchanges);

        var flag = Assert.Single((await At(T0 + Uncollected)).Exchanges);

        Assert.Equal(FlagKind.NotCollected, flag.Kind);
        Assert.Equal(exchange.Id, flag.ExchangeId);
        Assert.Equal(Work.Conversation, flag.ConversationId);
        Assert.Equal(Billing, flag.WaitingOn);
        Assert.Equal(Orders, flag.Waiting);
        Assert.Equal(T0, flag.SinceUtc);
    }

    [Fact]
    public async Task A_collected_request_is_not_uncollected_however_long_it_has_been()
    {
        // The two stages are different facts. An agent working for hours on something it picked up is
        // ordinary; flagging it on the fifteen-minute threshold would flag every agent doing real work.
        var (_, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await _messages.MarkDeliveredAsync(request.Id, T0.AddMinutes(1), Ct);

        Assert.Empty((await At(T0.AddMinutes(1) + Stalled - TimeSpan.FromSeconds(1))).Exchanges);
    }

    [Fact]
    public async Task Collected_and_left_is_flagged_from_when_it_was_collected()
    {
        var (_, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await _messages.MarkDeliveredAsync(request.Id, T0.AddMinutes(1), Ct);

        var flag = Assert.Single((await At(T0.AddMinutes(1) + Stalled)).Exchanges);

        Assert.Equal(FlagKind.NoProgress, flag.Kind);
        Assert.Equal(T0.AddMinutes(1), flag.SinceUtc);
    }

    [Fact]
    public async Task Asking_somebody_else_for_part_of_the_work_is_progress()
    {
        // The case that makes the stage a property of the conversation rather than of the exchange: billing
        // is working on orders' Request by asking inventory for something, and says nothing on the exchange
        // itself while it waits.
        var (_, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await _messages.MarkDeliveredAsync(request.Id, T0.AddMinutes(1), Ct);

        var later = T0.AddHours(3);
        await Work.OpenAsync(_connection, Billing, Inventory, later, Work.Request("how much stock"), cancellationToken: Ct);

        var flagged = (await At(T0.AddMinutes(1) + Stalled)).Exchanges;

        Assert.DoesNotContain(flagged, f => f.WaitingOn == Billing);
        Assert.Contains((await At(later + Stalled)).Exchanges, f => f.WaitingOn == Billing && f.SinceUtc == later);
    }

    [Fact]
    public async Task A_clarification_moves_the_flag_to_whoever_now_owes_the_move()
    {
        var (exchange, request) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await _messages.MarkDeliveredAsync(request.Id, T0.AddMinutes(1), Ct);

        var asked = T0.AddMinutes(2);
        Assert.True(exchange.Clarify(Billing, asked).Succeeded);
        Assert.True(await new ExchangeRepository(_connection).SaveAsync(exchange, cancellationToken: Ct));
        await _messages.AppendAsync(
            exchange.Id, MessageType.Clarify, Billing, Orders, Work.Model, """{"text":"gross of which tax?"}""", asked,
            cancellationToken: Ct);

        var flag = Assert.Single((await At(asked + Uncollected)).Exchanges);

        Assert.Equal(FlagKind.NotCollected, flag.Kind);
        Assert.Equal(Orders, flag.WaitingOn);
        Assert.Equal(Billing, flag.Waiting);
        Assert.Equal(asked, flag.SinceUtc);
    }

    [Fact]
    public async Task A_poll_waiting_on_nothing_but_a_vote_is_flagged_and_names_who()
    {
        // Orders proposed closure while billing still held the work, so both were
        // polled; billing then sent its Result and never voted. The conversation stays closing, so billing is
        // asked again and the person is told — as §5.0 promises.
        var conversations = new ConversationRepository(_connection);
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);

        await conversations.BeginPollAsync(Work.Conversation, T0.AddMinutes(1), cancellationToken: Ct);
        Assert.True(await conversations.RecordVoteAsync(Work.Conversation, Orders, Vote.Done, T0.AddMinutes(1), cancellationToken: Ct));

        // While the work is open, the exchange's own flags speak for it and the vote is not what it waits on.
        Assert.DoesNotContain((await At(T0.AddDays(1))).Exchanges, f => f.Kind == FlagKind.NotVoted);
        Assert.Empty(await conversations.GetSilentVotersAsync(Work.Conversation, Ct));

        var closed = T0.AddMinutes(5);
        await Work.CloseAsync(_connection, exchange, closed, cancellationToken: Ct);

        var flag = Assert.Single((await At(closed)).Exchanges);

        Assert.Equal(FlagKind.NotVoted, flag.Kind);
        Assert.Equal(0, flag.ExchangeId);
        Assert.Equal(Work.Conversation, flag.ConversationId);
        Assert.Equal(Billing, flag.WaitingOn);
        Assert.Equal(Orders, flag.Waiting);
        Assert.Equal(closed, flag.SinceUtc);
        var silent = Assert.Single(await conversations.GetSilentVotersAsync(Work.Conversation, Ct));

        Assert.Equal(Billing, silent.Agent);
        Assert.Equal(T0.AddMinutes(1), silent.PolledUtc);

        // A decline is not silence, and nothing is flagged or woken for it.
        Assert.True(await conversations.RecordVoteAsync(Work.Conversation, Billing, Vote.NotDone, closed.AddMinutes(1), cancellationToken: Ct));

        Assert.Empty((await At(closed.AddMinutes(2))).Exchanges);
        Assert.Empty(await conversations.GetSilentVotersAsync(Work.Conversation, Ct));
    }

    [Fact]
    public async Task Work_held_by_a_retired_agent_is_flagged_at_once()
    {
        // Somebody asked for that work and nobody is going to do it, so there is nothing to wait for.
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        Execute("UPDATE agents SET status = 'retired' WHERE name = 'billing@test';");

        Assert.Equal(FlagKind.Retired, Assert.Single((await At(T0)).Exchanges).Kind);
    }

    [Fact]
    public async Task Finished_work_is_never_flagged()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Empty((await At(T0.AddYears(1))).Exchanges);
    }

    [Fact]
    public async Task Each_exchange_is_measured_against_the_thresholds_it_was_admitted_under()
    {
        // Stamped at admission, so a deployment shortening its thresholds does not re-flag work in flight.
        var quick = ExchangePolicy.Default with { UncollectedAfter = TimeSpan.FromMinutes(1) };

        await Work.OpenAsync(_connection, Orders, Billing, T0, policy: quick, cancellationToken: Ct);
        await Work.OpenAsync(_connection, Orders, Inventory, T0, cancellationToken: Ct);

        var flag = Assert.Single((await At(T0.AddMinutes(2))).Exchanges);
        Assert.Equal(Billing, flag.WaitingOn);
    }

    [Fact]
    public async Task The_longest_silence_comes_first()
    {
        await Work.OpenAsync(_connection, Orders, Inventory, T0.AddMinutes(30), cancellationToken: Ct);
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);

        var flags = (await At(T0.AddDays(1))).Exchanges;

        Assert.Equal([Billing, Inventory], flags.Select(f => f.WaitingOn));
    }

    [Fact]
    public async Task A_recorded_cycle_is_listed_while_its_conversation_has_work_open()
    {
        var (first, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        var (second, _) = await Work.OpenAsync(_connection, Billing, Orders, T0.AddMinutes(1), Work.Request("q2"), cancellationToken: Ct);

        var cycle = Assert.Single((await At(T0.AddMinutes(2))).Cycles);

        Assert.Equal(second.Id, cycle.ExchangeId);
        Assert.Equal([Billing, Orders], cycle.Path);

        await Work.CloseAsync(_connection, second, T0.AddMinutes(3), cancellationToken: Ct);
        await Work.CloseAsync(_connection, first, T0.AddMinutes(4), cancellationToken: Ct);

        Assert.Empty((await At(T0.AddMinutes(5))).Cycles);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
