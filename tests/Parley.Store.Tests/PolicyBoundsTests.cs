using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Pins <see cref="ExchangePolicy"/>'s bounds to the ones storage actually enforces.
/// </summary>
/// <remarks>
/// <para>
/// The same numbers live in two places, which is usually a defect and here is deliberate: the
/// schema is what survives a broker bug and is therefore the real guarantee, while the domain
/// check is what makes a misconfigured deployment fail at start-up naming the field rather than
/// on the first question. Both are worth having.
/// </para>
/// <para>
/// What is not worth having is the two drifting, and the direction that hurts is specific. A
/// domain bound wider than the schema's accepts a policy storage will refuse, so the validation
/// that exists to move the failure earlier moves it nowhere: the deployment starts, and the
/// first question fails at the INSERT with a constraint violation several layers below the
/// mistake. A narrower one merely rejects values that would have worked, which is
/// irritating rather than dangerous.
/// </para>
/// <para>
/// So rather than restating the constants, these ask the engine. Each bound is verified by
/// writing a row at it and a row just outside it, which means the test cannot agree with a
/// schema that has changed underneath it.
/// </para>
/// </remarks>
public sealed class PolicyBoundsTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;

    public PolicyBoundsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Execute("INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders','test','full','offline'),('billing','test','full','offline');");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Writes an exchange carrying the given terms, bypassing every domain check.</summary>
    private void Write(string question, long graceMs, int openPairCap) => Execute(
        "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
      + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
      + "grace_period_ms, opened_utc, expires_utc, question_hash) VALUES "
      + "('billing' || char(31) || 'orders','orders','billing','billing','OPEN',0,3,"
      + openPairCap + ",900000,14400000," + graceMs + ",'" + Stamp + "','" + Stamp + "','"
      + question + "');");

    private static long Ms(TimeSpan value) => (long)value.TotalMilliseconds;

    [Fact]
    public void Storage_accepts_the_minimum_grace_period_the_domain_permits()
    {
        Write("q", Ms(ExchangePolicy.MinimumGracePeriod), ExchangePolicy.Default.OpenPairCap);
    }

    [Fact]
    public void Storage_refuses_anything_below_it()
    {
        // If this ever passes, the domain floor has become the stricter of the two and is
        // rejecting values storage would have taken.
        Assert.Throws<SqliteException>(() =>
            Write("q", Ms(ExchangePolicy.MinimumGracePeriod) - 1, ExchangePolicy.Default.OpenPairCap));
    }

    [Fact]
    public void Storage_accepts_the_maximum_grace_period_the_domain_permits()
    {
        Write("q", Ms(ExchangePolicy.MaximumGracePeriod), ExchangePolicy.Default.OpenPairCap);
    }

    [Fact]
    public void Storage_refuses_anything_above_it()
    {
        // The dangerous direction. Passing here would mean the domain accepts a grace period
        // storage refuses, so validation moves the failure nowhere.
        Assert.Throws<SqliteException>(() =>
            Write("q", Ms(ExchangePolicy.MaximumGracePeriod) + 1, ExchangePolicy.Default.OpenPairCap));
    }

    [Fact]
    public void Storage_accepts_the_maximum_open_pair_cap_the_domain_permits()
    {
        Write("q", Ms(ExchangePolicy.Default.GracePeriod), ExchangePolicy.MaximumOpenPairCap);
    }

    [Fact]
    public void Storage_refuses_a_larger_one()
    {
        Assert.Throws<SqliteException>(() =>
            Write("q", Ms(ExchangePolicy.Default.GracePeriod), ExchangePolicy.MaximumOpenPairCap + 1));
    }

    [Fact]
    public void Storage_refuses_an_open_pair_cap_below_one()
    {
        Assert.Throws<SqliteException>(() =>
            Write("q", Ms(ExchangePolicy.Default.GracePeriod), 0));
    }

    [Fact]
    public async Task Saving_an_exchange_never_rewrites_the_terms_it_was_admitted_under()
    {
        // The property the whole design rests on, and nothing pinned it. ExchangePolicy exists
        // because these values are stamped once and never read from configuration again; if a
        // later edit added a window to SaveAsync's column list, an exchange would start taking
        // whatever the current configuration said, an operator changing a number would re-time
        // work already in flight, and no test would fail.
        var repository = new ExchangeRepository(_connection);
        var admitted = ExchangePolicy.Default with
        {
            WorkingWindow = TimeSpan.FromHours(9),
            GracePeriod = TimeSpan.FromHours(7),
            ClarifyDepthCap = 2
        };

        var opened = await repository.TryOpenAsync(
            AgentName.Create("orders"), AgentName.Create("billing"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "q", admitted, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(opened.Succeeded, $"expected admission, got {opened.Rejection}");

        // A transition and a save, which is every write path an exchange has.
        var exchange = opened.Exchange!;
        Assert.True(exchange.Clarify(
            AgentName.Create("billing"),
            new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero)).Succeeded);
        Assert.True(await repository.SaveAsync(
            exchange, cancellationToken: TestContext.Current.CancellationToken));

        var reloaded = await repository.GetAsync(
            exchange.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(reloaded);
        Assert.Equal(admitted.DeliveryWindow, reloaded.DeliveryWindow);
        Assert.Equal(admitted.WorkingWindow, reloaded.WorkingWindow);
        Assert.Equal(admitted.GracePeriod, reloaded.GracePeriod);
        Assert.Equal(admitted.ClarifyDepthCap, reloaded.ClarifyDepthCap);
    }

    [Fact]
    public void The_default_policy_is_writable()
    {
        // The case that matters most and would be easy to omit: a deployment that configures
        // nothing must be able to open an exchange.
        ExchangePolicy.Default.Validate();

        Write("q", Ms(ExchangePolicy.Default.GracePeriod), ExchangePolicy.Default.OpenPairCap);
    }
}
