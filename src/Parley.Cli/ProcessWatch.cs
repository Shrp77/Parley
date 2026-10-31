using System.Globalization;
using System.Runtime.InteropServices;

namespace Parley.Cli;

/// <summary>
/// Writes to the broker's log what happens to the process itself: a crash, an exception nobody observed, a request to
/// stop, and the exit.
/// </summary>
/// <remarks>
/// <para>
/// <b>A broker that ended says why, where its log is read.</b> Without this a broker killed at logoff, stopped with
/// Ctrl+C or brought down by an exception ends with its last ordinary line, and the three cannot be told apart. A kill
/// that is not a signal (Task Manager, <c>taskkill /F</c>, SIGKILL) still says nothing, because nothing can.
/// </para>
/// <para>
/// <b>It observes and changes nothing.</b> A signal is not cancelled, so the process stops exactly as it would have;
/// an unobserved exception is not marked observed. Only <c>parley serve</c> starts one, at the top of the process,
/// because each hook is process-wide.
/// </para>
/// </remarks>
internal sealed class ProcessWatch : IDisposable
{
    private readonly TextWriter _log;
    private readonly List<PosixSignalRegistration> _signals = [];

    private ProcessWatch(TextWriter log)
    {
        _log = log;

        AppDomain.CurrentDomain.UnhandledException += Crashed;
        TaskScheduler.UnobservedTaskException += Unobserved;
        AppDomain.CurrentDomain.ProcessExit += Exiting;

        // On Windows these are Ctrl+C, Ctrl+Break, a closed console window, and logoff or shutdown.
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP, PosixSignal.SIGTERM })
        {
            _signals.Add(PosixSignalRegistration.Create(signal, Signalled));
        }
    }

    /// <summary>Starts writing what happens to this process to a log.</summary>
    /// <param name="log">Where to write.</param>
    /// <returns>The watch; disposing it stops watching.</returns>
    public static ProcessWatch Start(TextWriter log) => new(log);

    /// <inheritdoc/>
    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= Crashed;
        TaskScheduler.UnobservedTaskException -= Unobserved;
        AppDomain.CurrentDomain.ProcessExit -= Exiting;

        foreach (var signal in _signals)
        {
            signal.Dispose();
        }
    }

    /// <summary>The line a signal writes, worded for the platform the broker is on.</summary>
    /// <param name="signal">The signal.</param>
    /// <returns>What a person reading the log would call it.</returns>
    internal static string Describe(PosixSignal signal) => (signal, OperatingSystem.IsWindows()) switch
    {
        (PosixSignal.SIGINT, true) => "Ctrl+C",
        (PosixSignal.SIGQUIT, true) => "Ctrl+Break",
        (PosixSignal.SIGHUP, true) => "its console window being closed",
        (PosixSignal.SIGTERM, true) => "logoff or shutdown",
        _ => signal.ToString(),
    };

    private void Crashed(object? sender, UnhandledExceptionEventArgs e) =>
        Write($"an unhandled exception is ending the process{(e.IsTerminating ? string.Empty : " (not terminating)")}: {e.ExceptionObject}");

    private void Unobserved(object? sender, UnobservedTaskExceptionEventArgs e) =>
        Write($"a task failed and nothing observed it: {e.Exception}");

    private void Exiting(object? sender, EventArgs e) =>
        Write(string.Create(CultureInfo.InvariantCulture, $"the process is exiting, exit code {Environment.ExitCode}"));

    private void Signalled(PosixSignalContext context) =>
        Write($"asked to stop by {Describe(context.Signal)}");

    private void Write(string what)
    {
        try
        {
            _log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} process: {what}"));
            _log.Flush();
        }
#pragma warning disable CA1031 // Called from a signal or the exit: nothing may escape it.
        catch (Exception)
        {
            // A log that cannot be written cannot be told so.
        }
#pragma warning restore CA1031
    }
}
