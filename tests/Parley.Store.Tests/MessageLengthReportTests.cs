using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the report of how long messages have been says what was recorded (SPEC.md §10.2, <c>message_lengths</c>).
/// </summary>
[Integration]
public sealed class MessageLengthReportTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public MessageLengthReportTests() => Work.Register(_connection, Orders, Billing);

    public void Dispose() => _connection.Dispose();

    private Task<MessageLengthReport> Read() =>
        new MessageLengthRepository(_connection).ReadAsync(TestContext.Current.CancellationToken);

    private void Record(long position, string type, string from, string to, int characters, string conversation = "c") =>
        _connection.Execute(
            """
            INSERT INTO message_lengths (message_id, conversation_id, position, type, from_agent, to_agent, characters, created_utc)
            VALUES ((SELECT coalesce(max(message_id), 0) + 1 FROM message_lengths), @conversation, @position, @type, @from, @to,
                    @characters, '2026-01-01T00:00:00.0000000Z');
            """,
            new { conversation, position, type, from, to, characters });

    [Fact]
    public async Task With_nothing_recorded_every_count_is_zero()
    {
        var report = await Read();

        Assert.Equal(new MessageLengthGroup("all", 0, 0, 0, 0), report.Overall);
        Assert.Empty(report.ByType);
        Assert.Empty(report.ByPosition);
        Assert.Empty(report.ByAgent);
    }

    [Fact]
    public async Task The_median_longest_and_soft_limit_count_are_worked_out_from_the_records()
    {
        Record(1, "request", Orders.Value, Billing.Value, 100);
        Record(2, "result", Billing.Value, Orders.Value, 300);
        Record(3, "clarify", Orders.Value, Billing.Value, MessageLimits.SoftPayloadCharacters);
        Record(4, "result", Billing.Value, Orders.Value, MessageLimits.SoftPayloadCharacters + 1);
        Record(5, "result", Billing.Value, Orders.Value, 500_000);

        var report = await Read();

        Assert.Equal(new MessageLengthGroup("all", 5, MessageLimits.SoftPayloadCharacters, 500_000, 2), report.Overall);
        Assert.Equal(new MessageLengthGroup("request", 1, 100, 100, 0), report.ByType.Single(t => t.Name == "request"));
        Assert.Equal(new MessageLengthGroup("result", 3, 16_001, 500_000, 2), report.ByType.Single(t => t.Name == "result"));
    }

    [Fact]
    public async Task Positions_come_back_in_order_so_a_drift_shows()
    {
        Record(150, "clarify", Orders.Value, Billing.Value, 9000);
        Record(50, "clarify", Orders.Value, Billing.Value, 7000);
        Record(5, "clarify", Orders.Value, Billing.Value, 100);
        Record(6, "clarify", Orders.Value, Billing.Value, 200);
        Record(20, "clarify", Orders.Value, Billing.Value, 400);
        Record(21, "clarify", Orders.Value, Billing.Value, 6000);
        Record(100, "clarify", Orders.Value, Billing.Value, 8000);
        Record(1, "clarify", Orders.Value, Billing.Value, 50);

        var report = await Read();

        Assert.Equal(["1 to 5", "6 to 20", "21 to 100", "101 and later"], report.ByPosition.Select(p => p.Name));
        Assert.Equal([2L, 2L, 3L, 1L], report.ByPosition.Select(p => p.Messages));
        Assert.Equal([50, 200, 7000, 9000], report.ByPosition.Select(p => p.Median));
    }

    [Fact]
    public async Task Each_agent_counts_what_it_sent_and_what_it_was_sent()
    {
        Record(1, "request", Orders.Value, Billing.Value, 100);
        Record(2, "result", Billing.Value, Orders.Value, 20_000);
        Record(3, "request", Orders.Value, Billing.Value, 200);

        var report = await Read();

        Assert.Equal(new AgentMessageLengths(Billing, 1, 2, 20_000, 1), report.ByAgent.Single(a => a.Agent == Billing));
        Assert.Equal(new AgentMessageLengths(Orders, 2, 1, 20_000, 1), report.ByAgent.Single(a => a.Agent == Orders));
    }

    [Fact]
    public async Task A_renamed_agent_keeps_its_record_and_a_purged_one_takes_it_along()
    {
        var token = TestContext.Current.CancellationToken;
        var agents = new AgentRepository(_connection);

        Record(1, "request", Orders.Value, Billing.Value, 100);

        var renamed = AgentName.Create("sales@test");

        await agents.RenameAsync(Orders, renamed, cancellationToken: token);

        Assert.Equal(renamed.Value, _connection.ExecuteScalar<string>("SELECT from_agent FROM message_lengths;"));

        await agents.PurgeAsync(Billing, T0, cancellationToken: token);

        Assert.Equal(0, _connection.ExecuteScalar<long>("SELECT count(*) FROM message_lengths;"));
    }
}
