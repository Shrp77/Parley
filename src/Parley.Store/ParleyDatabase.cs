using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// Opens connections to a Parley database with the settings its correctness depends on
/// already applied.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because <c>PRAGMA foreign_keys</c> is a <em>connection</em> setting in
/// SQLite, not a database one, and it defaults to off. A connection opened directly with
/// <c>new SqliteConnection(...)</c> therefore has referential integrity silently disabled:
/// an exchange referencing an unregistered agent is accepted without complaint, and the
/// failure surfaces much later as a rehydration error against a row that should never have
/// existed. The same applies to WAL mode and the busy timeout.
/// </para>
/// <para>
/// Documenting "remember to call Configure" would leave a footgun with a warning label on
/// it. Routing every connection through this factory makes the step unskippable instead.
/// Use these methods rather than constructing connections directly.
/// </para>
/// </remarks>
public static class ParleyDatabase
{
    /// <summary>
    /// Opens a configured connection to a database file, without applying migrations.
    /// </summary>
    /// <param name="path">Path to the database file. Created if it does not exist.</param>
    /// <returns>An open, configured connection. The caller owns its disposal.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="SqliteException">The database could not be opened.</exception>
    /// <remarks>
    /// <para>
    /// Use this for additional connections to a database already migrated by
    /// <see cref="OpenAndMigrate"/>, such as the reaper's or an adapter session's.
    /// </para>
    /// <para>
    /// On Unix the file is restricted to owner read and write. The database holds the full
    /// text of every question and answer exchanged between agents, which is a running record
    /// of a project's architectural decisions and boundaries; the default umask would leave
    /// that group-readable on a shared machine. Windows inherits the containing directory's
    /// ACL, so place the file inside a user-scoped directory there.
    /// </para>
    /// </remarks>
    public static SqliteConnection Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        var configured = Configured(connection);

        try
        {
            RestrictPermissions(path);
            return configured;
        }
        catch
        {
            configured.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a configured connection to a database file and brings its schema up to date.
    /// </summary>
    /// <param name="path">Path to the database file. Created if it does not exist.</param>
    /// <returns>An open, configured, migrated connection. The caller owns its disposal.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="SqliteException">The database could not be opened or migrated.</exception>
    /// <remarks>
    /// Intended to be called once during broker start-up. Migration is idempotent, so calling
    /// it on an already-current database costs one pragma read.
    /// </remarks>
    public static SqliteConnection OpenAndMigrate(string path)
    {
        var connection = Open(path);

        try
        {
            SchemaMigrator.Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a configured, migrated, private in-memory database.
    /// </summary>
    /// <returns>An open connection. The caller owns its disposal.</returns>
    /// <remarks>
    /// The database exists only for the lifetime of the returned connection and is not
    /// shared with any other connection. Intended for tests and for verifying that a schema
    /// change applies cleanly.
    /// </remarks>
    public static SqliteConnection OpenInMemory()
    {
        var connection = Configured(new SqliteConnection("Data Source=:memory:"));

        try
        {
            SchemaMigrator.Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection Configured(SqliteConnection connection)
    {
        try
        {
            connection.Open();
            SchemaMigrator.Configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Restricts the database and its WAL sidecars to the owning user on Unix.
    /// </summary>
    /// <param name="path">Path to the database file.</param>
    /// <remarks>
    /// Applied after the connection is opened, because SQLite creates the file itself. The
    /// sidecars may not exist yet on a fresh database; they are created with the same mode as
    /// the main file, so restricting it covers them. Windows is left to directory ACLs, which
    /// is the platform-idiomatic mechanism.
    /// </remarks>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
