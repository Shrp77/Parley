using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the item-id bound the schema holds is the one the code states.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageLimits.LongestItemId"/> states the longest id a Result may record, and the schema holds the same
/// number in <c>result_items</c>. A Request carries no items (§5.1), so only the Result side is storage's.
/// </para>
/// </remarks>
[Integration]
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
    public async Task A_result_is_held_to_the_bound_the_code_states()
    {
        var ct = TestContext.Current.CancellationToken;

        var (fits, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("fits", Longest), cancellationToken: ct);
        await Work.CloseAsync(_connection, fits, T0.AddMinutes(1), payload: Work.Result((Longest, "answered")), cancellationToken: ct);

        // Storage does not compare a Result with its Request, so a Result can try an id no Request could carry.
        var (over, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("over", "q1"), cancellationToken: ct);

        await Assert.ThrowsAsync<SqliteException>(() =>
            Work.CloseAsync(_connection, over, T0.AddMinutes(2), payload: Work.Result((Over, "answered")), cancellationToken: ct));
    }
}
