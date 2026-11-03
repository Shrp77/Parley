using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>That an agent sending while its exchange moves loses nothing and splits nothing (SPEC.md §5.0).</summary>
[Integration]
public sealed class AMoveRacingASendTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-move-race-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Every_message_ends_in_the_conversation_its_exchange_is_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "parley.db");

        using var sending = ParleyDatabase.OpenAndMigrate(path);
        using var moving = ParleyDatabase.Open(path);

        sending.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00')
                ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
              ('orders@test', 'test', 'full', 'active'),
              ('billing@test', 'test', 'full', 'active');
            """);

        var broker = new Broker(sending, BrokerOptions.Default);
        var console = new OperatorConsole(moving);

        await broker.StartAsync(Orders, T0, ct);
        await broker.StartAsync(Billing, T0, ct);

        const int Rounds = 40;

        for (var round = 0; round < Rounds; round++)
        {
            var at = T0.AddMinutes(round);
            var opened = await broker.RequestAsync(Orders, Billing, "model-a", $$"""{"ask":"question {{round}}"}""", at, $"from-{round}", ct);
            var exchange = opened.Exchange!.Id;

            using var barrier = new Barrier(2);

            var clarify = Task.Run(
                async () =>
                {
                    barrier.SignalAndWait(ct);

                    return await broker.ClarifyAsync(exchange, Billing, "model-a", $$"""{"text":"which {{round}}?"}""", at.AddSeconds(1), ct);
                },
                ct);

            var move = Task.Run(
                async () =>
                {
                    barrier.SignalAndWait(ct);

                    return await console.MoveExchangesAsync($"from-{round}", $"to-{round}", true, [exchange], at.AddSeconds(1), ct);
                },
                ct);

            Assert.True((await clarify).Succeeded);
            Assert.True((await move).Moved);
        }

        Assert.Equal(Rounds * 2, sending.ExecuteScalar<int>("SELECT count(*) FROM messages;"));
        Assert.Equal(0, sending.ExecuteScalar<int>(
            "SELECT count(*) FROM messages m JOIN exchanges e ON e.id = m.exchange_id WHERE m.conversation_id <> e.conversation_id;"));
        Assert.Equal(0, sending.ExecuteScalar<int>("SELECT count(*) FROM messages WHERE conversation_id LIKE 'from-%';"));
        Assert.Empty(sending.Query("PRAGMA foreign_key_check;"));
    }
}
