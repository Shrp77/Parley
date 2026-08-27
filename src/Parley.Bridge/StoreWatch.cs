using Microsoft.Data.Sqlite;

namespace Parley.Bridge;

/// <summary>
/// Notices when anything in the store has changed, without asking any agent anything.
/// </summary>
/// <remarks>
/// <para>
/// The operator's objection to polling was about polling <em>agents</em>: a loop that pesters a
/// session on a timer, costing a turn each time, to ask a question the session would have
/// answered for free when it next did anything. That objection is right and nothing here does it.
/// </para>
/// <para>
/// What this does instead is read eight bytes from a local file. <c>PRAGMA data_version</c>
/// changes when another connection commits to the database, and does not change for this
/// connection's own writes -- so a single cheap read answers "has anybody done anything" without
/// running a single query against the tables. The expensive read only happens when the answer is
/// yes, and the browser is then pushed to and never polls at all.
/// </para>
/// <para>
/// The connection is deliberately long-lived. <c>data_version</c> is per-connection state: it is
/// only meaningful as a comparison against the previous value seen on the <em>same</em>
/// connection, so opening one per check would return a constant and this would report that
/// nothing ever happens.
/// </para>
/// </remarks>
public sealed class StoreWatch : IDisposable
{
    /// <summary>
    /// How often the version is checked.
    /// </summary>
    /// <remarks>
    /// Fast enough to feel immediate and cheap enough not to matter: the check is one pragma
    /// against a page that is certainly in the operating system's cache. It is unrelated to the
    /// agents' sweep cadence, which is a fact about the fleet rather than about this window.
    /// </remarks>
    public static TimeSpan Interval { get; } = TimeSpan.FromMilliseconds(400);

    private readonly SqliteConnection _connection;
    private long _seen;

    /// <summary>
    /// Opens a read-only watch on a store.
    /// </summary>
    /// <param name="databasePath">The store to watch.</param>
    /// <remarks>
    /// Read-only, and never <c>immutable</c>: every agent's server is writing to this file
    /// continuously, and a reader that took a lock they were waiting on would stall the
    /// deployment it exists to observe. <c>immutable</c> would be worse than a lock -- it
    /// promises SQLite the file will not change, which is the one thing known to be false.
    /// </remarks>
    public StoreWatch(string databasePath)
    {
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString());

        _connection.Open();
        _seen = Version();
    }

    /// <summary>
    /// Reports whether anybody has committed since this was last asked.
    /// </summary>
    /// <returns><see langword="true"/> if the store changed.</returns>
    public bool Changed()
    {
        var now = Version();

        if (now == _seen)
        {
            return false;
        }

        _seen = now;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();

    private long Version()
    {
        using var command = _connection.CreateCommand();

        // Not parameterisable: PRAGMA takes no parameters, and there is no user input here to
        // parameterise. CA2100 is satisfied because the text is a compile-time constant.
        command.CommandText = "PRAGMA data_version;";

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
