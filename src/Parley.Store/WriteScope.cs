using System.Data;
using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// A write that either owns its transaction or joins one already in progress.
/// </summary>
/// <remarks>
/// <para>
/// This exists because several repository operations are individually atomic and were being
/// composed into operations that were not. Opening an exchange and writing its Request are one
/// act from the protocol's point of view — SPEC.md §10.1 calls it <c>request</c> — but two commits
/// here, and a failure between them leaves an exchange open with no Request in it. That exchange
/// holds an entry in the distinctness index and owes a Result on work nobody sent, and it is
/// flagged to the person as never collected: nobody came, when nobody was ever asked.
/// </para>
/// <para>
/// The alternative was to validate before writing, which is worse. The rules that reject a
/// Request live in the schema deliberately — rule 8.5's length cap and rule 8.1's done_when floor
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
/// <para>
/// Public rather than internal, because the repository methods take a transaction as a public
/// parameter and this holds the only piece of knowledge needed to supply a correct one. Left
/// internal, every external caller would have to rediscover <c>IsolationLevel.Serializable</c>
/// with <c>deferred: false</c> — and the failure of getting it wrong is not a compile error but
/// a check that reads a stale snapshot and passes when it should not.
/// </para>
/// </remarks>
public sealed class WriteScope : IDisposable
{
    private readonly SqliteTransaction? _owned;

    private WriteScope(SqliteTransaction transaction, SqliteTransaction? owned)
    {
        Transaction = transaction;
        _owned = owned;
    }

    /// <summary>Gets the transaction the work runs in, owned or borrowed.</summary>
    /// <value>
    /// Pass this to every command issued inside the scope — but not because forgetting would
    /// fail. An earlier version of this comment claimed Microsoft.Data.Sqlite refuses a command
    /// that carries no transaction while one is open, and a probe showed otherwise: the command
    /// runs, joins the connection's transaction anyway, and is rolled back with it. That is the
    /// safe direction, and it is worth stating accurately rather than leaving a warning nobody
    /// can act on.
    /// <para>
    /// What the parameter actually buys is that the intent is visible where the query is, so a
    /// reviewer can see which reads a decision was made against without tracing the call stack
    /// to find out whether a transaction happened to be open. The load-bearing part of this type
    /// is <see cref="Begin"/>'s own-or-borrow decision, not the pass-through.
    /// </para>
    /// </value>
    public SqliteTransaction Transaction { get; }

    /// <summary>
    /// Joins <paramref name="ambient"/>, or begins an <c>IMMEDIATE</c> transaction if it is
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="connection">The connection to begin on when there is nothing to join.</param>
    /// <param name="ambient">A transaction to join, or <see langword="null"/> to own one.</param>
    /// <returns>A scope over the transaction the work should use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
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
        ArgumentNullException.ThrowIfNull(connection);

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
