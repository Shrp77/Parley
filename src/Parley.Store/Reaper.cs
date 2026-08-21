using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Releases the pair slots held by exchanges whose deadline has passed.
/// </summary>
/// <remarks>
/// <para>
/// An agent that vanishes mid-question would otherwise hold its pair slot forever, and with
/// it a wait-for edge: its peer cannot ask it anything, and any cycle running through that
/// edge stays closed. The TTL exists so that failure is bounded, and this is what makes the
/// TTL mean something. Without a sweep, <see cref="Exchange.ExpiresUtc"/> is a timestamp
/// nobody reads.
/// </para>
/// <para>
/// Orphaning preserves the question rather than discarding it. The pair slot and the
/// wait-for edge are released so the rest of the system keeps moving, while the exchange
/// remains available for replay when the owing agent returns — subject to the resume policy
/// in SPEC.md §14, which is the operator's decision and not this type's.
/// </para>
/// <para>
/// One sweep per call. There is no timer here: scheduling belongs to the host, and a reaper
/// that owned its own clock could not be tested without one.
/// </para>
/// <para>
/// One sweep does both halves of the lifecycle, because they are the same walk over the same
/// deadlines and separating them would mean two schedules to keep in step. Orphaning releases
/// what a vanished agent was holding; ghosting closes what it never came back for. The second
/// is what makes the retention age cap in SPEC.md §9.4 absolute — without it a <c>never</c>
/// resume policy accumulates orphans nothing ever clears, and every sweep manufactures data
/// the age cap will not remove.
/// </para>
/// </remarks>
/// <param name="exchanges">The repository to sweep.</param>
/// <param name="messages">Where the ghost notice of SPEC.md §14 is written.</param>
/// <param name="connection">The connection, for the transaction a ghost is written under.</param>
public sealed class Reaper(
    ExchangeRepository exchanges,
    MessageRepository messages,
    SqliteConnection connection)
{
    /// <summary>
    /// What SPEC.md §11.3 reserves for the broker's own attributions.
    /// </summary>
    private const string BrokerResponder = "parley:broker";

    private readonly ExchangeRepository _exchanges =
        exchanges ?? throw new ArgumentNullException(nameof(exchanges));

    private readonly MessageRepository _messages =
        messages ?? throw new ArgumentNullException(nameof(messages));

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Closes an exchange as ghosted and tells its asker, in one transaction.
    /// </summary>
    /// <param name="exchange">The exchange, already transitioned in memory.</param>
    /// <param name="nowUtc">The instant of the close.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if it was closed and the notice written.</returns>
    /// <remarks>
    /// <para>
    /// SPEC.md §14 has ghosting close the exchange <em>and</em> tell the asker that no answer is
    /// coming. The two are one write for a reason that is not tidiness: the notice is what makes
    /// ghosting non-destructive, and an exchange is ghostable exactly once. If the close
    /// committed and the notice did not, nothing would ever retry — the exchange is no longer
    /// orphaned, so the next sweep passes over it, and the asker waits for a message that will
    /// never be written now. Silence is the one outcome §14 exists to prevent.
    /// </para>
    /// <para>
    /// The notice is written first so that the trigger asserting a ghost carries no answer sees
    /// what it is meant to see. It fires on the exchange update and looks for a message of type
    /// <c>answer</c>; a ghost is a type of its own precisely so that this ordering is a detail
    /// rather than a loophole.
    /// </para>
    /// <para>
    /// A notice the store deduplicates counts as contention for the same reason a refused save
    /// does: it means this ghost has already been written, so somebody else is committing the
    /// close, and this attempt has nothing to add.
    /// </para>
    /// <para>
    /// A refused save means another writer changed the exchange between the scan and here —
    /// usually because it was answered after all — and rolling back takes the notice with it, so
    /// the asker is not told nobody answered by the same sweep that lost the race to somebody
    /// answering.
    /// </para>
    /// </remarks>
    private async Task<bool> GhostAsync(
        Exchange exchange, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        using var scope = WriteScope.Begin(_connection, ambient: null);

        var question = await _messages
            .GetQuestionAsync(exchange.Id, cancellationToken).ConfigureAwait(false);

        var notice = await _messages.AppendAsync(
            exchange.Id,
            MessageType.Ghost,
            // From the agent that owed the answer, because from_agent names a registered agent
            // and the broker is not one — §11.3 puts the broker's identity in the responder,
            // which is what the envelope renders and what §15 counts.
            exchange.Answerer,
            exchange.Asker,
            BrokerResponder,
            GhostNotice.For(exchange.Answerer, question?.PayloadJson),
            nowUtc,
            // Taken from the question rather than reconstructed, because messages carry a copy
            // of their exchange's conversation held in agreement by a composite foreign key
            // (§9.2). Reading the copy is how the notice lands in the same discussion without a
            // second opinion about which one that is.
            question?.ConversationId,
            scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        // Checked rather than assumed, on the one invariant this method exists to hold. A
        // deduplicated append writes no row and says so in the outcome instead of throwing, so
        // ignoring it would let the close commit beside a notice that is not there — which is
        // exactly "ghosted with nobody told", reached by the one path that does not look like a
        // failure. It cannot currently happen: the content hash covers the exchange id and an
        // exchange is ghostable once. That is an argument, and this is a check.
        if (!notice.Stored)
        {
            return false;
        }

        if (!await _exchanges
                .SaveAsync(exchange, transaction: scope.Transaction,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        scope.Complete();
        return true;
    }

    /// <summary>
    /// Orphans every open exchange whose deadline has passed.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate deadlines against.</param>
    /// <param name="brokerStartUtc">
    /// When the broker most recently started. The ghosting clock runs from the later of this
    /// and the instant of orphaning, so an outage does not consume an orphan's grace period.
    /// </param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>What the sweep did.</returns>
    /// <remarks>
    /// <para>
    /// Each exchange is written independently rather than under one transaction covering the
    /// whole sweep, and that is the better arrangement rather than a compromise. Admission
    /// control takes an <c>IMMEDIATE</c> write lock, so a sweep holding one across every
    /// update would block every question for its duration — turning a background tidy-up into
    /// a stall proportional to how much work it found. Independent writes let admission
    /// interleave.
    /// </para>
    /// <para>
    /// Nothing is lost by that. The exchanges are unrelated to one another, so a sweep
    /// interrupted halfway leaves the remainder expired but untouched, and the next sweep
    /// finds them exactly as this one did. There is no state that is only correct once all of
    /// them have been written.
    /// </para>
    /// <para>
    /// Each write is guarded on the version stamp read during the scan. A refused write means
    /// the exchange changed after it was read — most often because it was answered in the
    /// interval — so it is left alone and counted as contended. Reloading and retrying would
    /// be wrong: the transition this sweep intended was "orphan the thing that expired", and
    /// the thing that expired no longer exists in that form.
    /// </para>
    /// <para>
    /// That contention path needs two connections racing and so is not covered by the test
    /// suite, which drives a single connection and cannot interleave a write between this
    /// scan and this save. The guarantee it rests on is covered:
    /// <see cref="ExchangeRepository.SaveAsync"/> is tested to refuse a stale write and to
    /// preserve what the other writer did. What is untested here is the counting, not the
    /// safety.
    /// </para>
    /// <para>
    /// The expired set is loaded whole. It is bounded by the number of registered agents
    /// rather than by history — at most one open exchange per pair — so it stays small for the
    /// same reason <see cref="ExchangeRepository.GetOpenAsync"/> can be read in full.
    /// </para>
    /// </remarks>
    public async Task<ReapResult> SweepAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset brokerStartUtc,
        CancellationToken cancellationToken = default)
    {
        var expired = await _exchanges.GetExpiredAsync(nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var orphaned = 0;
        var ghosted = 0;
        var contended = 0;

        foreach (var exchange in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Unreachable as things stand, and kept deliberately. GetExpiredAsync filters on
            // state = 'OPEN' and Orphan refuses only a non-open exchange, so this cannot fire
            // today. It exists so that narrowing or widening that query later fails safe: the
            // state machine, not the WHERE clause, decides what may be orphaned, and a query
            // change that started returning closed rows would be refused here rather than
            // quietly reopening settled exchanges.
            if (!exchange.Orphan(nowUtc).Succeeded)
            {
                contended++;
                continue;
            }

            if (await _exchanges.SaveAsync(exchange, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                orphaned++;
            }
            else
            {
                contended++;
            }
        }

        // Read after the orphaning pass rather than before, so an exchange orphaned by this
        // sweep is considered for ghosting on the next one. Ghosting something the same sweep
        // just orphaned would mean an exchange whose grace period was zero, which the schema
        // forbids anyway.
        var abandoned = await _exchanges
            .GetGhostableAsync(nowUtc, brokerStartUtc, cancellationToken)
            .ConfigureAwait(false);

        foreach (var exchange in abandoned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Unreachable as things stand, for the same reason the orphan guard above is: the
            // query filters on state = 'ORPHANED' and Ghost refuses anything else. It exists so
            // that narrowing or widening that query later fails safe.
            if (!exchange.Ghost(nowUtc).Succeeded)
            {
                contended++;
                continue;
            }

            if (await GhostAsync(exchange, nowUtc, cancellationToken).ConfigureAwait(false))
            {
                ghosted++;
            }
            else
            {
                contended++;
            }
        }

        return new ReapResult(orphaned, ghosted, contended);
    }
}
