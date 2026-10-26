using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Parley.Server;

/// <summary>
/// Where a broker writes what it logs, and whether it also says it is alive every second.
/// </summary>
/// <param name="Log">Where every line goes. One line per entry, an exception's lines after it.</param>
/// <param name="Heartbeat">
/// Whether to write a line every second saying how the process is doing, and a line for every connection that opens
/// and closes, with the web server's own account of resets and aborts.
/// </param>
/// <param name="Label">What each line starts with, to tell one broker's lines from another's in a shared log.</param>
/// <remarks>
/// <para>
/// <b>Without this a broker writes to the console</b>, and a broker started hidden at logon has none: what it logged,
/// a failed call included, went nowhere. <c>parley serve</c> passes the writer its <c>--log</c> goes to.
/// </para>
/// <para>
/// <b>The heartbeat is for finding out why something stopped answering.</b> A beat that arrives late says the process
/// was starved or paused, not idle; one that stops says it died; connection lines say which side closed what, and
/// when. It is off unless asked for, because a line a second is noise in a log somebody reads for anything else.
/// </para>
/// </remarks>
public sealed record ServerDiagnostics(TextWriter Log, bool Heartbeat = false, string? Label = null);

/// <summary>Writes log entries to a <see cref="TextWriter"/>, a line each.</summary>
internal sealed class WriterLoggerProvider(TextWriter writer, string? label) : ILoggerProvider
{
    private readonly TextWriter _writer = TextWriter.Synchronized(writer);

    public ILogger CreateLogger(string categoryName) => new WriterLogger(_writer, label, categoryName);

    public void Dispose()
    {
        // The writer is the caller's.
    }

    private sealed class WriterLogger(TextWriter writer, string? label, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {Level(logLevel)} {label}{(label is null ? string.Empty : " ")}{category}: {formatter(state, exception)}");

            try
            {
                writer.WriteLine(exception is null ? line : line + Environment.NewLine + exception);
            }
            catch (ObjectDisposedException)
            {
                // The log went before the broker did, which is a teardown and not something to fail a call over.
            }
            catch (InvalidOperationException)
            {
                // A test's output, written to after its test ended.
            }
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}

/// <summary>Counts and logs the connections a broker accepts, for the heartbeat.</summary>
internal sealed partial class ConnectionWatch(ILogger logger)
{
    private int _open;
    private long _total;

    /// <summary>Gets how many connections are open now.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <summary>Gets how many have been accepted in all.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>Wraps the next step of a connection so its opening and closing are logged.</summary>
    /// <param name="next">What Kestrel does with the connection.</param>
    /// <returns>The wrapped step.</returns>
    public ConnectionDelegate Around(ConnectionDelegate next) => async connection =>
    {
        _ = Interlocked.Increment(ref _open);
        var number = Interlocked.Increment(ref _total);
        var timer = Stopwatch.StartNew();

        LogOpened(connection.ConnectionId, connection.RemoteEndPoint?.ToString(), number);

        try
        {
            await next(connection).ConfigureAwait(false);
            LogClosed(connection.ConnectionId, timer.ElapsedMilliseconds);
        }
        catch (Exception failure)
        {
            LogFailed(failure, connection.ConnectionId, timer.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _open);
        }
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "connection {Id} opened from {Remote}, #{Number}")]
    private partial void LogOpened(string id, string? remote, long number);

    [LoggerMessage(Level = LogLevel.Information, Message = "connection {Id} closed after {Milliseconds} ms")]
    private partial void LogClosed(string id, long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "connection {Id} ended with an exception after {Milliseconds} ms")]
    private partial void LogFailed(Exception failure, string id, long milliseconds);

    private readonly ILogger _logger = logger;
}

/// <summary>Says once a second that the broker is alive, and how the process is doing.</summary>
/// <remarks>
/// <b>The gap is the line that matters.</b> Beats are asked for a second apart, so a gap well over a second means the
/// process could not run its timer: a starved thread pool, a long collection, or a machine too busy to schedule it. A
/// connection reset that lines up with a late beat is the process's doing; one between punctual beats is not.
/// </remarks>
internal sealed partial class Heartbeat(ILogger logger, ConnectionWatch connections, Func<int> attached) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task _running = Task.CompletedTask;

    /// <summary>Starts beating.</summary>
    public void Start() => _running = Task.Run(() => BeatAsync(_stopping.Token));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Asked for.
        }

        _stopping.Dispose();
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var up = Stopwatch.StartNew();
        var last = up.Elapsed;
        var beat = 0L;

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = up.Elapsed;
            var gap = (long)(now - last).TotalMilliseconds;
            last = now;

            LogBeat(
                ++beat,
                (long)now.TotalSeconds,
                gap,
                connections.Open,
                connections.Total,
                attached(),
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                Environment.WorkingSet / (1024 * 1024));
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "heartbeat {Beat}: up {Seconds} s, gap {Gap} ms, connections {Open} open of {Total}, {Attached} attached, "
            + "thread pool {Threads} threads with {Queued} queued, collections {Gen0}/{Gen1}/{Gen2}, {Megabytes} MB")]
    private partial void LogBeat(
        long beat, long seconds, long gap, int open, long total, int attached, int threads, long queued, int gen0, int gen1,
        int gen2, long megabytes);

    private readonly ILogger _logger = logger;
}
