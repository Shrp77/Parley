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

        // Reads the identifiers an exchange's question declared, so an adapter can hold §5.3's
        // rule that there is one resolution per sub-question asked. On this side of the line
        // because the answerer was already told them when the question arrived — it returns
        // identifiers, never the question, so it cannot become a way around §13's envelope.
        nameof(Broker.GetSubQuestionIdsAsync),

        // Lifecycle. Driven by the host, not exposed to an agent by any adapter.
        nameof(Broker.StartAsync),
        nameof(Broker.DetachAsync),
        nameof(Broker.TickAsync),
        nameof(Broker.ResumeOwedByAsync),
        "get_StartedUtc",
        "get_Attached"
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
    public void The_console_exposes_no_operation_it_has_not_declared()
    {
        // The same discipline as the broker's surface, applied to the other side of §10.2. What
        // it buys is that adding an operator operation is a decision about which side of the
        // boundary it belongs on, rather than a question of which class the method was already
        // in — and this list is where the answer gets written down.
        //
        // This is the whole of §10.2 now that prune exists. It is the one that reads its policy
        // from an argument rather than deciding for itself, which is what keeps the operator's
        // command and the servers' sweep from applying different numbers to the same file.
        string[] declared =
        [
            nameof(OperatorConsole.ForceConcludeAsync),
            nameof(OperatorConsole.RetireAsync),
            nameof(OperatorConsole.ReinstateAsync),

            // On this side because it is a judgement about the registry that no agent may make
            // about itself or any other, which is the same reason retire is here — and the case
            // for keeping it here is now much stronger than when it was written. Purge once
            // refused any agent that had traded, so smuggling it onto the agent surface would
            // have leaked a way to delete an unused registration. It now destroys the agent and
            // every conversation it was party to, so the same leak would hand one agent the
            // power to erase another and every record of having dealt with it.
            nameof(OperatorConsole.PurgeAsync),
            nameof(OperatorConsole.DescribeRegistryAsync),
            nameof(OperatorConsole.ExportAsync),
            nameof(OperatorConsole.PruneAsync),
            nameof(OperatorConsole.DescribeRefusalsAsync),

            // Deliberately not named for the agent operation it reports on. §10.2's protection
            // against an operator operation reaching an agent's tool set is that the names
            // differ loudly, which is why force_conclude is not conclude — and the agent already
            // has clarify(exchange_id, text). A console method called DescribeClarifyAsync beside
            // a tool called parley_clarify is exactly the near-miss that convention exists for.
            nameof(OperatorConsole.DescribeClarificationsAsync),
            nameof(OperatorConsole.DescribeReferralsAsync),
            nameof(OperatorConsole.DescribeNotificationsAsync)
        ];

        var unexpected = PublicMethodsOf(typeof(OperatorConsole)).Where(n => !declared.Contains(n));

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
    public void An_operation_naming_an_exchange_or_a_conversation_also_names_the_agent()
    {
        // An identifier is not an authorisation. An exchange id is a small integer an agent can
        // guess, and a conversation id is a string an agent chose — so an operation that takes
        // one and no acting agent will answer whoever calls it about whatever they name.
        //
        // This is not hypothetical. GetSubQuestionIdsAsync shipped without the agent for exactly
        // one review round, and the adapter built a refusal out of it that read back another
        // pair's sub-question identifiers to a bystander that had guessed the exchange id.
        // Identifiers are agent-chosen, so one may well name what a team is working on. Ordering
        // the adapter's guards would not have fixed it: what leaked was the diagnostic itself.
        var unscoped = typeof(Broker)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p =>
                p.Name is "exchangeId" or "conversationId"))
            .Where(m => !m.GetParameters().Any(p => p.ParameterType == typeof(Parley.Core.AgentName)))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(unscoped);
    }

    [Fact]
    public void No_broker_operation_hands_back_a_stored_registry_record()
    {
        // AgentRecord carries a workspace path, a session handle and the adapter's own
        // configuration; AgentSummary exists so none of that reaches a peer. The two differ by
        // one word at the call site, so the projection is exactly the kind of thing that gets
        // skipped by a later operation written in a hurry — rule 3 breached through the one
        // operation whose job is to describe agents.
        var leaking = typeof(Broker)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => Mentions(m.ReturnType, typeof(Parley.Store.AgentRecord)))
            .Select(m => m.Name);

        Assert.Empty(leaking);

        static bool Mentions(Type type, Type target) =>
            type == target ||
            (type.IsGenericType && type.GetGenericArguments().Any(a => Mentions(a, target)));
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
