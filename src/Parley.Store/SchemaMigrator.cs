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
    /// An embedded migration is misnamed or unreadable.
    /// </exception>
    /// <exception cref="SqliteException">A migration script failed to execute.</exception>
    /// <remarks>
    /// Idempotent and safe to call on every start-up. Migrations already recorded in
    /// <c>user_version</c> are skipped.
    /// </remarks>
    public static int Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        Configure(connection);

        var current = GetUserVersion(connection);
        var applied = 0;

        foreach (var (version, sql) in LoadMigrations())
        {
            if (version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();

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
    /// Reads the schema version currently recorded in the database.
    /// </summary>
    /// <param name="connection">The open connection to query.</param>
    /// <returns>
    /// The highest migration number applied, or zero for an empty database.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public static int GetUserVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
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
