using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a flag owed by the person reads as their own to-do, and one owed by an agent reads as it always did (SPEC.md §6,
/// specs/023 US5).
/// </summary>
public sealed class FlagWordingTests
{
    private static readonly AgentName Person = PersonSeat.On(ProjectName.Create("alpha"));
    private static readonly AgentName Orders = AgentName.Create("orders@box");
    private static readonly AgentName Billing = AgentName.Create("billing@box");

    private static Flag Owed(FlagKind kind, AgentName by, AgentName asker) =>
        new(kind, 7, "room", by, asker, new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(FlagKind.NotCollected)]
    [InlineData(FlagKind.NoProgress)]
    [InlineData(FlagKind.NotVoted)]
    public void A_flag_the_person_owes_is_waiting_on_them_and_names_who_asked(FlagKind kind)
    {
        var said = FlagWording.Describe(Owed(kind, Person, Orders), "09:00");

        Assert.StartsWith(FlagWording.WaitingOnYou, said, StringComparison.Ordinal);
        Assert.Contains("orders@box", said, StringComparison.Ordinal);

        // Nothing starts a person, and asking yourself is not a remedy.
        Assert.DoesNotContain("start", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("collected", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Person.Value, said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flag_an_agent_owes_reads_as_before()
    {
        var said = FlagWording.Describe(Owed(FlagKind.NoProgress, Orders, Billing), "09:00");

        Assert.Equal("orders@box collected billing@box's request and has done nothing on it since 09:00.", said);
    }

    [Fact]
    public void A_kind_this_build_does_not_know_is_never_called_a_retirement()
    {
        // The default arm both surfaces had said "is retired", which is how 024's new flag would have been described.
        var said = FlagWording.Describe(Owed((FlagKind)99, Orders, Billing), "09:00");

        Assert.DoesNotContain("retired", said, StringComparison.Ordinal);
    }
}
