using Grpc.Core;
using Parley.Core;
using Parley.Store;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// The agent surface of SPEC.md §10.1, served over the transport.
/// </summary>
/// <remarks>
/// <para>
/// It holds no state. Everything is on <see cref="BrokerRuntime"/>, because a gRPC service is
/// constructed per call and state on one would be state that vanished between them.
/// </para>
/// <para>
/// <b>There is no operator operation here and there cannot be one.</b> CLAUDE.md records why the
/// broker's own surface is guarded: SPEC.md §10.2 warns that an adapter surfacing an operator
/// operation into an agent's tool set would breach §10.1, and a naming convention is thin
/// protection. Splitting the wire into two services makes it structural — this class has no
/// reference to an <see cref="Parley.Broker.OperatorConsole"/> to bind, so the mistake cannot be
/// made by reaching for the nearest object.
/// </para>
/// <para>
/// <b>Every handler nudges the peer it wrote to, after the write and outside the gate.</b> The
/// notice is the point of holding a connection at all (§4.0), and it is deliberately the last
/// thing each handler does: the operation is durable once the broker returns, so a failure to
/// nudge costs the peer some latency and costs the protocol nothing.
/// </para>
/// </remarks>
public sealed class AgentsService : Wire.Agents.AgentsBase
{
    private readonly BrokerRuntime _runtime;

    /// <summary>Creates the service over a runtime.</summary>
    /// <param name="runtime">The broker process's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public AgentsService(BrokerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc/>
    public override async Task Attach(
        Wire.AttachRequest request,
        IServerStreamWriter<Wire.Event> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var name = Name(request.Agent);

        RequireTheSameProtocol(request.ProtocolVersion);

        var registration = new AgentRecord(
            name,
            request.Adapter,
            Conversions.In(request.Tier),
            Blank(request.Workspace),
            Blank(request.SessionRef),
            Blank(request.AdapterConfig),
            Connected: false,
            AgentStatus.Active,
            Blank(request.Owns));

        AttachedAgent attached;

        try
        {
            attached = await _runtime.AttachAsync(registration, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // A name held by another workspace, an unknown registration, a retirement. All three
            // are deployment faults a person has to fix, and none is retryable -- so they are
            // reported as FailedPrecondition rather than as an error the client should back off
            // and repeat.
            throw new RpcException(
                new Status(StatusCode.FailedPrecondition, exception.Message, exception));
        }

        await using (attached.ConfigureAwait(false))
        {
            // First and always. SPEC.md §6.4: an agent coming back is told how much of its own
            // abandoned work was released, because the alternative is that it resumes a task it
            // no longer owes and discovers that only when the write is refused.
            await responseStream.WriteAsync(
                new Wire.Event
                {
                    Attached = new Wire.Attached
                    {
                        AgentMarkedLive = attached.Started.AgentMarkedLive,
                        ExchangesOrphaned = attached.Started.ExchangesOrphaned
                    }
                },
                context.CancellationToken).ConfigureAwait(false);

            // Anything already waiting, before the stream goes quiet. Without this an agent that
            // attaches after a question was asked hears nothing until the next one arrives --
            // the notice would only ever cover what happened while it was watching, which is the
            // half of the problem polling did not have.
            await _runtime.NudgeAsync(name, context.CancellationToken).ConfigureAwait(false);

            try
            {
                await foreach (var notice in attached.Notices
                    .ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(
                        new Wire.Event { Notice = notice }, context.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away, which is the ordinary end of an attachment rather than a
                // fault. The registry is told by the disposal above, on every path out of here.
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<Wire.AskReply> Ask(
        Wire.AskRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Name(request.From);
        var to = Name(request.To);

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.AskAsync(
                from, to, request.ResponderModel, request.PayloadJson, now,
                Blank(request.ConversationId), ct),
            context).ConfigureAwait(false);

        if (outcome.Succeeded)
        {
            await WakeAsync(to, from, context).ConfigureAwait(false);
        }

        return Asked(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.AskRoleReply> AskRole(
        Wire.AskRoleRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Name(request.From);

        var outcomes = await Run(
            from,
            (broker, now, ct) => broker.AskRoleAsync(
                from, request.Role, request.ResponderModel, request.PayloadJson, now,
                Blank(request.ConversationId), ct),
            context).ConfigureAwait(false);

        var reply = new Wire.AskRoleReply();

        foreach (var outcome in outcomes)
        {
            reply.Outcomes.Add(Asked(outcome));

            // Each member individually, because a role question is N ordinary exchanges (§5.5)
            // and the members that were admitted must be woken whether or not the others were.
            if (outcome.Exchange is { } exchange)
            {
                await WakeAsync(exchange.Answerer, from, context).ConfigureAwait(false);
            }
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.SayReply> Clarify(
        Wire.ClarifyRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Name(request.From);

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.ClarifyAsync(
                request.ExchangeId, from, request.ResponderModel, request.PayloadJson, now, ct),
            context).ConfigureAwait(false);

        await NudgeRecipient(outcome, context).ConfigureAwait(false);

        return Said(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.SayReply> Answer(
        Wire.AnswerRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Name(request.From);

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.AnswerAsync(
                request.ExchangeId, from, request.ResponderModel, request.PayloadJson, now,
                Conversions.In(request.Resolution), request.IsDecision, ct),
            context).ConfigureAwait(false);

        await NudgeRecipient(outcome, context).ConfigureAwait(false);

        return Said(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.NotifyReply> Notify(
        Wire.NotifyRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Name(request.From);
        var to = Name(request.To);

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.NotifyAsync(
                from, to, request.ResponderModel, request.PayloadJson, now,
                Blank(request.ConversationId), ct),
            context).ConfigureAwait(false);

        if (outcome.Succeeded)
        {
            await _runtime.NudgeAsync(to, context.CancellationToken).ConfigureAwait(false);
        }

        return new Wire.NotifyReply
        {
            Ok = outcome.Succeeded,
            Refusal = Conversions.Out(outcome.Rejection),
            Deduplicated = outcome.Succeeded && !outcome.Stored
        };
    }

    /// <inheritdoc/>
    public override async Task<Wire.ConcludeReply> Conclude(
        Wire.ConcludeRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Name(request.From);

        // Through Conversions like every other enum, rather than switched on here. One home for
        // each mapping is what lets a reflection test check them all; a mapping written at its
        // call site is one the guard cannot see.
        Vote vote;

        try
        {
            vote = Conversions.In(request.Vote);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "A vote must be DONE or NOT_DONE. An unset vote is not an abstention: SPEC.md " +
                "§8 has silence lapse into consent on its own schedule, and a call that said " +
                "nothing would conclude a conversation without anybody deciding to.",
                exception));
        }

        var finished = vote == Vote.Done;

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.ConcludeAsync(request.ConversationId, from, finished, now, ct),
            context).ConfigureAwait(false);

        return new Wire.ConcludeReply
        {
            Ok = outcome.Succeeded,
            Refusal = Conversions.Out(outcome.Error),
            Concluded = outcome.Concluded
        };
    }

    /// <inheritdoc/>
    public override async Task<Wire.ListAgentsReply> ListAgents(
        Wire.ListAgentsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var asking = Name(request.AskingAs);

        var agents = await Run(
            asking,
            (broker, _, ct) => broker.ListAgentsAsync(ct),
            context).ConfigureAwait(false);

        var reply = new Wire.ListAgentsReply();

        foreach (var agent in agents)
        {
            reply.Agents.Add(Conversions.Out(agent));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.InboxReply> DrainInbox(
        Wire.DrainInboxRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var inbox = await Run(
            agent,
            (broker, now, ct) => broker.DrainInboxAsync(
                agent, now, request.Limit > 0 ? request.Limit : null, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.InboxReply();

        foreach (var message in inbox.Messages)
        {
            reply.Messages.Add(Conversions.Out(message));
        }

        foreach (var poll in inbox.ClosurePolls)
        {
            reply.ClosurePolls.Add(Conversions.Out(poll));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.Notice> Waiting(
        Wire.WaitingRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        // Deliberately not routed through the attachment. This is asked by things that hold no
        // stream — an editor hook, a status line — and requiring an attach would mean opening one
        // to ask a question about counts, which is the opposite of cheap.
        return await _runtime.UseConnectionAsync(
            async (connection, _, ct) => new Wire.Notice
            {
                MessagesWaiting = await new MessageRepository(connection)
                    .CountPendingAsync(agent, ct).ConfigureAwait(false),
                ClosurePollsWaiting = await new ConversationRepository(connection)
                    .CountOutstandingPollsAsync(agent, ct).ConfigureAwait(false)
            },
            context.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<Wire.Empty> RecordRefusal(
        Wire.RecordRefusalRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        // Best effort, and silent when it fails. The adapter has already told the model its
        // message was too long, and that refusal must not depend on the bookkeeping behind it
        // succeeding — a full disk would otherwise turn a clear "say it in fewer words" into an
        // error about storage.
        try
        {
            _ = await _runtime.UseConnectionAsync(
                async (connection, now, ct) =>
                    await new SizeRefusalRepository(connection).RecordAsync(
                        agent, Conversions.In(request.Type), request.Characters,
                        MessageLimits.MaxPayloadCharacters, now, ct).ConfigureAwait(false),
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // See above.
        }

        return new Wire.Empty();
    }

    /// <inheritdoc/>
    public override async Task<Wire.ExchangesReply> ResumeOwedBy(
        Wire.ResumeOwedByRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Name(request.Agent);

        var exchanges = await Run(
            agent,
            (broker, now, ct) => broker.ResumeOwedByAsync(agent, now, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.ExchangesReply();

        foreach (var exchange in exchanges)
        {
            reply.Exchanges.Add(Conversions.Out(exchange));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.SubQuestionsReply> GetSubQuestionIds(
        Wire.SubQuestionsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Deliberately not on the request. The identity of the asker is the attachment's, and a
        // sub-question list is scoped to a participant -- so taking it from a field would let a
        // caller read the sub-questions of an exchange it is not party to by naming somebody who
        // is. §16 says the wire cannot tell who is calling; that is not a reason to add a second
        // place for a caller to say so.
        var asking = Name(request.AskingAs);

        var ids = await Run(
            asking,
            (broker, _, ct) => broker.GetSubQuestionIdsAsync(request.ExchangeId, asking, ct),
            context).ConfigureAwait(false);

        var reply = new Wire.SubQuestionsReply();
        reply.Ids.AddRange(ids);

        return reply;
    }

    /// <summary>Runs work on an agent's broker, turning its refusals into wire faults.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="agent">The agent the work is on behalf of.</param>
    /// <param name="work">The work.</param>
    /// <param name="context">The call.</param>
    /// <returns>Whatever the work returned.</returns>
    private async Task<T> Run<T>(
        AgentName agent,
        Func<Parley.Broker.Broker, DateTimeOffset, CancellationToken, Task<T>> work,
        ServerCallContext context)
    {
        try
        {
            return await _runtime.RunAsync(agent, work, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotAttachedException exception)
        {
            throw new RpcException(
                new Status(StatusCode.FailedPrecondition, exception.Message, exception));
        }
        catch (ArgumentException exception)
        {
            // A payload the broker would not accept: too long, malformed, missing what §8
            // requires of a question. Invalid rather than failed, because the same call with a
            // different payload would succeed.
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, exception.Message, exception));
        }
    }

    /// <summary>
    /// Tells a peer a question is waiting, and has it started if it is not running.
    /// </summary>
    /// <param name="peer">The agent the question went to.</param>
    /// <param name="asker">Who asked, named only so an operator can see why a process appeared.</param>
    /// <param name="context">The call.</param>
    /// <returns>A task that completes when both have been attempted.</returns>
    /// <remarks>
    /// <para>
    /// The two halves are for the two states a peer can be in. A running one is nudged, because
    /// its stream is already open. An idle one is started, because SPEC.md §5.5 says a question
    /// to a registered agent that is not running is what causes it to run — and a question that
    /// merely waited would be indistinguishable, from the asker's side, from one nobody could be
    /// bothered to answer.
    /// </para>
    /// <para>
    /// Neither is required to succeed. A machine with no node cannot start anything, and that
    /// is the case §5.5 designs for rather than around: the question stays open, orphans on the
    /// ordinary schedule, and is ghosted, so the asker is told that nobody answered instead of
    /// waiting on something that was never going to arrive.
    /// </para>
    /// </remarks>
    private async Task WakeAsync(AgentName peer, AgentName asker, ServerCallContext context)
    {
        await _runtime.NudgeAsync(peer, context.CancellationToken).ConfigureAwait(false);

        _ = _runtime.RequestAttention(peer, $"{asker.Value} asked it a question");
    }

    /// <summary>Wakes whichever party a move handed the turn to.</summary>
    /// <param name="outcome">The move.</param>
    /// <param name="context">The call.</param>
    /// <returns>A task that completes when the notice has been queued.</returns>
    /// <remarks>
    /// Read from the stored message rather than worked out from the exchange, because the
    /// message is what was actually written and the exchange is what it was written against. A
    /// clarification goes back to the asker and an answer goes to the asker too, but a
    /// clarification's <em>reply</em> goes the other way — and deriving that here would be a
    /// second copy of the turn-taking rule of §5, which lives in the broker.
    /// </remarks>
    private async Task NudgeRecipient(Parley.Broker.SayOutcome outcome, ServerCallContext context)
    {
        if (outcome.Message is { } message)
        {
            await _runtime.NudgeAsync(message.To, context.CancellationToken).ConfigureAwait(false);

            // Started as well, for the same reason an ask does it: a clarification handed to an
            // agent that has since closed its session is a turn nobody is there to take, and the
            // exchange is still open and still counting down.
            _ = _runtime.RequestAttention(message.To, $"{message.From.Value} is waiting on it");
        }
    }

    /// <summary>Converts an admission outcome for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The reply.</returns>
    private static Wire.AskReply Asked(Parley.Broker.AskOutcome outcome)
    {
        var reply = new Wire.AskReply
        {
            Ok = outcome.Succeeded,
            Rejection = Conversions.Out(outcome.Rejection),
            ExchangeId = outcome.Exchange?.Id ?? 0,
            Answerer = outcome.Exchange?.Answerer.Value ?? string.Empty
        };

        // What the asker is already waiting on, when it was refused for being at its cap. §7.1
        // hands this back so the refusal is actionable: the remedy is to conclude one of these,
        // and an asker that was told only "saturated" would have to go looking for which.
        foreach (var outstanding in outcome.Admission.Outstanding)
        {
            reply.WaitingOn.Add(outstanding.Id);
        }

        return reply;
    }

    /// <summary>Converts a move outcome for the wire.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The reply.</returns>
    private static Wire.SayReply Said(Parley.Broker.SayOutcome outcome) => new()
    {
        Ok = outcome.Succeeded,
        Refusal = Conversions.Out(outcome.Error),
        Deduplicated = outcome.Succeeded && !outcome.Stored,
        MessageId = outcome.Message?.Id ?? 0
    };

    /// <summary>Refuses an adapter speaking a different protocol.</summary>
    /// <param name="announced">What the adapter said it speaks.</param>
    /// <exception cref="RpcException">It is not what this broker speaks.</exception>
    /// <remarks>
    /// <para>
    /// The field existed before this did, which is the worse half of the story: an adapter sent
    /// its version on every attach and the broker read none of them. A field that looks like a
    /// compatibility check and is not is worse than no field at all, because somebody reading
    /// the contract concludes they are protected.
    /// </para>
    /// <para>
    /// <b>Any difference is refused while the major is zero</b>, because SPEC.md §18 says the
    /// minor carries breaking changes in that state. That is strict — an upgrade means updating
    /// the broker and every adapter together — and the alternative is worse in a way that is
    /// hard to diagnose: two versions that half-understand each other produce exchanges that
    /// open and are never answered, which looks exactly like a peer that is ignoring you.
    /// </para>
    /// <para>
    /// An adapter that announces nothing is refused with the same message. Silence means a build
    /// from before the field carried anything, which is 0.1 — and 0.1 opened the database
    /// directly, so it is not merely older but incompatible in the one way that matters.
    /// </para>
    /// <para>
    /// <c>Join</c> has no equivalent and needs none. A node receives commands rather than
    /// interpreting a state machine, and it ignores any command case it does not recognise —
    /// so a node older than its broker declines to act on what it cannot understand, which is
    /// the behaviour a version check would have had to produce anyway.
    /// </para>
    /// </remarks>
    private static void RequireTheSameProtocol(string announced)
    {
        if (string.Equals(announced, ProtocolVersion.Current, StringComparison.Ordinal))
        {
            return;
        }

        throw new RpcException(new Status(
            StatusCode.FailedPrecondition,
            $"This broker speaks Parley protocol {ProtocolVersion.Current} and that adapter " +
            $"speaks {(announced is { Length: > 0 } said ? said : "an older version that did not say")}. " +
            "While the major version is zero every minor carries breaking changes, so the two " +
            "do not interoperate. Update whichever is behind: both ship from the same build."));
    }

    /// <summary>Reads an agent name, refusing anything that is not one.</summary>
    /// <param name="value">The name as it arrived.</param>
    /// <returns>The name.</returns>
    /// <exception cref="RpcException">The value is not a well-formed name.</exception>
    private static AgentName Name(string value) =>
        AgentName.TryCreate(value, out var name)
            ? name
            : throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"'{value}' is not an agent name. Names are role@machine, lower case, and the " +
                "machine half is detected rather than configured (SPEC.md §4.1.1)."));

    /// <summary>Reads an optional string, treating the empty one as absent.</summary>
    /// <param name="value">The value as it arrived.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    /// <remarks>
    /// proto3 has no null for a scalar, so absence and emptiness arrive identically. Every field
    /// this is applied to is one where an empty string would be meaningless — a workspace path,
    /// a conversation identifier, a statement of ownership — so collapsing them loses nothing
    /// that could have been said deliberately.
    /// </remarks>
    private static string? Blank(string value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
