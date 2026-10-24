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
        // §10.1.
        nameof(Broker.RequestAsync),

        // The same §10.1 operation with a role for a receiver (§5.5), not a second one. It opens N ordinary
        // exchanges and can do nothing RequestAsync could not do N times.
        nameof(Broker.RequestRoleAsync),
        nameof(Broker.ClarifyAsync),
        nameof(Broker.ResultAsync),
        nameof(Broker.ConcludeAsync),
        nameof(Broker.DrainInboxAsync),
        nameof(Broker.ListAgentsAsync),

        // The one roadmap operation an agent has (§10.1, principle 2.12): it stores a proposal and nothing else, so
        // it opens no exchange and creates no roadmap. Everything else about a roadmap is the person's and is below,
        // on the console. The names differ loudly: no console method is called Propose, and no broker method
        // reads, changes or confirms.
        nameof(Broker.ProposeRoadmapAsync),

        // Lifecycle. Driven by the host, not exposed to an agent by any adapter.
        nameof(Broker.StartAsync),
        nameof(Broker.DetachAsync),

        // Neither §10.1 nor §10.2. ServeFrom marks a broker that speaks for no agent as started, for the tick;
        // an agent must never reach it, and neither must an operator.
        nameof(Broker.ServeFrom),
        nameof(Broker.TickAsync),

        // What a restart wakes an agent for (§14). The host reads it; an agent reaching it would learn what
        // another agent owes.
        nameof(Broker.OutstandingToWakeForAsync),
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

            // On this side because what it records is a PERSON'S decision about an agent, which is
            // the one thing §17 question 10 says the field is worth reading for. An agent-facing
            // version of this would let a model write its own charter, which is the whole of what
            // that question closes. Note what it deliberately cannot do even here: it does not write
            // `owns`. The registry is still filled from the launch environment on attach (§12.3), so
            // no operation on either side sets the line an asker reads.
            nameof(OperatorConsole.RecordOwnershipAsync),

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

            // Declared here on purpose, and this is the decision the test exists to force. A
            // listing reads across every pair in the deployment, which is precisely the
            // disclosure §10.1 withholds from an agent: an agent may address a peer, and must
            // not be able to enumerate what its peers have discussed with anybody else. It sits
            // beside export for the same reason export does.
            nameof(OperatorConsole.ListConversationsAsync),

            nameof(OperatorConsole.PruneAsync),
            nameof(OperatorConsole.DescribeRefusalsAsync),

            // Deliberately not named for the agent operation it reports on. §10.2's protection
            // against an operator operation reaching an agent's tool set is that the names
            // differ loudly, which is why force_conclude is not conclude — and the agent already
            // has clarify(exchange_id, text). A console method called DescribeClarifyAsync beside
            // a tool called parley_clarify is exactly the near-miss that convention exists for.
            nameof(OperatorConsole.DescribeClarificationsAsync),

            // What Results said, and what needs the person. Both read across every pair, which is the disclosure
            // §10.1 withholds: an agent could learn who has been blocked on whom.
            nameof(OperatorConsole.DescribeResultsAsync),
            nameof(OperatorConsole.DescribeFlagsAsync),

            // Who is waiting on whom. An operator operation because the whole of its value is
            // that it reads across pairs: from inside one, a question nobody will answer looks
            // exactly like one not answered yet, and only somebody seeing every open exchange at
            // once can tell them apart. Handing that to an agent would say who is talking to whom
            // across the deployment, which is the disclosure §10.1 exists to prevent.
            nameof(OperatorConsole.DescribeWaitingAsync),

            // Filing a conversation away, and how many are filed. An operator operation because
            // it is a decision about an operator's own view of a deployment, and because an agent
            // able to archive a conversation it is in would be deciding for every operator at
            // once — the authority Principle II says no agent has. It changes nothing an agent can
            // observe: delivery, exchanges and the closure poll are all untouched.
            nameof(OperatorConsole.SetConversationArchivedAsync),
            nameof(OperatorConsole.CountArchivedConversationsAsync),

            // §14's purge argument applied to a narrower subject, and it sits beside purge rather
            // than beside force_conclude for that reason: force_conclude ends a discussion and
            // leaves the record, this removes the record. Every participant's half goes, and an
            // open exchange inside it is not closed — it ceases to exist, so the agent waiting on
            // it is never told anything at all. An agent able to reach this
            // could erase a peer's evidence of having dealt with it, which is the same leak the
            // purge entry above warns about with a smaller blast radius and the same shape.
            nameof(OperatorConsole.DestroyConversationAsync),

            // Roadmaps (§5.7, principle 2.12). All six are a person's: an agent able to read one would learn a plan
            // it was never sent and could not be told to follow, and one able to confirm, edit or decline would be
            // an agent deciding what other agents are asked to do. There is deliberately nothing on the broker's
            // side but ProposeRoadmapAsync, and this list is where that is written down.
            nameof(OperatorConsole.ListRoadmapsAsync),
            nameof(OperatorConsole.GetRoadmapAsync),
            nameof(OperatorConsole.ListProposalsAsync),
            nameof(OperatorConsole.ListProposalHeadingsAsync),
            nameof(OperatorConsole.GetProposalAsync),
            nameof(OperatorConsole.CountProposalsAsync),
            nameof(OperatorConsole.ConfirmProposalAsync),
            nameof(OperatorConsole.DeclineProposalAsync),
            nameof(OperatorConsole.EditRoadmapAsync)
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
        // This is not hypothetical. An operation reading an exchange's item identifiers shipped
        // without the agent for one review round, and the adapter built a refusal out of it that
        // read another pair's identifiers back to a bystander that had guessed the exchange id.
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
            .Where(m => Mentions(m.ReturnType, typeof(Parley.Core.AgentRecord)))
            .Select(m => m.Name);

        Assert.Empty(leaking);

        static bool Mentions(Type type, Type target) =>
            type == target ||
            (type.IsGenericType && type.GetGenericArguments().Any(a => Mentions(a, target)));
    }

    [Fact]
    public void The_operator_console_carries_no_agent_operation()
    {
        // The boundary in the other direction. An operator console that grew a request would put a
        // person's hand on an agent's identity, which §11.3 spends its length keeping honest.
        var leaked = PublicMethodsOf(typeof(OperatorConsole))
            .Where(n => n is nameof(Broker.RequestAsync) or nameof(Broker.ClarifyAsync)
                          or nameof(Broker.ResultAsync) or nameof(Broker.ConcludeAsync)
                          or nameof(Broker.DrainInboxAsync));

        Assert.Empty(leaked);
    }

    /// <summary>
    /// The facilitator's surface, fixed as the other two are (SPEC.md principle 2.11).
    /// </summary>
    /// <remarks>
    /// A third list rather than an addition to either, because the facilitator is neither: it may ask, send a person's
    /// reply and agree its own conversation is done, and the constitution names those as all it may do. Growing this
    /// list is therefore a constitutional question before it is a code one, and this test is where that surfaces.
    /// </remarks>
    [Fact]
    public void The_facilitator_exposes_no_operation_it_has_not_declared()
    {
        string[] declared =
        [
            nameof(Facilitator.AskDueAsync),
            nameof(Facilitator.AskAsync),
            nameof(Facilitator.ReadBackAsync),
            nameof(Facilitator.AcceptAutomaticallyAsync),
            nameof(Facilitator.HoldAsync),
            nameof(Facilitator.SettledByPersonAsync),
            nameof(Facilitator.AutoAcceptAsync),
            nameof(Facilitator.RefreshDueAsync),
            nameof(Facilitator.RefreshSentAsync),

            // The person's side (§10.2): what they read, the switch, and the three decisions about a question. Asking
            // again is asking; the reply is a person's words sent through Parley; a discard settles its own question.
            nameof(Facilitator.ReportAsync),
            nameof(Facilitator.SetAutoAcceptAsync),
            nameof(Facilitator.AskAgainAsync),
            nameof(Facilitator.ReplyAsync),
            nameof(Facilitator.DiscardAsync)
        ];

        var unexpected = PublicMethodsOf(typeof(Facilitator)).Where(n => !declared.Contains(n));

        Assert.Empty(unexpected);
    }

    [Fact]
    public void Neither_other_surface_gained_a_facilitator_operation()
    {
        // Broker is what an attached agent is handed and OperatorConsole what the operator is; a facilitator operation
        // on either would hand one of them the facilitator's voice.
        var facilitator = PublicMethodsOf(typeof(Facilitator)).ToHashSet();

        Assert.DoesNotContain(PublicMethodsOf(typeof(Broker)), facilitator.Contains);
        Assert.DoesNotContain(PublicMethodsOf(typeof(OperatorConsole)), facilitator.Contains);
    }

    [Fact]
    public void The_facilitator_has_none_of_the_operations_it_may_never_have()
    {
        // It never sends a Result (so `closed` stays the only outcome the broker writes), never votes on anybody's
        // conversation but its own through a caller's say-so, and holds no operator operation.
        var forbidden = PublicMethodsOf(typeof(Facilitator))
            .Where(n => n is nameof(Broker.ResultAsync) or nameof(Broker.ConcludeAsync) or nameof(Broker.RequestAsync)
                          or nameof(OperatorConsole.ForceConcludeAsync) or nameof(OperatorConsole.RetireAsync)
                          or nameof(OperatorConsole.PurgeAsync)
                          or nameof(OperatorConsole.DestroyConversationAsync));

        Assert.Empty(forbidden);
    }
}
