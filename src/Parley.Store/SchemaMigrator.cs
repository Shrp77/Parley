using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// Applies the embedded SQL migrations that create and evolve the Parley schema, and
/// configures the connection settings the broker depends on.
/// </summary>
/// <remarks>
/// <para>
/// Schema version is tracked with SQLite's <c>user_version</c> pragma. There is no migration
/// framework by design: the broker ships as a single self-contained binary, and a
/// schema-versioning dependency would be the largest thing in it.
/// </para>
/// <para>
/// Migration files live under <c>Migrations/</c>, are embedded as resources, and must be
/// named <c>NNN_description.sql</c>. They are applied in ordinal order of file name, each
/// inside its own transaction together with the version bump — so a failed migration leaves
/// the database at the previous version rather than half-applied. Migration scripts must not
/// contain their own <c>BEGIN</c> or <c>COMMIT</c> statements.
/// </para>
/// </remarks>
public static class SchemaMigrator
{
    private const string ResourcePrefix = "Parley.Store.Migrations.";

    /// <summary>
    /// Brings a database up to the latest schema version, opening and configuring the
    /// connection if required.
    /// </summary>
    /// <param name="connection">The connection to migrate. Opened if not already open.</param>
    /// <returns>
    /// The number of migrations applied. Zero means the database was already current.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// An embedded migration is misnamed or unreadable, or the store is at a schema version
    /// newer than this build knows — which is refused rather than opened, because this code
    /// cannot know what changed in a migration it does not carry.
    /// </exception>
    /// <exception cref="SqliteException">A migration script failed to execute.</exception>
    /// <remarks>
    /// Idempotent and safe to call on every start-up. Migrations already recorded in
    /// <c>user_version</c> are skipped.
    /// </remarks>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "Neither statement can carry user input. The migration body is read from an " +
            "embedded resource compiled into this assembly, and the user_version value is an " +
            "int parsed from that resource's file name. PRAGMA does not accept parameters, so " +
            "interpolation is the only option for the version bump. Every other statement in " +
            "this assembly is parameterised, and CA2100 remains an error to keep it that way.")]
    public static int Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        Configure(connection);

        var current = GetUserVersion(connection);

        // A database written by a newer Parley than this one is refused rather than served: a
        // migration that drops a column, renames a table or tightens a constraint would be met by
        // code that believes none of it happened, and the first sign would be a write that fails
        // or one that succeeds against the wrong meaning. It is reachable in ordinary upgrading,
        // when the old scheduled task restarts after a new Parley is installed.
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"This store is at schema {current} and this Parley only knows {LatestVersion}. " +
                "It was written by a newer version and will not be opened, because this build " +
                "cannot know what changed. Install the newer Parley; an older one still starting " +
                "after an install is the usual cause. See `Upgrading` in docs/operating.md.");
        }

        var applied = 0;

        foreach (var (version, sql) in LoadMigrations())
        {
            if (version <= current)
            {
                continue;
            }

            // IMMEDIATE, so the write lock is taken before the decision to apply rather than at
            // the first CREATE TABLE. A deferred transaction acquires nothing until it writes,
            // which leaves the version read above outside any lock at all.
            using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);

            // Read again inside the lock. Two processes opening one project at the same moment
            // both see an empty database and both decide to apply 001; SQLite serialises them, so
            // the second arrives holding the lock with the tables already created. Every agent has
            // its own process, so this is the ordinary case after a reboot.
            if (version <= GetUserVersion(connection, transaction))
            {
                transaction.Rollback();
                continue;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;

                // PRAGMA does not accept parameters. The value is an integer parsed from an
                // embedded resource name, never from user input, so interpolation is safe
                // here in a way it would not be for any other statement in the codebase.
                bump.CommandText = FormattableString.Invariant($"PRAGMA user_version = {version};");
                bump.ExecuteNonQuery();
            }

            transaction.Commit();
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Applies the connection-level settings the broker's correctness depends on.
    /// </summary>
    /// <param name="connection">The open connection to configure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// These are per-connection settings, not per-database, so every connection the broker
    /// opens must be configured. <see cref="Migrate"/> does this for the connection it is
    /// given; other connections are the caller's responsibility.
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>journal_mode = WAL</c> — survives hard power loss and lets readers proceed
    /// concurrently with a writer, which the reaper and the inbox drain both depend on.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>synchronous = NORMAL</c> — the standard companion to WAL, trading a durability
    /// window on power loss for materially fewer syncs.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>foreign_keys = ON</c> — SQLite disables these by default, and the schema relies on
    /// them to prevent exchanges referencing unregistered agents.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>busy_timeout = 5000</c> — waits rather than failing immediately when another
    /// connection holds the write lock.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static void Configure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the schema version a database is brought to by everything this build ships.
    /// </summary>
    /// <remarks>
    /// Read from the embedded migrations rather than written down, so it cannot disagree with
    /// what <see cref="Migrate"/> would actually apply. It exists because a reader needs to know
    /// whether a database is older than the code reading it: the operator's tool never migrates,
    /// so without this a report against a database one migration behind fails on a missing table
    /// with a stack trace, instead of saying which of the two is out of date.
    /// </remarks>
    public static int LatestVersion => LoadMigrations().Max(m => m.Version);

    /// <summary>
    /// Reads the schema version currently recorded in the database.
    /// </summary>
    /// <param name="connection">The open connection to query.</param>
    /// <returns>
    /// The highest migration number applied, or zero for an empty database.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public static int GetUserVersion(SqliteConnection connection) =>
        GetUserVersion(connection, transaction: null);

    /// <summary>
    /// Reads the schema version currently recorded in the database, within a transaction.
    /// </summary>
    /// <param name="connection">The open connection to query.</param>
    /// <param name="transaction">The transaction to read within, or <see langword="null"/>.</param>
    /// <returns>
    /// The highest migration number applied, or zero for an empty database.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The overload exists so the migration loop can re-read the version while holding the write
    /// lock. Read outside one, the answer is only ever a description of the past.
    /// </remarks>
    private static int GetUserVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IEnumerable<(int Version, string Sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            var bare = name[ResourcePrefix.Length..];
            var underscore = bare.IndexOf('_', StringComparison.Ordinal);

            if (underscore <= 0 || !int.TryParse(
                    bare.AsSpan(0, underscore),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var version))
            {
                throw new InvalidOperationException(
                    $"Migration '{bare}' is misnamed. Expected 'NNN_description.sql'.");
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Could not read migration '{name}'.");
            using var reader = new StreamReader(stream);

            yield return (version, reader.ReadToEnd());
        }
    }
}
