using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a freeze an earlier run made and kept across a start is still a freeze, so freezing it again in a later run changes
/// nothing: not its instant, not its run, not the person's choice to keep it (SPEC.md §14).
/// </summary>
/// <remarks>
/// Review, lens 3 on fc26cb8a: <see cref="AgentRepository.FreezeAsync"/> lets a new freeze take over a lapsed one, and the
/// guard that a kept freeze is not lapsed passed every test with it removed. Without it a person's Freeze all over a
/// project rewrote a kept freeze as an ordinary one, which the next start then lifted, and counted it as changed.
/// </remarks>
[Integration]
public sealed class AKeptFreezeIsNotFrozenAgainTests : IDisposable
{
    private const string Earlier = "11111111-1111-1111-1111-111111111111";
    private const string Now = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTimeOffset Then = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public AKeptFreezeIsNotFrozenAgainTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test','test','full','active');
            """;
        _ = command.ExecuteNonQuery();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Freezing_a_kept_freeze_again_in_a_later_run_changes_nothing(bool keepAgain)
    {
        var orders = AgentName.Create("orders@test");
        _ = await _agents.FreezeAsync(orders, Then, Earlier, keepFrozen: true, cancellationToken: Ct);

        Assert.Equal(0, await _agents.FreezeAsync(orders, Then.AddHours(1), Now, keepAgain, Ct));

        var row = (await _agents.GetAllAsync(Ct)).Single(agent => agent.Name == orders);

        Assert.True(row.KeepFrozen, "the person's choice to keep the freeze was rewritten");
        Assert.Equal(Then, row.FrozenUtc);
        Assert.Empty(await _agents.LapsedAsync(Now, Ct));
    }
}
