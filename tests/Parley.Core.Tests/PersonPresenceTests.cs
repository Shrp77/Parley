using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That the person reads here or away and nothing else, the facilitator reads running, and no agent reads either of the
/// person's words (SPEC.md §14, specs/023 US3).
/// </summary>
public sealed class PersonPresenceTests
{
    private static readonly AgentName Person = PersonSeat.On(ProjectName.Create("alpha"));
    private static readonly AgentName Parley = AgentName.Create(Facilitation.Role + "@alpha");
    private static readonly AgentName Orders = AgentName.Create("orders@box");

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void The_person_reads_here_with_a_connection_and_away_without_whatever_the_node(bool node, bool connected) =>
        Assert.Equal(connected ? AgentPresence.Here : AgentPresence.Away, AgentLiveness.Read(Person, node, connected));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Parley_always_reads_running(bool node, bool connected) =>
        Assert.Equal(AgentPresence.Running, AgentLiveness.Read(Parley, node, connected));

    [Theory]
    [InlineData(true, true, AgentPresence.Running)]
    [InlineData(true, false, AgentPresence.Startable)]
    [InlineData(false, false, AgentPresence.Stranded)]
    [InlineData(false, true, AgentPresence.Running)]
    public void An_agent_reads_as_it_always_did_and_never_as_here_or_away(bool node, bool connected, AgentPresence reads) =>
        Assert.Equal(reads, AgentLiveness.Read(Orders, node, connected));
}
