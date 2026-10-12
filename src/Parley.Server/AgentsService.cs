using Grpc.Core;
using Parley.Core;
using Parley.Store;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// The participant surface of SPEC.md §10.1, served over the transport.
/// </summary>
/// <remarks>
/// <para>
/// It holds no state. Everything is on <see cref="BrokerRuntime"/>, because a gRPC service is
/// constructed per call.
/// </para>
/// <para>
/// <b>There is no operator operation here and there cannot be one.</b> This class has no reference to an
/// <see cref="Parley.Broker.OperatorConsole"/> to bind, so the mistake §10.2 warns about cannot be made
/// by reaching for the nearest object.
/// </para>
/// <para>
/// <b>Every message wakes the participant it was written to, after the write and outside the gate</b>
/// (§5.4). The operation is durable once the broker returns, so a failure to wake costs latency and
/// never correctness. Waking is coalesced per receiver across every sender until it collects, so a
/// burst of Requests costs one turn.
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

        var name = Caller(request.Agent);

        // The other half of the reservation: a row carrying the facilitator's adapter under any role would read as
        // Parley on every surface. Storage refuses it too, but as a constraint rather than a sentence.
        if (string.Equals(request.Adapter, Facilitation.Adapter, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, Facilitation.Reserved));
        }

        // The person's seat (§12.3): only a surface seating the person attaches under it. The row is written when the
        // store opens, so an attach under the name fires no storage rule; this comparison is the whole guard, and it
        // trusts the adapter the caller reports, as §16 concedes of every name. Without it an agent attached here
        // would speak with the person's standing — a person may always open a conversation (principle 2.10).
        if (PersonSeat.Is(name) != string.Equals(request.Adapter, PersonSeat.Adapter, StringComparison.Ordinal))
        {
            // Both directions (§12.3). The second is the one with the standing in it: the broker recognises the person by
            // this adapter, so an agent registering under it could name any conversation as only a person may — found by
            // the combined review gate, round 3, after round 1 closed only the first.
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                PersonSeat.Is(name)
                    ? PersonSeat.Reserved
                    : $"'{PersonSeat.Adapter}' is the adapter of the person's seat, so no agent can attach under it."));
        }

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
            attached = await _runtime.AttachAsync(
                registration, context.CancellationToken, Blank(request.LaunchToken)).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // A name held by another workspace, an unknown registration, a retirement: deployment faults
            // a person has to fix, none of them retryable.
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message, exception));
        }

        await using (attached.ConfigureAwait(false))
        {
            await responseStream.WriteAsync(
                new Wire.Event
                {
                    Attached = new Wire.Attached
                    {
                        AgentMarkedLive = attached.Started.AgentMarkedLive,
                        ExchangesRedelivered = attached.Started.ExchangesRedelivered
                    }
                },
                context.CancellationToken).ConfigureAwait(false);

            // Anything already waiting, before the stream goes quiet, so a participant that attaches
            // after a Request arrived hears about it now rather than at the next one.
            await _runtime.NudgeAsync(name, context.CancellationToken).ConfigureAwait(false);

            try
            {
                await foreach (var notice in attached.Notices.ReadAllAsync(context.CancellationToken)
                                   .ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(new Wire.Event { Notice = notice }, context.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away: the ordinary end of an attachment.
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<Wire.RequestReply> Request(Wire.OpenRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Caller(request.From);
        var conversation = Blank(request.ConversationId);

        // A bare role reaches every participant registered under it (§5.5); a whole name reaches one.
        // Told apart by shape rather than guessed at.
        IReadOnlyList<Parley.Broker.RequestOutcome> outcomes = AgentName.IsRole(request.To)
            ? await Run(
                from,
                (broker, now, ct) => broker.RequestRoleAsync(
                    from, request.To, request.ResponderModel, request.PayloadJson, now, conversation, ct),
                context).ConfigureAwait(false)
            : [await Run(
                from,
                (broker, now, ct) => broker.RequestAsync(
                    from, Name(request.To), request.ResponderModel, request.PayloadJson, now, conversation, ct),
                context).ConfigureAwait(false)];

        var reply = new Wire.RequestReply();

        foreach (var outcome in outcomes)
        {
            WakeOutcome? wake = outcome.Exchange is { } exchange
                ? await WakeAsync(exchange.Receiver, $"{from.Value} sent it a request", context).ConfigureAwait(false)
                : null;

            reply.Outcomes.Add(Conversions.Out(outcome, wake));
        }

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.SayReply> Clarify(Wire.ClarifyRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Caller(request.From);

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.ClarifyAsync(
                request.ExchangeId, from, request.ResponderModel, request.PayloadJson, now, ct, [.. request.Settles]),
            context).ConfigureAwait(false);

        await WakeRecipientAsync(outcome, context).ConfigureAwait(false);

        return Said(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.SayReply> Result(Wire.ResultRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Caller(request.From);

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.ResultAsync(
                request.ExchangeId, from, request.ResponderModel, request.PayloadJson, now, ct, [.. request.Settles]),
            context).ConfigureAwait(false);

        await WakeRecipientAsync(outcome, context).ConfigureAwait(false);

        // The Result may have closed the last work a poll was waiting behind.
        if (outcome is { Message: { } result, Stored: true })
        {
            await WakeSilentVotersAsync(result.ConversationId, context).ConfigureAwait(false);
        }

        return Said(outcome);
    }

    /// <inheritdoc/>
    public override async Task<Wire.ConcludeReply> Conclude(Wire.ConcludeRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var from = Caller(request.From);

        Vote vote;

        try
        {
            vote = Conversions.In(request.Vote);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "A vote must be DONE or NOT_DONE. An unset vote is not an answer, and a call that said " +
                "nothing must not be recorded as a participant having said something.",
                exception));
        }

        var outcome = await Run(
            from,
            (broker, now, ct) => broker.ConcludeAsync(request.ConversationId, from, vote == Vote.Done, now, ct),
            context).ConfigureAwait(false);

        // A vote that did not close it may have left it waiting on nothing but the silent. Each is asked again at most
        // once for each poll, whatever the votes do next — see WakeSilentVotersAsync for why that bound and not another.
        if (outcome is { Succeeded: true, Concluded: false })
        {
            await WakeSilentVotersAsync(request.ConversationId, context).ConfigureAwait(false);
        }

        return new Wire.ConcludeReply
        {
            Ok = outcome.Succeeded,
            Refusal = Conversions.Out(outcome.Error),
            Concluded = outcome.Concluded
        };
    }

    /// <inheritdoc/>
    public override async Task<Wire.ListAgentsReply> ListAgents(Wire.ListAgentsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var asking = Caller(request.AskingAs);

        var agents = await Run(asking, (broker, _, ct) => broker.ListAgentsAsync(ct), context).ConfigureAwait(false);

        var reply = new Wire.ListAgentsReply();
        reply.Agents.AddRange(agents.Select(Conversions.Out));

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.InboxReply> DrainInbox(Wire.DrainInboxRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Caller(request.Agent);

        // The single place a collection is observable, which is why the wake bound keys on it: an agent that
        // attached and has not collected has read nothing. Collected rather than run, so the bound is freed before
        // the drain reads and a message landing during it asks for its own turn.
        var inbox = await Run(
            agent,
            (broker, now, ct) => broker.DrainInboxAsync(agent, now, request.Limit > 0 ? request.Limit : null, ct),
            context,
            collecting: true).ConfigureAwait(false);

        // A collection its caller limited frees the bound for everything, and may have left messages behind that
        // nothing will now ask a turn for. Asked again here, once: the claim holds until the next collection, so a
        // caller draining one at a time costs a turn per collection and never a turn per message left.
        if (request.Limit > 0 && inbox.Messages.Count >= request.Limit)
        {
            var left = await _runtime.UseConnectionAsync(
                async (connection, _, ct) => await new MessageRepository(connection).CountPendingAsync(agent, ct)
                    .ConfigureAwait(false),
                context.CancellationToken).ConfigureAwait(false);

            if (left > 0)
            {
                _ = await WakeAsync(agent, "a collection it limited left messages waiting", context).ConfigureAwait(false);
            }
        }

        var reply = new Wire.InboxReply();
        reply.Messages.AddRange(inbox.Messages.Select(Conversions.Out));
        reply.ClosurePolls.AddRange(inbox.ClosurePolls.Select(Conversions.Out));

        // Never a reason to wake anything, even when some are left behind: a notice waits for the agent's next look.
        reply.FileNotices.AddRange(inbox.Notices.Notices.Select(Conversions.Out));
        reply.FileNoticesWaiting = inbox.Notices.StillWaiting;

        // What became of the caller's own proposals (SPEC.md section 5.7). Not a reason to wake anything either.
        reply.RoadmapNotices.AddRange(inbox.RoadmapNotices.Select(Conversions.Out));

        reply.RoadmapNoticesWaiting = await _runtime.UseConnectionAsync(
            async (connection, _, ct) => await new RoadmapRepository(connection).CountNoticesAsync(agent, ct)
                .ConfigureAwait(false),
            context.CancellationToken).ConfigureAwait(false);

        return reply;
    }

    /// <inheritdoc/>
    public override async Task<Wire.ProposeRoadmapReply> ProposeRoadmap(
        Wire.ProposeRoadmapRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var proposer = Caller(request.Agent);
        var content = Conversions.In(request.Content ?? new Wire.RoadmapContent());

        // Stores a proposal and nothing else (SPEC.md section 5.7): no wake, no exchange, no message. The one
        // roadmap operation an agent has, and the whole of what it may do with one (principle 2.12).
        var outcome = await Run(
            proposer,
            (broker, now, ct) => broker.ProposeRoadmapAsync(
                proposer, content, Blank(request.ConversationId), Blank(request.Revises), now, ct),
            context).ConfigureAwait(false);

        return new Wire.ProposeRoadmapReply
        {
            ProposalId = outcome.Id ?? 0,
            Problem = outcome.Problem ?? string.Empty,
        };
    }

    /// <inheritdoc/>
    public override async Task<Wire.Notice> Waiting(Wire.WaitingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Caller(request.Agent);

        // Not routed through an attachment: asked by things that hold no stream.
        return await _runtime.UseConnectionAsync(
            async (connection, _, ct) => new Wire.Notice
            {
                MessagesWaiting = await new MessageRepository(connection).CountPendingAsync(agent, ct)
                    .ConfigureAwait(false),
                ClosurePollsWaiting = await new ConversationRepository(connection)
                    .CountOutstandingPollsAsync(agent, ct).ConfigureAwait(false),
                FileNoticesWaiting = await new FileNoticeRepository(connection)
                    .CountWaitingAsync(agent, cancellationToken: ct).ConfigureAwait(false),
                RoadmapNoticesWaiting = await new RoadmapRepository(connection)
                    .CountNoticesAsync(agent, ct).ConfigureAwait(false)
            },
            context.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<Wire.Empty> RecordRefusal(Wire.RecordRefusalRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var agent = Caller(request.Agent);

        // Best effort, and silent when it fails: the adapter has already told the model its message was
        // too long, and that must not depend on the bookkeeping behind it.
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
    /// <remarks>
    /// Placing sends nothing and wakes nobody (SPEC.md §5): the Request or Result that needs the file names it, and
    /// that message does the waking.
    /// </remarks>
    public override Task<Wire.PlaceFileReply> PlaceFile(
        IAsyncStreamReader<Wire.PlaceFileFrame> requestStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).PlaceAsync(requestStream, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task FetchFile(
        Wire.FetchFileRequest request,
        IServerStreamWriter<Wire.FetchFileFrame> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).FetchAsync(request, responseStream, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task<Wire.FetchedFileReply> FetchedFile(Wire.FetchedFileRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).FetchedAsync(request, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task<Wire.FilesReply> ListFiles(Wire.ListFilesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).ListAsync(request, forOperator: false, context.CancellationToken);
    }

    /// <inheritdoc/>
    public override Task<Wire.RemoveFilesReply> RemoveOwnFiles(Wire.RemoveFilesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return new FileWork(_runtime).RemoveAsync(request, forOperator: false, context.CancellationToken);
    }

    private async Task<T> Run<T>(
        AgentName agent,
        Func<Parley.Broker.Broker, DateTimeOffset, CancellationToken, Task<T>> work,
        ServerCallContext context,
        bool collecting = false)
    {
        try
        {
            return collecting
                ? await _runtime.CollectAsync(agent, work, context.CancellationToken).ConfigureAwait(false)
                : await _runtime.RunAsync(agent, work, context.CancellationToken).ConfigureAwait(false);
        }
        catch (NotAttachedException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message, exception));
        }
        catch (ArgumentException exception)
        {
            // A payload the broker would not accept. Invalid rather than failed, because the same call with
            // a different payload would succeed.
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message, exception));
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            // A storage constraint refused the write — a Request with no ask, a source that is not
            // registered. Nothing was written. Reported as invalid with the constraint's own words, which
            // name the rule, rather than as a fault in the broker.
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message, exception));
        }
    }

    /// <summary>Tells a receiver something is waiting, and has it started or given a turn.</summary>
    /// <param name="receiver">Who the message went to.</param>
    /// <param name="because">Why, for whoever reads the node's log.</param>
    /// <param name="context">The call.</param>
    /// <returns>What became of the receiver.</returns>
    /// <remarks>
    /// A running receiver is nudged down its stream; one that is not is started or given a turn by its
    /// machine. Neither is required to succeed: a receiver on a machine that is off gets the message when
    /// it comes back, and the person is shown it waiting (§6).
    /// </remarks>
    private async Task<WakeOutcome> WakeAsync(AgentName receiver, string because, ServerCallContext context)
    {
        await _runtime.NudgeAsync(receiver, context.CancellationToken).ConfigureAwait(false);
        return _runtime.RequestAttentionForMail(receiver, because);
    }

    private async Task WakeRecipientAsync(Parley.Broker.SayOutcome outcome, ServerCallContext context)
    {
        // A retry that stored nothing has already woken its recipient once.
        if (outcome is { Message: { } message, Stored: true })
        {
            _ = await WakeAsync(message.To, $"{message.From.Value} is waiting on it", context).ConfigureAwait(false);
        }
    }

    /// <summary>Asks again the participants a conversation is waiting on for nothing but a vote (§5.0).</summary>
    /// <param name="conversationId">The conversation that just moved.</param>
    /// <param name="context">The call.</param>
    /// <returns>A task that completes when each has been woken.</returns>
    /// <remarks>
    /// <para>
    /// The poll reaches a participant through its inbox, and only a participant that collects sees it. One that
    /// finished its part has no reason to collect again, so without this the conversation stayed closing and
    /// nothing asked it. The reason names nobody: the poll leaks nothing about who else was asked (§5.0).
    /// </para>
    /// <para>
    /// <b>At most once for each participant in each poll.</b> Each turn is a collection and a collection frees the
    /// next wake, so any rule about <i>when</i> to ask was a rule a participant could replay: waking on every vote
    /// let one that had agreed buy a silent one a turn per call, and waking on a decline turning into agreement let
    /// it buy one for every two calls by alternating. The bound is on the thing being spent instead. A new poll —
    /// which only a new Request can bring about, by voiding this one — may ask again, and a broker that restarts
    /// forgets and may ask once more.
    /// </para>
    /// </remarks>
    private async Task WakeSilentVotersAsync(string? conversationId, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            return;
        }

        var silent = await _runtime.UseConnectionAsync(
            async (connection, _, ct) => await new ConversationRepository(connection)
                .GetSilentVotersAsync(conversationId, ct).ConfigureAwait(false),
            context.CancellationToken).ConfigureAwait(false);

        foreach (var (voter, polled) in silent)
        {
            if (_runtime.TryAskAgain(conversationId, voter, polled))
            {
                _ = await WakeAsync(voter, "a conversation it is in is waiting only on its vote", context).ConfigureAwait(false);
            }
        }
    }

    private static Wire.SayReply Said(Parley.Broker.SayOutcome outcome)
    {
        var reply = new Wire.SayReply
        {
            Ok = outcome.Succeeded,
            Refusal = Conversions.Out(outcome.Error),
            Deduplicated = outcome.Succeeded && !outcome.Stored,
            MessageId = outcome.Message?.Id ?? 0
        };

        reply.CannotSettle.AddRange(outcome.CannotSettle.Select(c => new Wire.SettleRefusal
        {
            ExchangeId = c.Exchange,
            Problem = Conversions.Out(c.Problem)
        }));
        reply.Settled.AddRange(outcome.Settled);
        reply.HandedBack.AddRange(outcome.HandedBack);
        return reply;
    }

    /// <summary>Refuses an adapter speaking a different protocol.</summary>
    /// <param name="announced">What the adapter said it speaks.</param>
    /// <exception cref="RpcException">It is not what this broker speaks.</exception>
    /// <remarks>
    /// Any difference is refused while the major is zero, because every minor carries breaking changes.
    /// Two versions that half-understand each other produce exchanges that open and are never answered,
    /// which looks exactly like a participant ignoring you.
    /// </remarks>
    private static void RequireTheSameProtocol(string announced)
    {
        if (string.Equals(announced, ProtocolVersion.Current, StringComparison.Ordinal))
        {
            return;
        }

        throw new RpcException(new Status(
            StatusCode.FailedPrecondition,
            $"This broker speaks Parley protocol {ProtocolVersion.Current} and that adapter speaks " +
            $"{(announced is { Length: > 0 } said ? said : "an older version that did not say")}. While the major " +
            "version is zero every minor carries breaking changes, so the two do not interoperate. Update " +
            "whichever is behind: both ship from the same build."));
    }

    private static AgentName Name(string value) =>
        AgentName.TryCreate(value, out var name)
            ? name
            : throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"'{value}' is not an agent name. Names are role@machine, lower case, and the machine half " +
                "is detected rather than configured (SPEC.md §4.1.1)."));

    /// <summary>Reads the name a call is made as, refusing the facilitator's.</summary>
    /// <param name="value">The name the caller claims.</param>
    /// <returns>The name.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every operation here that names its caller goes through this, not only attaching.</b> §16 concedes that
    /// anything able to reach a broker can claim to be any agent, and the facilitator is the one name that claim must
    /// not reach: its questions are the ones every agent is asked without having asked for anything, so a caller able
    /// to act under it could put words in the system's mouth (SPEC.md principle 2.11).
    /// </para>
    /// <para>
    /// Refused here, with a sentence, before storage refuses the row. The facilitator itself never calls this service:
    /// it writes through the broker's own process.
    /// </para>
    /// </remarks>
    private static AgentName Caller(string value)
    {
        var name = Name(value);

        return Facilitation.Is(name)
            ? throw new RpcException(new Status(StatusCode.PermissionDenied, Facilitation.Reserved))
            : name;
    }

    private static string? Blank(string value) => string.IsNullOrEmpty(value) ? null : value;
}
