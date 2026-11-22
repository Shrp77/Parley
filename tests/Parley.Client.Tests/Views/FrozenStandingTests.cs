using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// That a person's freeze on an agent reads as the one of three phrases it is, that a surface asking whether a row is
/// frozen is told so for all three and for nothing else, and that a retired agent never reads as frozen (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// A surface offers Unfreeze on a row by asking <see cref="Standing.IsFrozen"/>. A phrase left out of it is a frozen agent
/// offered Freeze, which does nothing, with no way shown to unfreeze it.
/// </remarks>
[Unit]
public sealed class FrozenStandingTests
{
    private const string Machine = "test";
    private const string SeatAdapter = "parley-console";

    private static readonly DateTimeOffset Then = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every phrase an agent's row can carry that is declared as a constant, by its name.</summary>
    public static TheoryData<string, string> EveryPhrase()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in typeof(Standing).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is string phrase)
            {
                data.Add(field.Name, phrase);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPhrase))]
    public void Only_the_three_frozen_phrases_read_as_frozen(string name, string phrase)
    {
        var frozen = name is nameof(Standing.Frozen) or nameof(Standing.FrozenKept) or nameof(Standing.Freezing);

        Assert.Equal(frozen, Standing.IsFrozen(phrase));
    }

    [Fact]
    public void The_three_frozen_phrases_are_declared_and_differ()
    {
        // The theory above reads the constants by reflection, so it would pass with one of them gone. This is what it rests on.
        string[] phrases = [Standing.Frozen, Standing.FrozenKept, Standing.Freezing];

        Assert.Equal(3, phrases.Distinct(StringComparer.Ordinal).Count());
        Assert.All(phrases, phrase => Assert.True(Standing.IsFrozen(phrase), phrase));
        Assert.False(Standing.IsFrozen("frozen"));
        Assert.False(Standing.IsFrozen(string.Empty));
    }

    [Theory]
    [InlineData(AgentPresence.Stranded, false, Standing.Frozen)]
    [InlineData(AgentPresence.Startable, false, Standing.Frozen)]
    [InlineData(AgentPresence.Stranded, true, Standing.FrozenKept)]
    [InlineData(AgentPresence.Startable, true, Standing.FrozenKept)]
    [InlineData(AgentPresence.Running, false, Standing.Freezing)]
    [InlineData(AgentPresence.Running, true, Standing.Freezing)]
    public void A_frozen_agent_reads_as_stopping_while_it_is_attached_and_then_as_kept_or_not(
        AgentPresence presence, bool kept, string expected)
    {
        var row = Assert.Single(Standing.Join([Agent(presence, AgentStatus.Active, Then, kept)], Roles(), SeatAdapter, Machine));

        Assert.Equal(expected, row.Standing);
        Assert.True(Standing.IsFrozen(row.Standing));
    }

    [Theory]
    [InlineData(AgentPresence.Stranded, false)]
    [InlineData(AgentPresence.Stranded, true)]
    [InlineData(AgentPresence.Running, false)]
    public void A_retired_agent_never_reads_as_frozen_whatever_freeze_it_carries(AgentPresence presence, bool kept)
    {
        // Retiring keeps the freeze on the row, so a reinstated agent is still frozen; while retired it is said to be retired alone.
        var row = Assert.Single(Standing.Join([Agent(presence, AgentStatus.Retired, Then, kept)], Roles(), SeatAdapter, Machine));

        Assert.Equal(Standing.Retired, row.Standing);
        Assert.False(Standing.IsFrozen(row.Standing));
        Assert.True(row.Retired);
    }

    [Theory]
    [InlineData(AgentPresence.Stranded)]
    [InlineData(AgentPresence.Startable)]
    [InlineData(AgentPresence.Running)]
    public void An_agent_nobody_froze_never_reads_as_frozen(AgentPresence presence)
    {
        var row = Assert.Single(Standing.Join([Agent(presence, AgentStatus.Active, null, false)], Roles(), SeatAdapter, Machine));

        Assert.False(Standing.IsFrozen(row.Standing));
    }

    [Fact]
    public void A_freeze_outranks_what_its_turns_and_its_process_say()
    {
        // Working, open outside Parley and left by a node that is gone each have a phrase of their own; a freeze is said first.
        var working = Agent(AgentPresence.Running, AgentStatus.Active, Then, false) with
        {
            Turns = new TurnStanding(true, Then, null, true, 0),
        };
        var outside = Agent(AgentPresence.Running, AgentStatus.Active, Then, false) with { OpenOutside = true };
        var left = Agent(AgentPresence.Stranded, AgentStatus.Active, Then, true) with { Unsupervised = true };

        Assert.Equal(Standing.Freezing, Assert.Single(Standing.Join([working], Roles(), SeatAdapter, Machine)).Standing);
        Assert.Equal(Standing.Freezing, Assert.Single(Standing.Join([outside], Roles(), SeatAdapter, Machine)).Standing);
        Assert.Equal(Standing.FrozenKept, Assert.Single(Standing.Join([left], Roles(), SeatAdapter, Machine)).Standing);
    }

    private static HashSet<string> Roles() => new(StringComparer.Ordinal) { "orders" };

    private static AgentRecord Agent(AgentPresence presence, AgentStatus status, DateTimeOffset? frozen, bool kept) =>
        new(AgentName.Create("orders@" + Machine), "claude-code", AgentTier.Pull, null, null, null,
            presence == AgentPresence.Running, status, Presence: presence, FrozenUtc: frozen, KeepFrozen: kept);
}
