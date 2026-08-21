using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Store;

namespace Parley.Mcp;

/// <summary>
/// The one broker this process owns, and the gate that keeps it to one caller at a time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Parley.Broker.Broker"/> documents that it is not thread-safe and that a host must
/// serialise access: it holds a single <see cref="SqliteConnection"/> for its lifetime, which is
/// not thread-safe either, and <c>StartedUtc</c> is plain mutable state that the ghosting clock
/// reads. An MCP server handles requests concurrently, so something has to reconcile those, and
/// this is it.
/// </para>
/// <para>
/// A semaphore rather than a per-call connection, which was the alternative. The broker's own
/// documentation sets out why: <c>StartedUtc</c> is per-process state that the ghosting clock
/// depends on, so an instance created for one call would not have it, and reworking that means a
/// connection factory rather than a connection — a larger change than it looks, because the
/// in-memory databases the tests use are private to their connection. At this scale the gate
/// costs nothing real: every operation is a handful of indexed queries against a local file, and
/// §4.1 already parallelises across projects by giving each its own process.
/// </para>
/// <para>
/// The sweep goes through the same gate as every tool call. It is the one piece of work that
/// arrives on a different thread by construction, so leaving it outside would reintroduce
/// exactly the race the gate exists to prevent — and it would do so rarely, which is the worst
/// frequency for a data race to occur at.
/// </para>
/// </remarks>
public sealed class BrokerSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _connection;
    private readonly Parley.Broker.Broker _broker;
    private readonly TimeProvider _clock;

    private BrokerSession(SqliteConnection connection, Parley.Broker.Broker broker, TimeProvider clock)
    {
        _connection = connection;
        _broker = broker;
        _clock = clock;
    }

    /// <summary>
    /// Opens the database, migrates it, and runs the broker's reboot sequence.
    /// </summary>
    /// <param name="options">What this server was launched with.</param>
    /// <param name="clock">The clock to stamp operations from.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>A started session.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The database could not be opened or migrated.</exception>
    /// <remarks>
    /// <para>
    /// The reboot sequence runs here rather than lazily, because SPEC.md §14 makes it the thing
    /// that invalidates what the previous run believed — and every operation on the broker
    /// refuses until it has. Doing it at start-up means a launch either works or fails visibly,
    /// rather than producing a server whose every tool reports that the broker has not started.
    /// </para>
    /// <para>
    /// It also orphans every exchange left open by the previous run, which is why the resume
    /// policy matters and why the default is <c>Never</c>: an interactive deployment should not
    /// have work resume that somebody stopped on purpose.
    /// </para>
    /// </remarks>
    public static async Task<BrokerSession> StartAsync(
        ServerOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var connection = ParleyDatabase.OpenAndMigrate(options.DatabasePath);

        try
        {
            var broker = new Parley.Broker.Broker(connection, BrokerOptions.Default);
            await broker.StartAsync(clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

            return new BrokerSession(connection, broker, clock);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs one unit of work against the broker, alone.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work, given the broker and the instant to stamp it with.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the work itself.</param>
    /// <returns>Whatever <paramref name="work"/> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The instant is supplied rather than read inside the work, so that every write in one
    /// operation carries the same one. A broker method that read the clock twice could stamp a
    /// message and its exchange a millisecond apart, which is harmless until somebody orders by
    /// it and finds an answer preceding its own question.
    /// </remarks>
    public async Task<T> RunAsync<T>(
        Func<Parley.Broker.Broker, DateTimeOffset, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work(_broker, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Takes the gate before closing the connection, so a tool call in flight finishes rather
    /// than meeting a disposed connection. A stdio server is shut down by its client closing the
    /// pipe, which can happen mid-call.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
