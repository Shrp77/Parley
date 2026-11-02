using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Pins <see cref="ExchangePolicy"/>'s bounds to the ones storage actually enforces.
/// </summary>
/// <remarks>
/// <para>
/// The same numbers live in two places, which is usually a defect and here is deliberate: the schema is what
/// survives a broker bug and is therefore the real guarantee, while the domain check is what makes a
/// misconfigured deployment fail at start-up naming the field rather than on the first Request.
/// </para>
/// <para>
/// What is not worth having is the two drifting, and the direction that hurts is specific. A domain bound
/// wider than the schema's accepts a policy storage will refuse, so the first Request fails at the INSERT
/// several layers below the mistake. So rather than restating the constants, these ask the engine: each bound
/// is verified by writing a row at it and a row just outside it.
/// </para>
/// </remarks>
[Integration]
public sealed class PolicyBoundsTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;

    public PolicyBoundsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));
        Execute($"INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '{Stamp}');");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Writes an exchange carrying the given terms, bypassing every domain check.</summary>
    private void Write(long uncollectedMs, long stalledMs, int openPairCap, string hash = "q") => Execute(
        "INSERT INTO exchanges (pair_key, requester, receiver, owed_by, state, open_pair_cap, "
      + "uncollected_flag_ms, stalled_flag_ms, opened_utc, request_hash, conversation_id) VALUES "
      + "('billing@test' || char(31) || 'orders@test','orders@test','billing@test','billing@test','OPEN',"
      + openPairCap + "," + uncollectedMs + "," + stalledMs + ",'" + Stamp + "','" + hash + "','pricing');");

    private static long Ms(TimeSpan value) => (long)value.TotalMilliseconds;

    private static readonly long Uncollected = Ms(ExchangePolicy.Default.UncollectedAfter);
    private static readonly long Stalled = Ms(ExchangePolicy.Default.StalledAfter);

    [Fact]
    public void Storage_accepts_the_smallest_thresholds_the_domain_permits()
    {
        // The domain floor is one millisecond; storage must take it, or the floor rejects nothing storage would.
        Write(1, 1, ExchangePolicy.Default.OpenPairCap);
    }

    [Fact]
    public void Storage_refuses_a_threshold_of_zero()
    {
        // The dangerous direction: a threshold of zero flags every Request the instant it is sent.
        Assert.Throws<SqliteException>(() => Write(0, Stalled, ExchangePolicy.Default.OpenPairCap));
        Assert.Throws<SqliteException>(() => Write(Uncollected, 0, ExchangePolicy.Default.OpenPairCap));
    }

    [Fact]
    public void Storage_accepts_the_maximum_open_pair_cap_the_domain_permits()
    {
        Write(Uncollected, Stalled, ExchangePolicy.MaximumOpenPairCap);
    }

    [Fact]
    public void Storage_refuses_a_larger_one()
    {
        Assert.Throws<SqliteException>(() => Write(Uncollected, Stalled, ExchangePolicy.MaximumOpenPairCap + 1));
    }

    [Fact]
    public void Storage_refuses_an_open_pair_cap_below_one()
    {
        Assert.Throws<SqliteException>(() => Write(Uncollected, Stalled, 0));
    }

    [Fact]
    public async Task Saving_an_exchange_never_rewrites_the_terms_it_was_admitted_under()
    {
        // The property ExchangePolicy exists for: stamped once, never read from configuration again. If a later
        // edit added a threshold to SaveAsync's column list, an operator changing a number would re-flag work
        // already in flight, and no other test would fail.
        var repository = new ExchangeRepository(_connection);
        var orders = AgentName.Create("orders@test");
        var billing = AgentName.Create("billing@test");
        var admitted = ExchangePolicy.Default with
        {
            UncollectedAfter = TimeSpan.FromMinutes(7),
            StalledAfter = TimeSpan.FromHours(9),
            ClarifyDepthCap = 2
        };

        var opened = await repository.TryOpenAsync(
            orders, billing, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "q", "pricing", admitted,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(opened.Succeeded, $"expected admission, got {opened.Rejection}");

        var exchange = opened.Exchange!;
        Assert.True(exchange.Clarify(billing, new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero)).Succeeded);
        Assert.True(await repository.SaveAsync(exchange, cancellationToken: TestContext.Current.CancellationToken));

        var reloaded = await repository.GetAsync(exchange.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(admitted.UncollectedAfter, reloaded!.UncollectedAfter);
        Assert.Equal(admitted.StalledAfter, reloaded.StalledAfter);
        Assert.Equal(admitted.ClarifyDepthCap, reloaded.ClarifyDepthCap);
    }

    [Fact]
    public void The_default_policy_is_writable()
    {
        // The case that matters most and would be easy to omit: a deployment that configures nothing must be
        // able to open an exchange.
        ExchangePolicy.Default.Validate();

        Write(Uncollected, Stalled, ExchangePolicy.Default.OpenPairCap);
    }
}
