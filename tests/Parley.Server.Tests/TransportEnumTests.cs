using System.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Keeps every wire enum in step with the domain enum it copies.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the copies drifted while they were being written: members invented, members
/// missing, a state named that the domain does not have. None of those is a compile error. A missing member means
/// a value the broker can produce and the wire cannot carry, which surfaces as an exception on the one code path
/// that produces it — so the refusal a participant most needs to understand is the one that arrives as a
/// transport fault.
/// </para>
/// <para>
/// So the check is by reflection over both enums rather than a written list. A list would be a third copy, and
/// the failure it caught would be the failure of somebody to update it.
/// </para>
/// </remarks>
public sealed class TransportEnumTests
{
    /// <summary>Every domain enum that has a counterpart on the wire, paired with it.</summary>
    /// <remarks>
    /// The pairing is written down; the members are not. Which types correspond is a decision, and deriving it
    /// from matching names would silently stop checking a type that was renamed.
    /// </remarks>
    public static TheoryData<Type, Type> Counterparts =>
        new()
        {
            { typeof(Core.AgentTier), typeof(Wire.AgentTier) },
            { typeof(Core.AgentStatus), typeof(Wire.AgentStatus) },
            { typeof(Core.AgentPresence), typeof(Wire.AgentPresence) },
            { typeof(Core.MessageType), typeof(Wire.MessageType) },
            { typeof(Core.ExchangeState), typeof(Wire.ExchangeState) },
            { typeof(Core.ExchangeOutcome), typeof(Wire.ExchangeOutcome) },
            { typeof(Core.Outcome), typeof(Wire.Outcome) },
            { typeof(Core.BlockerKind), typeof(Wire.BlockerKind) },
            { typeof(Core.ConversationCloser), typeof(Wire.ConversationCloser) },
            { typeof(Core.FlagKind), typeof(Wire.FlagKind) },
            { typeof(Core.Vote), typeof(Wire.Vote) },
            { typeof(Store.RequestRejection), typeof(Wire.RequestRejection) },
            { typeof(Core.PurgeVerdict), typeof(Wire.PurgeVerdict) },
            { typeof(Core.InstructionOutcome), typeof(Wire.InstructionOutcome) },
            { typeof(Core.TransitionError), typeof(Wire.TransitionError) },
            { typeof(Broker.ClosureError), typeof(Wire.ClosureError) },
            { typeof(Core.WakeOutcome), typeof(Wire.WakeOutcome) },
            { typeof(Core.FileRejection), typeof(Wire.FileRejection) },
            { typeof(Core.PlaceOutcome), typeof(Wire.PlaceOutcome) }
        };

    [Theory]
    [MemberData(nameof(Counterparts))]
    public void Every_domain_member_can_be_carried(Type domain, Type wire)
    {
        var carried = Enum.GetNames(wire).ToHashSet(StringComparer.Ordinal);

        // The generated names are PascalCase without the enum-name prefix proto3 requires in the file, which is
        // what makes a name-for-name comparison possible at all.
        var missing = Enum.GetNames(domain).Where(n => !carried.Contains(n)).ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Counterparts))]
    public void The_wire_invents_nothing_the_domain_does_not_have(Type domain, Type wire)
    {
        var known = Enum.GetNames(domain).ToHashSet(StringComparer.Ordinal);

        // Unspecified is the exception and the only one: proto3 requires a zero-valued member, and it is what an
        // unset field arrives as.
        var invented = Enum.GetNames(wire)
            .Where(n => !string.Equals(n, "Unspecified", StringComparison.Ordinal))
            .Where(n => !known.Contains(n))
            .ToArray();

        Assert.Empty(invented);
    }

    [Theory]
    [MemberData(nameof(Counterparts))]
    public void Nothing_maps_onto_the_unspecified_value(Type domain, Type wire)
    {
        // A domain member that converted to zero would be indistinguishable on the other side from a field nobody
        // set. Checked through the production conversion rather than by arithmetic.
        foreach (var member in Enum.GetValues(domain))
        {
            Assert.NotEqual(0, Convert(member, wire));
        }
    }

    /// <summary>Converts a domain value through the production conversion.</summary>
    /// <param name="member">The domain value.</param>
    /// <param name="wire">The wire enum it should become.</param>
    /// <returns>Its numeric wire value.</returns>
    private static int Convert(object member, Type wire)
    {
        var conversions = typeof(BrokerRuntime).Assembly.GetType("Parley.Server.Conversions", throwOnError: true)!;

        var method = conversions
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Out"
                         && m.ReturnType == wire
                         && m.GetParameters() is [var only]
                         && (only.ParameterType == member.GetType()
                             || only.ParameterType == typeof(Nullable<>).MakeGenericType(member.GetType())));

        return (int)method.Invoke(null, [member])!;
    }
}
