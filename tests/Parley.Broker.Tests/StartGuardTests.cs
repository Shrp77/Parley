using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What every operation does before <see cref="Broker.StartAsync"/> has run.
/// </summary>
/// <remarks>
/// Until the attach sequence has run, the registry still holds liveness a previous run observed and work
/// collected by a session that has gone is still stamped as collected. An operation deciding against either is
/// deciding against evidence a start invalidates, so every one of them is refused rather than answered from it.
/// </remarks>
public sealed class StartGuardTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly Broker _broker;
    private readonly long _exchangeId;

    public StartGuardTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing);

        _exchangeId = new ExchangeRepository(_connection)
            .TryOpenAsync(Orders, Billing, T0, "stale", "pricing", cancellationToken: Ct)
            .GetAwaiter().GetResult().Exchange!.Id;

        // Deliberately not started. That is what every test here is about.
        _broker = new Broker(_connection, BrokerOptions.Default);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sending_a_request_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.RequestAsync(
            Orders, Billing, Requests.Model, Requests.Request(), T0, cancellationToken: Ct));
    }

    [Fact]
    public async Task Sending_to_a_role_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.RequestRoleAsync(
            Orders, "billing", Requests.Model, Requests.Request(), T0, cancellationToken: Ct));
    }

    [Fact]
    public async Task Clarifying_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ClarifyAsync(
            _exchangeId, Billing, Requests.Model, Requests.Clarify("gross of what?"), T0, Ct));
    }

    [Fact]
    public async Task Sending_a_result_before_start_is_refused()
    {
        // This would close an exchange on the word of a run that has not shown it is the one doing the work.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ResultAsync(
            _exchangeId, Billing, Requests.Model, Requests.Result(), T0, Ct));
    }

    [Fact]
    public async Task Concluding_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ConcludeAsync("pricing", Orders, true, T0, Ct));
    }

    [Fact]
    public async Task Draining_before_start_is_refused()
    {
        // The drain would stamp work collected by a session that has not yet been given back what its
        // predecessor left, which is exactly the redelivery the attach sequence exists to do first.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.DrainInboxAsync(Billing, T0, cancellationToken: Ct));
    }

    [Fact]
    public async Task Ticking_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.TickAsync(T0, Ct));
    }
}
