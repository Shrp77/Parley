using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Store;

namespace Parley.Bridge;

/// <summary>
/// Owns the one connection every request runs against, and lets only one at a time use it.
/// </summary>
/// <remarks>
/// <para>
/// A web server answers requests concurrently and a <see cref="SqliteConnection"/> is not
/// thread-safe, so something has to reconcile the two. The choice here is one connection behind a
/// gate rather than a connection per request, for two reasons.
/// </para>
/// <para>
/// The operator surface is one person clicking, not a workload -- serialising costs nothing a
/// human can perceive, while a pool would multiply this process's writers against a file that
/// every agent in the fleet is already writing to.
/// </para>
/// <para>
/// And <see cref="OperatorConsole"/> takes a connection in its constructor, so a per-request
/// connection means a per-request console, which is a lot of moving parts to avoid a wait no
/// operator will ever notice.
/// </para>
/// </remarks>
public sealed class StoreAccess : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StoreAccess(SqliteConnection connection)
    {
        _connection = connection;
        Console = new OperatorConsole(connection);
        View = new FleetView(Console, connection);
    }

    /// <summary>The operator operations of SPEC.md §10.2, and nothing else.</summary>
    public OperatorConsole Console { get; }

    /// <summary>The board's read model.</summary>
    public FleetView View { get; }

    /// <summary>
    /// Opens a store, refusing one this build cannot read.
    /// </summary>
    /// <param name="databasePath">The store.</param>
    /// <returns>The access, or the reason it could not be opened.</returns>
    /// <remarks>
    /// The same two refusals the CLI makes, for the same reasons. Schema zero means the path is
    /// wrong and this must not install a schema into it -- a typo would otherwise produce an
    /// empty, healthy-looking deployment. A schema behind this build means an older server last
    /// touched the file, and a report that reads a table a later migration added fails with a
    /// stack trace that names nothing useful. A schema ahead is allowed: migrations here add
    /// rather than remove, and being one version behind the servers is the ordinary state of a
    /// machine mid-upgrade.
    /// </remarks>
    public static (StoreAccess? Access, string? Error) Open(string databasePath)
    {
        SqliteConnection connection;

        try
        {
            connection = ParleyDatabase.Open(databasePath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException
                                      or UnauthorizedAccessException)
        {
            return (null, $"Could not open '{databasePath}': {ex.Message}");
        }

        var schema = SchemaMigrator.GetUserVersion(connection);

        if (schema == 0)
        {
            connection.Dispose();

            return (null,
                $"'{databasePath}' is not a Parley database. It has a schema version of zero, " +
                "which means nothing here wrote it. This bridge administers a database an " +
                "agent's server created, and will not create or alter one.");
        }

        if (schema < SchemaMigrator.LatestVersion)
        {
            connection.Dispose();

            return (null, string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"'{databasePath}' is at schema version {schema}, and this build expects {SchemaMigrator.LatestVersion}. Start any agent's server against it once — the server migrates, this deliberately does not."));
        }

        return (new StoreAccess(connection), null);
    }

    /// <summary>
    /// Runs one piece of work against the store, alone.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate.</param>
    /// <returns>What the work returned.</returns>
    public async Task<T> UseAsync<T>(
        Func<StoreAccess, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work(this).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the launch command an agent recorded for itself.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workspace and command, either of which may be absent.</returns>
    /// <remarks>
    /// Read here rather than taken from a request, because a launch command supplied by the
    /// caller would make this endpoint "run this program on my machine" wearing an agent's name.
    /// The registry is where an agent records what it consents to being started with, and that is
    /// the only acceptable source.
    /// </remarks>
    public async Task<(string? Workspace, string? Command)> LaunchAsync(
        string name, CancellationToken cancellationToken = default)
    {
        using var command = _connection.CreateCommand();

        command.CommandText =
            "SELECT workspace, invoke_command FROM agents WHERE name = @Name;";

        command.Parameters.AddWithValue("@Name", name);

        var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var closing = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}
