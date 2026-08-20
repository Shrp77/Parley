using System.Data;
using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// A write that either owns its transaction or joins one already in progress.
/// </summary>
/// <remarks>
/// <para>
/// This exists because several repository operations are individually atomic and were being
/// composed into operations that were not. Opening an exchange and writing its question are one
/// act from the protocol's point of view — SPEC.md §10.1 calls it <c>ask</c> — but two commits
/// here, and a failure between them leaves an exchange open with no question in it. That
/// exchange holds a pair slot and an entry in the distinctness index, waits out its delivery
/// window, and is finally reported to the asker as <c>ghosted</c>: nobody came, when nobody was
/// ever asked. Five of them saturate the pair and lock it out entirely.
/// </para>
/// <para>
/// The alternative was to validate before writing, which is worse. The rules that reject a
/// question live in the schema deliberately — §8.5's length cap and rule 1's consequences floor
/// are there so no code path can route around them — and a broker that re-implemented them to
/// pre-check would be the second copy those constraints exist to avoid.
/// </para>
/// <para>
/// A borrowed transaction is neither committed nor disposed here. Ownership stays with whoever
/// began it, which is the only arrangement under which a caller can span several repositories:
/// a callee that committed would end the caller's transaction underneath it, and one that
/// disposed would roll back work the caller had not finished.
/// </para>
/// <para>
/// <see cref="SqliteConnection"/> has no nested transactions, so a borrowed transaction is not
/// an optimisation but the only way to compose these at all. Beginning a second while one is
/// open throws.
/// </para>
/// </remarks>
internal sealed class WriteScope : IDisposable
{
    private readonly SqliteTransaction? _owned;

    private WriteScope(SqliteTransaction transaction, SqliteTransaction? owned)
    {
        Transaction = transaction;
        _owned = owned;
    }

    /// <summary>Gets the transaction the work runs in, owned or borrowed.</summary>
    public SqliteTransaction Transaction { get; }

    /// <summary>
    /// Joins <paramref name="ambient"/>, or begins an <c>IMMEDIATE</c> transaction if it is
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="connection">The connection to begin on when there is nothing to join.</param>
    /// <param name="ambient">A transaction to join, or <see langword="null"/> to own one.</param>
    /// <returns>A scope over the transaction the work should use.</returns>
    /// <remarks>
    /// <c>IMMEDIATE</c> rather than deferred, for the reason set out on
    /// <see cref="ExchangeRepository.TryOpenAsync"/>: a deferred transaction takes its read
    /// snapshot at the first <c>SELECT</c> and only asks for the write lock at the
    /// <c>INSERT</c>, which WAL refuses if another connection committed in between — and
    /// <c>busy_timeout</c> cannot help, because waiting never makes a stale snapshot fresh.
    /// Taking the lock up front is what makes a read-then-write check sound rather than
    /// advisory.
    /// <para>
    /// Synchronous, deliberately: the async overloads do not expose the deferred flag, and
    /// beginning a transaction is a local operation with no I/O to await.
    /// </para>
    /// </remarks>
    public static WriteScope Begin(SqliteConnection connection, SqliteTransaction? ambient)
    {
        if (ambient is not null)
        {
            return new WriteScope(ambient, owned: null);
        }

        var owned = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        return new WriteScope(owned, owned);
    }

    /// <summary>
    /// Commits, if this scope owns its transaction.
    /// </summary>
    /// <remarks>
    /// A no-op on a borrowed transaction. The caller that began it decides when the whole
    /// composite operation is complete, and a callee committing early would publish half of it.
    /// </remarks>
    public void Complete() => _owned?.Commit();

    /// <inheritdoc />
    /// <remarks>
    /// Disposing an uncommitted owned transaction rolls it back, which is what makes an
    /// exception anywhere above leave nothing behind. A borrowed transaction is left alone.
    /// </remarks>
    public void Dispose() => _owned?.Dispose();
}
