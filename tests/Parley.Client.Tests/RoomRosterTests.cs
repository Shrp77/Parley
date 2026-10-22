using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Who a conversation's room offers beside the agents that have taken part (SPEC.md §5.0).
/// </summary>
public sealed class RoomRosterTests
{
    private static AgentName Name(string value) => AgentName.Create(value);

    private static AgentRecord Agent(
        string name,
        AgentStatus status = AgentStatus.Active,
        string adapter = "claude-code") =>
        new(
            Name(name),
            adapter,
            AgentTier.Full,
            null,
            null,
            null,
            false,
            status,
            null,
            AgentPresence.Stranded,
            null,
            null,
            default!,
            false);

    private static readonly IReadOnlyList<AgentRecord> Project =
    [
        Agent("orders@m"),
        Agent("billing@m"),
        Agent("inventory@m"),
        Agent("ledger@m"),
        Agent("human-user@m", adapter: PersonSeat.Adapter),
        Agent("parley-facilitator@m", adapter: "parley-facilitator"),
    ];

    [Fact]
    public void Every_active_agent_that_has_not_taken_part_is_offered_in_name_order()
    {
        var offered = RoomRoster.NotInIt(Project, [Name("orders@m"), Name("billing@m")], me: Name("human-user@m"));

        Assert.Equal(["inventory@m", "ledger@m"], offered.Select(a => a.Value));
    }

    [Fact]
    public void The_person_a_seat_and_Parley_are_never_offered_as_agents()
    {
        var offered = RoomRoster.NotInIt(Project, [], me: null);

        // Not the person under any name, nor the adapter that marks a seat, nor the facilitator: this is the
        // drawn list's rule.
        Assert.DoesNotContain(offered, a => a.Value.StartsWith("human-user", StringComparison.Ordinal));
        Assert.DoesNotContain(offered, a => a.Value.StartsWith("parley-facilitator", StringComparison.Ordinal));
        Assert.Equal(4, offered.Count);
    }

    [Fact]
    public void A_retired_agent_is_not_offered_because_nothing_routes_to_it()
    {
        var registry = new[] { Agent("orders@m"), Agent("billing@m", AgentStatus.Retired) };

        Assert.Equal(["orders@m"], RoomRoster.NotInIt(registry, [], null).Select(a => a.Value));
    }

    [Fact]
    public void A_participant_is_never_listed_a_second_time_even_when_it_is_retired()
    {
        var registry = new[] { Agent("orders@m", AgentStatus.Retired), Agent("billing@m") };

        var offered = RoomRoster.NotInIt(registry, [Name("orders@m")], null);

        Assert.Equal(["billing@m"], offered.Select(a => a.Value));
    }

    [Fact]
    public void An_agent_that_joined_after_the_conversation_began_is_offered_on_the_next_read()
    {
        var before = RoomRoster.NotInIt(Project.Take(2).ToList(), [Name("orders@m")], null);
        var after = RoomRoster.NotInIt(Project.Take(3).ToList(), [Name("orders@m")], null);

        Assert.Equal(["billing@m"], before.Select(a => a.Value));
        Assert.Equal(["billing@m", "inventory@m"], after.Select(a => a.Value));
    }

    [Fact]
    public void A_registry_that_could_not_be_read_offers_nobody_rather_than_a_guess()
    {
        Assert.Empty(RoomRoster.NotInIt(null, [Name("orders@m")], null));
    }

    [Fact]
    public void The_participant_mark_is_a_character_and_the_legend_says_what_it_means()
    {
        Assert.StartsWith(RoomRoster.ParticipantMark, RoomRoster.ParticipantLegend, StringComparison.Ordinal);
        Assert.False(char.IsAscii(RoomRoster.ParticipantMark[0]));
    }
}
