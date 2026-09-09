using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The bound on an agent's reply, which the review gate found had none.
/// </summary>
/// <remarks>
/// <para>
/// <c>instructions.text</c> has carried rule 8.5's cap since 003 and <c>instructions.reply</c>
/// carried nothing, so anything able to reach the broker and claim to be the node reporting — which
/// SPEC.md §16 says is anything on the network — could write an arbitrarily large blob into the
/// operator's own record.
/// </para>
/// <para>
/// <b>The CHECK is the enforcement and <see cref="MessageLimits.Fit"/> is what keeps writers under
/// it.</b> §9.3's argument: application code can route around a check it makes itself, and this
/// table has already acquired a second writer once.
/// </para>
/// </remarks>
public sealed class InstructionReplyCapTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;

    public InstructionReplyCapTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);

        using var command = _connection.CreateCommand();

        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) "
            + "VALUES ('orders@test','test','full','active');";

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_store_refuses_a_reply_over_the_cap()
    {
        // The invariant where §9.3 says it belongs. A caller that forgot to fit gets a failed write
        // rather than an oversized row.
        var id = await _instructions.RecordAsync(Orders, "have a look", T0, Ct);

        await Assert.ThrowsAnyAsync<SqliteException>(async () =>
            await _instructions.SettleAsync(
                id,
                InstructionOutcome.Taken,
                new string('x', MessageLimits.MaxPayloadCharacters + 1),
                T0.AddMinutes(1),
                Ct));
    }

    [Fact]
    public async Task A_reply_exactly_at_the_cap_is_accepted()
    {
        // Non-vacuity for the test above, and the boundary itself: an off-by-one here would refuse
        // an answer that fits, which is a loss dressed as a guard.
        var id = await _instructions.RecordAsync(Orders, "have a look", T0, Ct);

        Assert.True(await _instructions.SettleAsync(
            id,
            InstructionOutcome.Taken,
            new string('x', MessageLimits.MaxPayloadCharacters),
            T0.AddMinutes(1),
            Ct));
    }

    [Fact]
    public void Fitting_leaves_anything_within_the_cap_exactly_as_it_was()
    {
        // An agent's words must not be altered for being short enough. A guard that rewrote every
        // reply would be a worse problem than the one it was added for.
        const string said = "I changed three files and the tests pass";

        Assert.Equal(said, MessageLimits.Fit(said));
        Assert.Equal(string.Empty, MessageLimits.Fit(null));
    }

    [Fact]
    public void Fitting_cuts_to_exactly_the_cap_and_says_where()
    {
        var fitted = MessageLimits.Fit(new string('x', 200_000));

        Assert.Equal(MessageLimits.MaxPayloadCharacters, fitted.Length);
        Assert.EndsWith(MessageLimits.Truncated, fitted, StringComparison.Ordinal);
        Assert.StartsWith("xxx", fitted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_fitting_produces_is_what_the_store_accepts()
    {
        // The two halves have to agree at the boundary or the guard refuses its own output -- which
        // would turn every oversized reply into a lost one, the failure the readback exists to end.
        var id = await _instructions.RecordAsync(Orders, "have a look", T0, Ct);

        Assert.True(await _instructions.SettleAsync(
            id,
            InstructionOutcome.Taken,
            MessageLimits.Fit(new string('x', 200_000)),
            T0.AddMinutes(1),
            Ct));

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.EndsWith(MessageLimits.Truncated, read.Reply!, StringComparison.Ordinal);
    }
}
