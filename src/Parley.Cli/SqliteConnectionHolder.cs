using Microsoft.Data.Sqlite;

namespace Parley.Cli;

/// <summary>
/// Owns the connection a command runs against.
/// </summary>
/// <remarks>
/// A named type rather than a <c>using</c> on the connection itself, because the entry point is
/// a file of top-level statements: a local declared in a <c>try</c> is not in scope after it, and
/// the alternative shapes either widen the <c>try</c> around every command or leave the
/// connection undisposed on the paths that return early.
/// </remarks>
/// <param name="connection">The open, migrated connection. This takes ownership.</param>
internal sealed class SqliteConnectionHolder(SqliteConnection connection) : IDisposable
{
    /// <summary>The connection.</summary>
    public SqliteConnection Connection { get; } = connection;

    /// <inheritdoc />
    public void Dispose() => Connection.Dispose();
}
