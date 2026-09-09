using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The cap at the boundary, where a character is not a character.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two counting systems meet here and they do not agree.</b> The edge cuts on
/// <c>string.Length</c>, which counts UTF-16 units; the schema's <c>CHECK</c> uses SQLite's
/// <c>length()</c>, which counts characters. For anything outside the basic plane — an emoji, most
/// obviously — C# counts two where SQLite counts one.
/// </para>
/// <para>
/// That direction is the safe one: the edge cuts sooner than the schema requires, so the guard can
/// never hand the store something it will refuse. The unsafe thing is different and is what these
/// probe: a cut taken at a fixed offset can land <i>between</i> the two halves of a surrogate pair,
/// producing a string with a lone surrogate in it. That is not a character, it is not what the
/// agent said, and it is the kind of value that breaks whatever reads it next rather than here.
/// </para>
/// </remarks>
public sealed class InstructionCapBoundaryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One astral-plane character: two UTF-16 units, one SQLite character.</summary>
    private const string Pair = "\U0001F600";

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;

    public InstructionCapBoundaryTests()
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
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // **The probe.** A reply built so that the boundary falls inside a pair -- and then walked
        // for a lone surrogate, which is what a fixed-offset slice leaves behind.
        //
        // Driven across a window of lengths rather than at one, because exactly which offset lands
        // mid-pair depends on the marker's length, and a test that hard-coded the alignment would
        // stop testing anything the day somebody reworded the marker.
        for (var padding = 0; padding < 8; padding++)
        {
            var said = new string('x', padding)
                + string.Concat(Enumerable.Repeat(Pair, MessageLimits.MaxPayloadCharacters));

            var fitted = MessageLimits.Fit(said);

            Assert.True(
                fitted.Length <= MessageLimits.MaxPayloadCharacters,
                $"padding {padding} produced {fitted.Length} units");

            // A lone surrogate is a unit that is one half of a pair with nothing on its other side.
            for (var at = 0; at < fitted.Length; at++)
            {
                if (char.IsHighSurrogate(fitted[at]))
                {
                    Assert.True(
                        at + 1 < fitted.Length && char.IsLowSurrogate(fitted[at + 1]),
                        $"padding {padding} left a high surrogate at {at} with nothing after it");
                }
                else if (char.IsLowSurrogate(fitted[at]))
                {
                    Assert.True(
                        at > 0 && char.IsHighSurrogate(fitted[at - 1]),
                        $"padding {padding} left a low surrogate at {at} with nothing before it");
                }
            }
        }
    }

    [Fact]
    public async Task What_the_cut_produces_is_accepted_by_the_schema_even_at_the_worst_alignment()
    {
        // The two counting systems, meeting for real. The edge counts UTF-16 units and the CHECK
        // counts characters, so this asserts the direction of the inequality rather than assuming
        // it -- a guard that cut on the wrong one would refuse its own output and turn every
        // oversized reply into a lost one.
        for (var padding = 0; padding < 4; padding++)
        {
            var id = await _instructions.RecordAsync(Orders, $"padding {padding}", T0, Ct);

            var said = new string('x', padding)
                + string.Concat(Enumerable.Repeat(Pair, MessageLimits.MaxPayloadCharacters));

            Assert.True(
                await _instructions.SettleAsync(
                    id, InstructionOutcome.Taken, MessageLimits.Fit(said), T0.AddMinutes(1), Ct),
                $"the store refused what the guard produced, at padding {padding}");
        }
    }

    [Fact]
    public async Task An_astral_reply_within_the_cap_is_stored_exactly_as_the_agent_said_it()
    {
        // Non-vacuity, and the property that matters most: a reply that fits must not be altered.
        // A guard that normalised or re-encoded would change an agent's words for no reason.
        var said = string.Concat(Enumerable.Repeat(Pair, 100)) + " done";
        var id = await _instructions.RecordAsync(Orders, "have a look", T0, Ct);

        Assert.Equal(said, MessageLimits.Fit(said));
        Assert.True(await _instructions.SettleAsync(
            id, InstructionOutcome.Taken, said, T0.AddMinutes(1), Ct));

        var read = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(said, read.Reply);
    }

    [Fact]
    public async Task The_schema_counts_characters_and_the_guard_counts_units()
    {
        // Stated as a test because it is the assumption the whole arrangement rests on, and it is
        // the kind of assumption that is true until somebody changes a collation. SQLite's length()
        // over astral text is half what string.Length reports, so a reply of 8000 pairs is 16,000
        // units and 8,000 characters -- refused by the guard, accepted by the CHECK. The guard
        // being the stricter of the two is what makes it safe.
        var pairs = string.Concat(Enumerable.Repeat(Pair, 4000));

        Assert.Equal(8000, pairs.Length);

        using var command = _connection.CreateCommand();

        command.CommandText = "SELECT length(@Text);";
        _ = command.Parameters.AddWithValue("@Text", pairs);

        var counted = Convert.ToInt64(await command.ExecuteScalarAsync(Ct), provider: null);

        Assert.Equal(4000, counted);
        Assert.True(
            pairs.Length >= counted,
            "the guard would be the looser of the two, which is the unsafe direction");
    }
}
