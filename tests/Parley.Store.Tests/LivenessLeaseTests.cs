using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The lease that says whether an agent is still attached.
/// </summary>
/// <remarks>
/// <para>
/// Two halves that have to agree: <see cref="AgentRepository.MarkLiveAsync"/> writes the
/// instant, and <see cref="AgentRepository.WasSeenSinceAsync"/> compares against it. The
/// comparison is a string comparison, correct only because both go through the same
/// round-trippable format — so a divergence there would not fail, it would answer wrongly, and
/// the attach sequence of SPEC.md §14 would release live work on the strength of it.
/// </para>
/// <para>
/// Which is why the boundary is tested from both sides rather than the query alone.
/// </para>
/// </remarks>
public sealed class LivenessLeaseTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public LivenessLeaseTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
        _agents.UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, null,
                AgentStatus.Registered),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private Task<bool> SeenAt(DateTimeOffset now) =>
        _agents.WasSeenSinceAsync(Orders, now - Lease,
            cancellationToken: TestContext.Current.CancellationToken);

    private Task MarkLive(DateTimeOffset at) =>
        _agents.MarkLiveAsync(Orders, null, at, TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_agent_that_has_never_attached_is_not_within_the_lease()
    {
        // Registered from a manifest and never launched: last_seen_utc is null, and a null must
        // read as "gone" rather than as "recent". Reading it the other way would make every
        // manifest entry permanently attached, and no attach would ever release anything.
        Assert.False(await SeenAt(T0));
    }

    [Fact]
    public async Task An_agent_marked_live_is_within_the_lease()
    {
        await MarkLive(T0);

        Assert.True(await SeenAt(T0));
        Assert.True(await SeenAt(T0.AddMinutes(4)));
    }

    [Fact]
    public async Task The_lease_lapses_once_nothing_renews_it()
    {
        await MarkLive(T0);

        Assert.False(await SeenAt(T0.AddMinutes(6)));
        Assert.False(await SeenAt(T0.AddHours(9)));
    }

    [Fact]
    public async Task Renewing_extends_it_from_the_renewal()
    {
        // The heartbeat. A session open all afternoon has not been heard from since it attached
        // unless the sweep says so, and without this it would look departed after five minutes.
        await MarkLive(T0);

        for (var minute = 1; minute <= 60; minute++)
        {
            await MarkLive(T0.AddMinutes(minute));
            Assert.True(await SeenAt(T0.AddMinutes(minute)));
        }

        Assert.True(await SeenAt(T0.AddMinutes(62)));
        Assert.False(await SeenAt(T0.AddMinutes(70)));
    }

    [Fact]
    public async Task Going_offline_does_not_by_itself_end_the_lease()
    {
        // Deliberate, and the reason the query reads the timestamp rather than the status. A
        // clean shutdown records that *one* session stopped, which says nothing about another
        // one still running — and with two windows open on a repository, believing it would
        // release the work the surviving session is doing.
        await MarkLive(T0);
        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);

        Assert.True(await SeenAt(T0.AddMinutes(1)));
        Assert.False(await SeenAt(T0.AddMinutes(6)));
    }

    [Fact]
    public async Task The_lease_is_read_across_the_boundary_that_a_string_comparison_makes_fragile()
    {
        // Timestamps are compared as text, so an hour that formats without a leading zero, or a
        // year boundary, or a fractional-second difference in how the two sides render the same
        // instant, would answer wrongly rather than fail. Each of these straddles one.
        DateTimeOffset[] instants =
        [
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2025, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new(2026, 3, 1, 9, 5, 0, TimeSpan.Zero),
            new(2026, 10, 9, 8, 7, 6, TimeSpan.Zero)
        ];

        foreach (var instant in instants)
        {
            await MarkLive(instant);

            Assert.True(await SeenAt(instant), $"{instant:O} should be within its own lease");
            Assert.True(await SeenAt(instant.AddMinutes(4)), $"{instant:O} at four minutes");
            Assert.False(await SeenAt(instant.AddMinutes(6)), $"{instant:O} at six minutes");
        }
    }
}
