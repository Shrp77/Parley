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

        try
        {
            connection.Open();

            // Restrict before configuring, not after. Enabling WAL is what creates the -wal
            // and -shm sidecars, and SQLite mints them from the mode the main database file
            // carries at that moment. Configuring first derives both sidecars from the
            // default umask and leaves them group-readable, even though the main file is
            // tightened a moment later — which defeats the protection for the most recently
            // written pages, the ones most likely to matter.
            RestrictPermissions(path);

            SchemaMigrator.Configure(connection);

            // Again, so the sidecars are covered whether or not they inherited, and so a
            // database carrying sidecars from an earlier run is corrected on open.
            RestrictPermissions(path);

            return connection;
        }
        catch
        {
            connection.Dispose();
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
    /// For tests and new stores. It takes no copy first, so the broker does not use it: it opens with <see cref="Open"/>,
    /// copies with <see cref="BackUpBeforeMigrating"/> under the store's lock, then migrates (SPEC.md §9.2).
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
    /// Copies a store that is about to be migrated to a file beside it, and says where.
    /// </summary>
    /// <param name="connection">An open connection to the store, not yet migrated.</param>
    /// <param name="path">The store's file.</param>
    /// <param name="nowUtc">The instant, which names the copy so an earlier one is never overwritten.</param>
    /// <returns>The copy's path, or <see langword="null"/> when nothing is to be migrated or the store is new.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The copy could not be made; the store is then not migrated.</exception>
    /// <remarks>
    /// <para>
    /// <b>A live store holds work in flight and is upgraded in place</b> (SPEC.md §9.2), so the store as the previous
    /// build left it is kept for the span between that build stopping and the new one serving, when an upgrade or the
    /// start after it could still go wrong. The copy is SQLite's online backup, taken under the store's lock before the
    /// first migration's transaction. A store with no schema yet is new and has nothing to keep.
    /// </para>
    /// <para>
    /// <b>Kept only until the upgraded store is served</b>, when <c>serve</c> deletes every copy of the store: it holds
    /// every conversation's text, which destroy, purge and retention exist to remove. Restricted to the owner on Unix as
    /// the store is, and a copy that fails part way is deleted rather than left looking like one that could be restored.
    /// </para>
    /// </remarks>
    public static string? BackUpBeforeMigrating(SqliteConnection connection, string path, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var version = SchemaMigrator.GetUserVersion(connection);

        if (version == 0 || version >= SchemaMigrator.LatestVersion)
        {
            return null;
        }

        var named = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{path}{Parley.Core.StoreFiles.BeforeSchema}{SchemaMigrator.LatestVersion}-{nowUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
        var copy = named;

        // Two starts in one second (a failed migration retried) would otherwise write over the first copy.
        for (var again = 2; File.Exists(copy); again++)
        {
            copy = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{named}-{again}");
        }

        try
        {
            // Made owner-only before SQLite opens it, so no other account can hold it open from the moment SQLite would
            // have created it until RestrictPermissions narrowed it (lens 2 run 93). SQLite takes an empty file as an empty
            // database.
            if (!OperatingSystem.IsWindows())
            {
                using var _ = new FileStream(
                    copy,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    });
            }

            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());

            target.Open();
            RestrictPermissions(copy);
            connection.BackupDatabase(target);
        }
        catch (Exception failed) when (failed is SqliteException or IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(copy);
            }
            catch (Exception left) when (left is IOException or UnauthorizedAccessException)
            {
                // Named in the message below; a person can remove it.
            }

            throw new InvalidOperationException(
                $"The store at '{path}' is at schema {version} and was not upgraded to {SchemaMigrator.LatestVersion}, because "
                + $"it could not first be copied to '{copy}': {failed.Message}",
                failed);
        }

        return copy;
    }

    /// <summary>Copies the write-ahead log into the store and empties it, so what a delete overwrote is gone from both.</summary>
    /// <param name="connection">An open connection to the store, outside any transaction.</param>
    /// <remarks>
    /// <b>Called after destroy, purge and a retention pass</b> (lens 2 run 97). An overwrite lands in the log first; the
    /// store keeps the old page, and the log the frames that wrote the text, until a checkpoint, which SQLite runs by
    /// itself only after about a thousand pages. On a quiet project that is days, and a copy of the store taken right
    /// after a person removed something gave it back. A checkpoint another connection holds back finishes later, as
    /// SQLite's own would; the broker has one.
    /// </remarks>
    internal static void Flush(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
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
    /// Applied after the connection is opened, because SQLite creates the files itself. The
    /// sidecars are named explicitly rather than assumed to inherit: SQLite derives their
    /// mode from the main database file when it creates them, so inheritance delivers the
    /// intended mode only if the main file was already restricted at that point. Naming all
    /// three means the guarantee does not rest on that ordering, or on the inheritance
    /// behaviour surviving a future SQLite release. Windows is left to directory ACLs, which
    /// is the platform-idiomatic mechanism.
    /// </remarks>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
