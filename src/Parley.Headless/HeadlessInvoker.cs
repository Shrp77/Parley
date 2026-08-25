using System.Diagnostics;

namespace Parley.Headless;

/// <summary>
/// What one invocation cost.
/// </summary>
/// <param name="ExitCode">The process's own verdict. Zero is success.</param>
/// <param name="Elapsed">How long it ran.</param>
/// <param name="TimedOut">Whether the invoker gave up waiting and killed it.</param>
public sealed record InvocationOutcome(int ExitCode, TimeSpan Elapsed, bool TimedOut);

/// <summary>
/// Starts an agent that owes an answer and has nobody running it.
/// </summary>
/// <remarks>
/// <para>
/// The Headless tier of SPEC.md §11.1, which §11.2 calls the important one and which existed
/// until now as a string in a column. Everything else in this protocol is pull: an agent acts
/// when its session takes a turn, and a session takes a turn when a person sends one. That makes
/// every exchange need a human at both ends, which is not what the protocol is for.
/// </para>
/// <para>
/// This gives the model a turn and nothing else. It passes no question, reads no message, and
/// writes nothing to the exchange — the invoked agent calls <c>parley_inbox</c> through its own
/// server under its own identity, and what it finds is what a person would have found. That is
/// what keeps an invoked answer honest: the identity still comes from the launch environment
/// (§13), the envelope still frames the peer's words, and nothing here can forge either.
/// </para>
/// <para>
/// Standard output is read and discarded rather than left unread. A process whose pipe fills
/// blocks on the write and never exits, so the invoker would time out on exactly the agents that
/// had most to say. Discarding is also the point: what the agent said travels as messages under
/// §9.4's retention, and keeping a copy here would put every word an agent ever wrote outside
/// every guard that governs the first copy.
/// </para>
/// </remarks>
public sealed class HeadlessInvoker
{
    private readonly TimeProvider _clock;

    /// <summary>Creates an invoker.</summary>
    /// <param name="clock">The clock to measure elapsed time from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    public HeadlessInvoker(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Runs the command in the agent's own workspace and waits for it.
    /// </summary>
    /// <param name="command">What to run.</param>
    /// <param name="workspace">The directory to run it in.</param>
    /// <param name="cancellationToken">Cancels the wait and kills the process.</param>
    /// <returns>What the invocation cost.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="workspace"/> is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace does not exist.</exception>
    /// <remarks>
    /// <para>
    /// The workspace is checked before launching rather than after failing. A registry entry can
    /// name a directory that has since been moved or deleted, and the process-start failure for
    /// that is an obscure Win32 error; the exchange stays owed either way, so the difference is
    /// entirely in whether an operator can tell what went wrong.
    /// </para>
    /// <para>
    /// A timeout kills the process and is reported rather than thrown. The exchange is unharmed:
    /// it is still owed, still open, and the next sweep will try again. Throwing would make one
    /// slow agent an error in the sweep that serves all of them.
    /// </para>
    /// </remarks>
    public async Task<InvocationOutcome> InvokeAsync(
        InvocationCommand command,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException(
                $"The registry says this agent works in '{workspace}', and that directory does " +
                "not exist. It cannot be started until the registry and the disk agree.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = workspace,
            // Redirected and closed immediately, and this line is load-bearing rather than
            // tidy: without it every invocation from inside a real MCP server hangs forever.
            //
            // The child inherits the host's standard input, and for an MCP server that is the
            // live JSON-RPC pipe its client is driving it over. The inherited handle keeps that
            // pipe open, and the invoker then waits on a process that has already exited. Found
            // by running it. See HeadlessInvokerTests for what it means that no test catches it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var started = _clock.GetTimestamp();

        using var process = new Process { StartInfo = startInfo };

        // Set before Start rather than after. This was originally believed to be the fix for the
        // hang above and is not: mutating it back leaves every test green. Kept because it is
        // still correct -- WaitForExitAsync completes off the Exited event, and subscribing after
        // the process may already have exited is a race worth not having -- but recorded
        // honestly, because a comment claiming to explain a bug it does not cause is worse than
        // no comment at all.
        process.EnableRaisingEvents = true;
        process.Start();

        // Nothing is written to the child. Closing rather than leaving it open means an agent
        // that reads stdin gets end-of-file instead of blocking on input that will never come.
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(command.Timeout);

        // Read both pipes to completion, concurrently with the wait. A child whose output buffer
        // fills blocks writing and never exits, so reading only after exit would deadlock on
        // precisely the agents that produced the most -- and that deadlock looks like a broken
        // agent rather than a broken invoker.
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout firing and the kill. Nothing to do: the outcome
                // below reports what actually happened rather than what was attempted.
            }

            if (!timedOut)
            {
                throw;
            }
        }

        // Awaited after exit so the pipes are drained even on the timeout path, where the reads
        // would otherwise be left running against a dead process.
        await Task.WhenAll(Swallow(stdout), Swallow(stderr)).ConfigureAwait(false);

        return new InvocationOutcome(
            timedOut ? -1 : process.ExitCode,
            _clock.GetElapsedTime(started),
            timedOut);
    }

    /// <summary>Awaits a pipe read, discarding both its value and any failure.</summary>
    /// <param name="read">The read in flight.</param>
    /// <returns>A task that completes when the read does, however it does.</returns>
    /// <remarks>
    /// The value is not wanted and a failure is not actionable: the process has already been
    /// killed or has already exited, and the outcome is decided by its exit code either way. An
    /// exception escaping here would turn a successful invocation into a failed sweep.
    /// </remarks>
    private static async Task Swallow(Task<string> read)
    {
        try
        {
            await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
