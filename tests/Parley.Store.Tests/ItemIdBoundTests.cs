using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the item-id bound the schema holds is the one the code states.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageLimits.LongestItemId"/> decides which of a Request's ids a Result is held to, and the schema holds
/// the same number twice: once refusing a Request carrying a longer id, once refusing a Result recording one. If the
/// constant moved past the schema, the broker would require ids storage then refused to record, and a Request would
/// be unanswerable again — the defect the review gate found through a migrated 0.4 question (lens 1 run 1.15). The
/// constant was written with this check, the way <c>The_cap_the_schema_enforces_is_the_one_the_code_states</c> binds
/// the message cap.
/// </para>
/// </remarks>
public sealed class ItemIdBoundTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string Longest = new('r', MessageLimits.LongestItemId);
    private static readonly string Over = new('s', MessageLimits.LongestItemId + 1);

    private readonly SqliteConnection _connection;

    public ItemIdBoundTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_request_is_held_to_the_bound_the_code_states()
    {
        var ct = TestContext.Current.CancellationToken;

        await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("longest", Longest), cancellationToken: ct);

        await Assert.ThrowsAsync<SqliteException>(() =>
            Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("over", Over), cancellationToken: ct));
    }

    [Fact]
    public async Task A_result_is_held_to_the_bound_the_code_states()
    {
        var ct = TestContext.Current.CancellationToken;

        var (fits, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("fits", Longest), cancellationToken: ct);
        await Work.CloseAsync(_connection, fits, T0.AddMinutes(1), payload: Work.Result((Longest, "answered")), cancellationToken: ct);

        // Storage does not compare a Result with its Request, so a Result can try an id no Request could carry. That is
        // the refusal a migrated question used to meet.
        var (over, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("over", "q1"), cancellationToken: ct);

        await Assert.ThrowsAsync<SqliteException>(() =>
            Work.CloseAsync(_connection, over, T0.AddMinutes(2), payload: Work.Result((Over, "answered")), cancellationToken: ct));
    }
}
