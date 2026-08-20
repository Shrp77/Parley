using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// Owns the lifecycle: what happens when the broker starts, and what happens on every tick.
/// </summary>
/// <remarks>
/// <para>
/// Everything below this line was already written, tested, and unreachable. The repositories
/// know how to orphan an exchange and the reaper knows when to; nothing decided <em>when to
/// ask</em>. That is all this type is, and keeping it that thin is the point — a broker that
/// grew its own persistence or its own state machine would be a third place for the rules to
/// live.
/// </para>
/// <para>
/// It owns no timer. Scheduling belongs to the host, and a broker that owned its own clock
/// could not be tested without one — the same reason <see cref="Reaper"/> takes an instant
/// rather than reading it.
/// </para>
/// <para>
/// <b>Not thread-safe, and the host must serialise access.</b> A broker holds one connection
/// for its lifetime, which contradicts the advice on the repositories to give each unit of work
/// its own — deliberately, because the two constraints pull against each other. A
/// <see cref="SqliteConnection"/> is not thread-safe, and neither is
/// <see cref="StartedUtc"/>, so calling <see cref="TickAsync"/> from a timer while
/// <see cref="ResumeOwedByAsync"/> runs on another thread is a data race in both.
/// </para>
/// <para>
/// A per-unit-of-work broker is not the answer either: <see cref="StartedUtc"/> is
/// per-process state that the ghosting clock depends on, and an instance created for one call
/// would not have it. So the resolution is that a host runs its brokers on one thread each,
/// which at this scale costs nothing — the work is a handful of indexed queries against a
/// local file, and §4.1 already contemplates one process hosting several brokers, one per
/// project, which parallelises across projects rather than within one.
/// </para>
/// <para>
/// What would change this is a host that needs concurrency inside a single project. The shape
/// that fits is a connection factory rather than a connection, so each operation opens its own
/// and <see cref="StartedUtc"/> stays on the broker. That is a larger change than it looks,
/// because the in-memory databases the tests use are private to their connection, and it is not
/// worth making against a host that does not exist yet.
/// </para>
/// </remarks>
public sealed class Broker
{
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;
    private readonly MessageRepository _messages;
    private readonly Reaper _reaper;
    private readonly BrokerOptions _options;

    /// <summary>SQLite's extended result code for a unique index violation.</summary>
    /// <remarks>
    /// The code says what kind of failure occurred and is stable; it does not say which index
    /// failed, and this table carries more than one. So the class of failure is matched on the
    /// code and the specific index on a column name — ours, from our own schema, rather than
    /// SQLite's prose around it. That is the part of the message that cannot drift without us
    /// changing it, and a rename that broke the match would break a test in the same commit.
    /// </remarks>
    private const int UniqueConstraintViolation = 2067;

    /// <summary>
    /// Creates a broker over an open connection.
    /// </summary>
    /// <param name="connection">
    /// An open, already-migrated connection to this project's database. The broker borrows it
    /// and never closes it: it outlives individual operations but not the host, and a type that
    /// disposed something it was handed would make the two lifetimes hard to reason about. The
    /// caller opens it, keeps it, and closes it.
    /// </param>
    /// <param name="options">Lifecycle configuration, or <see langword="null"/> for defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public Broker(SqliteConnection connection, BrokerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _options = options ?? BrokerOptions.Default;
        _options.Validate();

        _agents = new AgentRepository(connection);
        _exchanges = new ExchangeRepository(connection);
        _messages = new MessageRepository(connection);
        _reaper = new Reaper(_exchanges);
    }

    /// <summary>
    /// Gets when this broker last started, or <see langword="null"/> before it has.
    /// </summary>
    /// <remarks>
    /// Read by the ghosting clock, which runs from the later of this and the instant of
    /// orphaning. A broker that was down overnight returns to exchanges whose grace period
    /// elapsed while nothing could possibly have resumed them.
    /// </remarks>
    public DateTimeOffset? StartedUtc { get; private set; }

    /// <summary>
    /// Runs the reboot sequence.
    /// </summary>
    /// <param name="nowUtc">The instant the broker started.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>What the sequence did.</returns>
    /// <remarks>
    /// Call once per process lifetime. A second call is not refused, because refusing one would
    /// break the case this exists for, but it moves <see cref="StartedUtc"/> forward and with it
    /// the ghosting clock — so a host that calls it twice by accident silently delays ghosting
    /// for every orphan, with nothing to notice.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// SPEC.md §14: mark agents offline, then orphan every open exchange. Both are the same
    /// admission — that whatever the previous run believed about liveness is no longer evidence
    /// of anything. An exchange left open across a restart would keep a deadline nobody is
    /// working towards, and a wait-for edge blocking peers on behalf of an agent that is not
    /// running.
    /// </para>
    /// <para>
    /// Retired agents are spared, and only retired agents. Every other status is what the last
    /// run observed and the restart may well have invalidated it — reinstalling or re-cloning a
    /// repository is exactly the kind of thing that happens between runs, which is why an
    /// <c>unreachable</c> mark is cleared here and gets to be proved again. Retirement is a
    /// person's judgement, and a restart is not evidence against it.
    /// </para>
    /// <para>
    /// Orphaning goes through the domain transition one exchange at a time rather than a bulk
    /// update, which is slower and correct for two reasons. The transition is where the rule
    /// lives, and it stamps the instant of orphaning onto <c>expires_utc</c>, which is what the
    /// grace period is later measured from. A bulk <c>UPDATE</c> would have to reproduce both,
    /// and reproducing a rule is how the two copies come to disagree.
    /// </para>
    /// </remarks>
    public async Task<StartResult> StartAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var offline = await _agents.MarkAllOfflineAsync(cancellationToken).ConfigureAwait(false);

        var open = await _exchanges.GetOpenAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var orphaned = 0;

        foreach (var exchange in open)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!exchange.Orphan(nowUtc).Succeeded)
            {
                continue;
            }

            if (await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                orphaned++;
            }
        }

        // Recorded last, so a sequence that throws part-way leaves a broker that refuses to
        // tick rather than one that ticks having half-orphaned its exchanges. TickAsync reads
        // this to time ghosting, and a start that did not finish is not a start.
        StartedUtc = nowUtc;

        return new StartResult(offline, orphaned);
    }

    /// <summary>
    /// Runs one maintenance pass.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate deadlines against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartAsync"/> has not been called.
    /// </exception>
    /// <remarks>
    /// Refuses to run before the reboot sequence, because the ghosting clock is measured from
    /// broker start and a sweep without one would fall back to wall-clock timing — ghosting
    /// every orphan that outlived an outage the instant the process came back, which is the
    /// exact failure the clock rule exists to prevent. Failing loudly beats a sweep that looks
    /// like it worked.
    /// </remarks>
    public async Task<ReapResult> TickAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (StartedUtc is not { } startedUtc)
        {
            throw new InvalidOperationException(
                "The broker has not started. Call StartAsync first: ghosting is measured from " +
                "broker start, and a sweep without one would ghost every orphan that outlived " +
                "an outage as soon as the process returned.");
        }

        return await _reaper.SweepAsync(nowUtc, startedUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains an agent's inbox, delivering what is waiting for it.
    /// </summary>
    /// <param name="agent">The agent collecting its messages.</param>
    /// <param name="nowUtc">The instant of the drain.</param>
    /// <param name="limit">The most messages to return, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>The messages delivered, debts first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// This is the inbound traffic SPEC.md §14 describes, so it does the replay before the
    /// read. Nothing is replayed at start-up: the reboot sequence orphans everything, and an
    /// agent collecting its messages is what brings back what that agent owes, so a deployment
    /// does not pay to instantiate agents that would have stayed idle. Under a resume policy of
    /// <see cref="Core.ResumePolicy.Never"/> the replay finds nothing and the drain is simply a
    /// read.
    /// </para>
    /// <para>
    /// Delivery is recorded on two rows and both matter. The message is marked delivered, which
    /// is what stops it being handed out again. The exchange is told, which starts its working
    /// window — and that is the whole reason §6 could split one deadline into two without
    /// inventing an acknowledgement message. Until this runs, silence means nobody collected the
    /// question; afterwards it means somebody has it and is working.
    /// </para>
    /// <para>
    /// A Notify has no exchange and only the message is marked. It is the one type where
    /// delivery says nothing about anybody's deadline, because nothing is owed in return.
    /// </para>
    /// <para>
    /// Delivery is recorded per message rather than in one write at the end. A drain
    /// interrupted half way has delivered what it returned and nothing more, which is the
    /// property a caller needs: it may act on what it received without knowing whether the rest
    /// arrived.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> DrainInboxAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        // Traffic for this agent is what triggers the replay, so it happens before the read and
        // anything it brings back is in the drain that asked for it rather than the next one.
        await ResumeOwedByAsync(agent, nowUtc, cancellationToken).ConfigureAwait(false);

        var pending = await _messages.GetPendingAsync(agent, limit, cancellationToken)
            .ConfigureAwait(false);

        var delivered = new List<Message>(pending.Count);

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The window is started before the message is stamped, and the order is the whole
            // point rather than an accident. These are two writes and nothing makes them atomic,
            // so one of them can be the last thing that happens, and the two failures are not
            // equally bad.
            //
            // This way round, a crash in between leaves an exchange with its long window and a
            // message still pending: the next drain hands it over again, which costs the agent a
            // repeated question. The other way round leaves a message marked delivered against
            // an exchange still on its short window, so it orphans within minutes while the
            // agent is working on it, and the answer that eventually arrives is refused because
            // the exchange is no longer open. A repeated question is recoverable; a discarded
            // answer is not.
            if (message.ExchangeId is { } exchangeId)
            {
                await StartWorkingWindow(exchangeId, nowUtc, cancellationToken).ConfigureAwait(false);
            }

            if (!await _messages.MarkDeliveredAsync(message.Id, nowUtc, cancellationToken)
                .ConfigureAwait(false))
            {
                // Already delivered by a drain that raced this one. Skipped rather than returned
                // twice, because a caller acting on the same question twice is the failure the
                // delivered stamp exists to prevent.
                //
                // Unreachable as things stand, and kept deliberately. Getting here needs a
                // second writer to stamp the row between the pending read and this write, which
                // needs two connections racing — something this type's threading contract
                // forbids. It is defence against a deployment that later relaxes that contract,
                // in the same spirit as the contention counter in Reaper.SweepAsync, and its
                // absence would turn that relaxation into duplicate questions rather than an
                // error.
                continue;
            }

            delivered.Add(message);
        }

        return delivered;
    }

    /// <summary>
    /// Tells an exchange its question has been collected.
    /// </summary>
    /// <remarks>
    /// Silent when the exchange is no longer open. Between the pending read and here it may have
    /// been orphaned by a sweep or closed by its answerer, and neither is a reason to fail the
    /// drain — the message was genuinely delivered, and an exchange that is not open has no
    /// working window to start.
    /// <para>
    /// The state half of that guard is defensive rather than load-bearing:
    /// <see cref="Exchange.Deliver"/> refuses a closed exchange itself and leaves it unmutated,
    /// so removing the check changes only that a save would be issued for a row nothing
    /// changed. Worth keeping anyway, because that save still bumps the version stamp and would
    /// make a concurrent write fail for no reason. The null half is load-bearing and is not
    /// covered by a test, because a message whose exchange has vanished is something the foreign
    /// key makes impossible to construct.
    /// </para>
    /// </remarks>
    private async Task StartWorkingWindow(
        long exchangeId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var exchange = await _exchanges.GetAsync(exchangeId, cancellationToken).ConfigureAwait(false);

        if (exchange is null || !exchange.Deliver(nowUtc).Succeeded)
        {
            return;
        }

        await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replays orphaned exchanges the owing agent has yet to answer.
    /// </summary>
    /// <param name="agent">The agent that owes the answers.</param>
    /// <param name="nowUtc">The instant of the replay.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>The exchanges returned to the open state.</returns>
    /// <remarks>
    /// <para>
    /// Lazy, per SPEC.md §14: nothing is replayed at start-up. The reboot sequence orphans
    /// everything and the first inbound traffic for an agent brings back what that agent owes,
    /// so a deployment does not pay to instantiate agents that would have stayed idle.
    /// </para>
    /// <para>
    /// Returns an empty list under <see cref="Core.ResumePolicy.Never"/> and
    /// <see cref="Core.ResumePolicy.Ask"/> without consulting the gate. Never means the operator has
    /// said no already; ask means somebody must be asked, and asking is an operator surface this
    /// type does not have — so it declines rather than inventing consent. Both leave the orphans
    /// in place, and the grace period bounds how long that lasts.
    /// </para>
    /// <para>
    /// A replay whose question is already open again is skipped rather than replayed, and this
    /// is the one place where a refusal arrives as an exception rather than a false return.
    /// <see cref="ExchangeRepository.SaveAsync"/> reports a stale write by returning false, but
    /// here the write is not stale: a <em>different</em> row holds the same question, so the
    /// distinctness index raises a constraint violation. Catching it keeps one collision from
    /// abandoning the replay of everything else the agent owes.
    /// </para>
    /// <para>
    /// The re-ask therefore wins, which is what SPEC.md §17 open question 9 settles. The orphan
    /// stays orphaned and the grace period disposes of it. What that question leaves open is the
    /// incentive: the surviving re-ask starts at clarify depth zero while the orphan held the
    /// accumulated depth.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Exchange>> ResumeOwedByAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (_options.ResumePolicy != ResumePolicy.Always)
        {
            return [];
        }

        var orphans = await _exchanges.GetOrphanedOwedByAsync(agent, cancellationToken)
            .ConfigureAwait(false);

        var resumed = new List<Exchange>();

        foreach (var exchange in orphans)
        {
            // Defensive rather than load-bearing: every awaited call below already honours the
            // token, so removing this changes no observable behaviour today. It keeps the loop
            // cancellable independently of what the body happens to do, which a body that gained
            // a synchronous step would otherwise lose.
            cancellationToken.ThrowIfCancellationRequested();

            // A gate that throws is not caught, and the asymmetry with the constraint violation
            // below is deliberate. That violation is an expected outcome of ordinary operation —
            // an agent re-asked — so it is handled and the pass continues. A gate is the host's
            // own code, and an exception from it is a defect the host needs to see rather than
            // one the broker should absorb. Expected outcomes get handled; defects get surfaced.
            if (!await _options.ResumeGate.MayResumeAsync(exchange, cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            if (!exchange.Resume(nowUtc).Succeeded)
            {
                continue;
            }

            try
            {
                if (!await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }
            }
            catch (SqliteException ex)
                when (ex.SqliteExtendedErrorCode == UniqueConstraintViolation
                      && ex.Message.Contains("question_hash", StringComparison.Ordinal))
            {
                // The asker re-asked while this was orphaned, so an identical question is open
                // again and the distinctness index refuses the replay. That is the outcome
                // SPEC.md §17 open question 9 settles — the re-ask reflects current intent and
                // wins — but it arrives as an exception rather than a refused write, because it
                // is a different row that conflicts rather than this one being stale.
                //
                // Skipped rather than propagated: one collision must not abandon the replay of
                // every other exchange the agent owes. The orphan stays orphaned and the grace
                // period disposes of it.
                //
                // Narrowed to the distinctness index rather than any unique violation, because
                // this table carries another one and may gain more. Swallowing an unexpected
                // constraint failure would turn a schema defect into a replay that quietly did
                // nothing, which is the worst of both.
                continue;
            }

            // Reopening the exchange is only half a replay. SPEC.md §14 is explicit that a
            // replay is re-delivery rather than re-sending, so the message the agent owes an
            // answer to goes back into its inbox — the existing row, marked undelivered, not a
            // fresh one that would make the log a record of delivery attempts.
            //
            // Without this the exchange reopens and the agent has an obligation it cannot see:
            // its inbox is empty because the question is still stamped delivered from before the
            // interruption, while the broker waits for an answer nobody was asked for, until the
            // grace period ghosts it.
            var owed = await _messages
                .GetLatestOwedAsync(exchange.Id, agent, cancellationToken)
                .ConfigureAwait(false);

            if (owed is not null)
            {
                await _messages.RedeliverAsync(owed.Id, cancellationToken).ConfigureAwait(false);
            }

            resumed.Add(exchange);
        }

        return resumed;
    }
}
