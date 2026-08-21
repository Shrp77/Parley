using System.Reflection;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Keeps SPEC.md §10.1 and §10.2 on opposite sides of a type boundary.
/// </summary>
/// <remarks>
/// <para>
/// §10.2 opens by warning that an adapter surfacing an operator operation into an agent's tool
/// set would breach §10.1 and, through it, §16. The protection the document offers is a naming
/// convention — <c>force_conclude</c> is deliberately not called <c>conclude</c> — which is real
/// and thin: it depends on whoever writes the adapter reading carefully.
/// </para>
/// <para>
/// So the operations live on separate types, and this is what keeps them there. An adapter is
/// handed a <see cref="Broker"/>, and a broker has no operator operation to bind. The failure
/// this guards against is not writing the wrong call today; it is somebody adding the next
/// operator operation to the nearest class a year from now, when the reason has faded and
/// <see cref="Broker"/> is where the connection already is.
/// </para>
/// </remarks>
public sealed class SurfaceBoundaryTests
{
    /// <summary>
    /// The agent operations of §10.1, plus the lifecycle a host drives.
    /// </summary>
    /// <remarks>
    /// Listed rather than pattern-matched, because the point is to make an addition a decision.
    /// A new public method on the broker fails this until somebody writes it down here, which is
    /// the moment to ask whether it belongs to the agent or to the operator.
    /// </remarks>
    private static readonly HashSet<string> BrokerSurface =
    [
        // §10.1, as far as it is built.
        nameof(Broker.AskAsync),
        nameof(Broker.ClarifyAsync),
        nameof(Broker.AnswerAsync),
        nameof(Broker.ConcludeAsync),
        nameof(Broker.NotifyAsync),
        nameof(Broker.DrainInboxAsync),
        nameof(Broker.ListAgentsAsync),

        // Lifecycle. Driven by the host, not exposed to an agent by any adapter.
        nameof(Broker.StartAsync),
        nameof(Broker.TickAsync),
        nameof(Broker.ResumeOwedByAsync),
        "get_StartedUtc"
    ];

    private static IEnumerable<string> PublicMethodsOf(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_Equals", StringComparison.Ordinal))
            .Where(n => n is not ("Equals" or "GetHashCode" or "ToString"));

    [Fact]
    public void The_broker_exposes_no_operation_it_has_not_declared()
    {
        var unexpected = PublicMethodsOf(typeof(Broker)).Where(n => !BrokerSurface.Contains(n));

        Assert.Empty(unexpected);
    }

    [Fact]
    public void No_operator_operation_is_reachable_from_the_broker()
    {
        // The specific mistake §10.2 describes: an adapter binds the wrong one, the signatures
        // nearly match, and the agent acquires the power to end a discussion over its peers'
        // objections.
        var operatorOperations = PublicMethodsOf(typeof(OperatorConsole)).ToHashSet();
        Assert.NotEmpty(operatorOperations);

        var leaked = PublicMethodsOf(typeof(Broker)).Where(operatorOperations.Contains);

        Assert.Empty(leaked);
    }

    [Fact]
    public void The_operator_console_carries_no_agent_operation()
    {
        // The boundary in the other direction. An operator console that grew an ask would put a
        // person's hand on an agent's identity, which §11.3 spends its length keeping honest.
        var leaked = PublicMethodsOf(typeof(OperatorConsole))
            .Where(n => n is nameof(Broker.AskAsync) or nameof(Broker.ClarifyAsync)
                          or nameof(Broker.AnswerAsync) or nameof(Broker.ConcludeAsync)
                          or nameof(Broker.DrainInboxAsync));

        Assert.Empty(leaked);
    }
}
