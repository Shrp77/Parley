using System.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Keeps every wire enum in step with the domain enum it copies.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the copies drifted while they were being written. The contract file
/// carries a comment saying that a hand-written copy of an enum is a thing that can drift, and
/// four of the five enums under it were wrong within minutes of that comment being typed —
/// <c>Vote</c> had invented members, <c>Resolution</c> had three that did not exist,
/// <c>ExchangeState</c> named a state the domain does not have. A fifth, <c>OpenRejection</c>,
/// was short a member and was caught only because a formatting <c>switch</c> in the adapter
/// happened to name it.
/// </para>
/// <para>
/// None of those is a compile error. A missing member means a value the broker can produce and
/// the wire cannot carry, which surfaces as an exception on the one code path that produces it —
/// so the refusal an agent most needs to understand is the one that arrives as a transport
/// fault. An invented member is worse: it compiles, serialises, and means nothing on the other
/// side.
/// </para>
/// <para>
/// So the check is by reflection over both enums rather than a written list. A list would be a
/// third copy, and the failure it caught would be the failure of somebody to update it.
/// </para>
/// </remarks>
public sealed class TransportEnumTests
{
    /// <summary>
    /// Every domain enum that has a counterpart on the wire, paired with it.
    /// </summary>
    /// <remarks>
    /// The pairing is written down; the members are not. Which types correspond is a decision —
    /// the wire deliberately has no counterpart for several domain enums — and deriving it from
    /// matching names would silently stop checking a type that was renamed.
    /// </remarks>
    public static TheoryData<Type, Type> Counterparts =>
        new()
        {
            { typeof(Core.AgentTier), typeof(Wire.AgentTier) },
            { typeof(Core.AgentStatus), typeof(Wire.AgentStatus) },
            { typeof(Core.AgentPresence), typeof(Wire.AgentPresence) },
            { typeof(Core.MessageType), typeof(Wire.MessageType) },
            { typeof(Core.ExchangeState), typeof(Wire.ExchangeState) },
            { typeof(Core.Resolution), typeof(Wire.Resolution) },
            { typeof(Core.Vote), typeof(Wire.Vote) },
            { typeof(Store.OpenRejection), typeof(Wire.OpenRejection) },
            { typeof(Core.PurgeVerdict), typeof(Wire.PurgeVerdict) },
            { typeof(Core.InstructionOutcome), typeof(Wire.InstructionOutcome) },
            { typeof(Core.TransitionError), typeof(Wire.TransitionError) },
            { typeof(Broker.NotifyRejection), typeof(Wire.NotifyRejection) },
            { typeof(Broker.ClosureError), typeof(Wire.ClosureError) }
        };

    [Theory]
    [MemberData(nameof(Counterparts))]
    public void Every_domain_member_can_be_carried(Type domain, Type wire)
    {
        var carried = Enum.GetNames(wire).ToHashSet(StringComparer.Ordinal);

        // The generated names are PascalCase without the enum-name prefix proto3 requires in the
        // file, which is what makes a name-for-name comparison possible at all.
        var missing = Enum.GetNames(domain).Where(n => !carried.Contains(n)).ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Counterparts))]
    public void The_wire_invents_nothing_the_domain_does_not_have(Type domain, Type wire)
    {
        var known = Enum.GetNames(domain).ToHashSet(StringComparer.Ordinal);

        // Unspecified is the exception and the only one. proto3 requires a zero-valued member and
        // the domain has no counterpart for it by design: it is what an unset field arrives as,
        // and every conversion refuses it rather than mapping it to whichever member happens to
        // be numbered zero.
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
        // The property the whole scheme rests on. A domain member that converted to zero would
        // be indistinguishable on the other side from a field nobody set, so a real refusal
        // would arrive as "unknown" and be reported to an agent as a value the broker had never
        // heard of.
        //
        // Checked by conversion rather than by arithmetic: Conversions is what runs in
        // production, and asserting that its output is non-zero tests the thing that matters
        // instead of the numbering convention that currently makes it true.
        foreach (var member in Enum.GetValues(domain))
        {
            var converted = Convert(member, wire);

            Assert.NotEqual(0, converted);
        }
    }

    /// <summary>Converts a domain value through the production conversion.</summary>
    /// <param name="member">The domain value.</param>
    /// <param name="wire">The wire enum it should become.</param>
    /// <returns>Its numeric wire value.</returns>
    /// <remarks>
    /// Found by signature rather than named, because the conversions are deliberately a set of
    /// overloads called <c>Out</c> — naming each one here would be a third list to keep in step.
    /// </remarks>
    private static int Convert(object member, Type wire)
    {
        var conversions = typeof(BrokerRuntime).Assembly
            .GetType("Parley.Server.Conversions", throwOnError: true)!;

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
