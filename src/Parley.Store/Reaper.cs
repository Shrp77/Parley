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
/// This is also the source of the retention gap noted in SPEC.md §9.4. The floor keeps the
/// messages of an orphaned exchange so §14 can still replay them, and under a <c>never</c>
/// resume policy nothing ever clears such an exchange — so every sweep manufactures data
/// that the age cap will not remove. Sweeping is still right: leaving the pair slot held
/// forever is worse than holding the text. But the two rules only compose once §17 open
/// question 7 decides what becomes of an orphan nobody comes back for, and until then a
/// deployment that reaps steadily and resumes never will grow without bound.
/// </para>
/// </remarks>
/// <param name="exchanges">The repository to sweep.</param>
public sealed class Reaper(ExchangeRepository exchanges)
{
    private readonly ExchangeRepository _exchanges =
        exchanges ?? throw new ArgumentNullException(nameof(exchanges));

    /// <summary>
    /// Orphans every open exchange whose deadline has passed.
    /// </summary>
    /// <param name="nowUtc">The instant to evaluate deadlines against.</param>
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
        CancellationToken cancellationToken = default)
    {
        var expired = await _exchanges.GetExpiredAsync(nowUtc, cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return ReapResult.Nothing;
        }

        var orphaned = 0;
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

        return new ReapResult(orphaned, contended);
    }
}
