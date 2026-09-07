using System.Diagnostics;

namespace Parley.Cli.Tests;

/// <summary>
/// What a long-running tool has said so far, and a wait for it to say something more.
/// </summary>
/// <remarks>
/// <para>
/// One copy, for the reason <see cref="Loopback"/> is one copy: a duplicated wait is the
/// duplication that hurts most, because the copy that drifts does not fail — it waits
/// differently, and stops passing on somebody else's machine.
/// </para>
/// <para>
/// <b>It drains continuously rather than reading on demand, and that is a bug fix rather than a
/// preference.</b> The obvious shape is a loop that awaits <c>ReadLineAsync</c> with a deadline
/// and gives up when the deadline passes. Giving up does not cancel the read: the
/// <see cref="StreamReader"/> still has one in flight, and the next call throws <i>the stream is
/// currently in use by a previous operation</i>. So the first wait that times out poisons every
/// wait after it — which is exactly what a test expecting the tool to stay quiet does on purpose.
/// Draining into a list on its own thread has no such state.
/// </para>
/// <para>
/// Running out of time is not a failure here. The caller asserts on everything that was said,
/// which produces "the tool said this instead" rather than "timed out", and a test expecting
/// silence reads the same way as one expecting speech.
/// </para>
/// </remarks>
internal sealed class ToolLog
{
    private readonly List<string> _lines = [];

    private ToolLog(Process process)
    {
        Pump = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_lines)
                {
                    _lines.Add(line);
                }
            }
        });
    }

    /// <summary>The draining, kept so a leak shows up as a hung task rather than as nothing.</summary>
    private Task Pump { get; }

    /// <summary>Starts draining a running tool's standard output.</summary>
    /// <param name="process">The running tool.</param>
    /// <returns>The log, already reading.</returns>
    public static ToolLog Of(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return new ToolLog(process);
    }

    /// <summary>Everything said so far.</summary>
    /// <returns>A copy, safe to read while the tool keeps talking.</returns>
    public List<string> Lines()
    {
        lock (_lines)
        {
            return [.. _lines];
        }
    }

    /// <summary>Whether any line said so far contains every one of some words.</summary>
    /// <param name="words">What the line must contain, all of it.</param>
    /// <returns>Whether such a line has been said.</returns>
    public bool Said(params string[] words) =>
        Lines().Exists(line => Array.TrueForAll(
            words, word => line.Contains(word, StringComparison.Ordinal)));

    /// <summary>Waits until the tool has said enough, or until the time runs out.</summary>
    /// <param name="done">Whether what has been said is enough.</param>
    /// <param name="within">How long to wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>Whether it was said in time.</returns>
    public async Task<bool> UntilAsync(
        Func<ToolLog, bool> done,
        TimeSpan within,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(done);

        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            if (done(this))
            {
                return true;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return done(this);
    }

    /// <summary>Everything said, for a failure message.</summary>
    /// <returns>The lines, one per line.</returns>
    public override string ToString() => string.Join(Environment.NewLine, Lines());
}
