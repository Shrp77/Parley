namespace Parley.Terminal;

/// <summary>
/// Puts the terminal back the way it was found, on the paths where the toolkit's own shutdown
/// never runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported as "the mouse capture issue": every mouse movement printing <c>[555;93;23M</c> at
/// the shell prompt, for the rest of that window's life.</b> Terminal.Gui asks the terminal to
/// report mouse movement when it starts, and asks it to stop when it shuts down. Both halves are
/// the toolkit's and both are correct — what is missing is that the second half only happens if
/// the process lives long enough to run it.
/// </para>
/// <para>
/// <b>And this surface deliberately lets Ctrl+C kill it.</b> <see cref="TerminalCommand"/> argues
/// at length that terminating is "not the lesser option, it is the correct one", because an open
/// connection is the evidence an agent is reachable (SPEC.md §4.0) and a dying process closes
/// every attach stream — there is no politer signal, only the same signal sent sooner. **That
/// argument is right about the seat and says nothing about the console.** A seat is released by
/// the connection going; the terminal's modes are released by somebody sending the sequences, and
/// a killed process sends nothing. So the deliberate decision was correct and incomplete, and
/// this is the missing half rather than a reversal of it.
/// </para>
/// <para>
/// <b>It covers a crash as well, which is why it is not simply "handle Ctrl+C".</b> An unhandled
/// exception on the draw thread ends the process exactly as abruptly and leaves exactly the same
/// wreckage — and this surface has met that: a list handed a selection the toolkit refuses threw
/// on the first keystroke. A person whose terminal is ruined cannot tell the two apart, and should
/// not have to.
/// </para>
/// <para>
/// <b>Nothing is written on the ordinary path.</b> The toolkit's shutdown does it there, and doing
/// it twice would be this type guessing at a job somebody else has finished. What
/// <see cref="Dispose"/> does is take the handlers off again.
/// </para>
/// </remarks>
internal sealed class ConsoleRestore : IDisposable
{
    /// <summary>
    /// The DEC private modes Terminal.Gui switches on, in the order they are switched off.
    /// </summary>
    /// <remarks>
    /// Read out of the toolkit rather than guessed: 1003 is "report every mouse movement", 1006
    /// and 1015 are two encodings those reports come back in, 2004 is bracketed paste, and 1047
    /// and 1049 are the alternate screen. <c>TerminalRestoredTests</c> scans the assembly for every
    /// mode it enables and fails when one of them is missing from this list, so a toolkit upgrade
    /// that starts using a seventh is caught here rather than on somebody's prompt.
    /// </remarks>
    internal static readonly int[] Modes = [1003, 1015, 1006, 2004, 1047, 1049];

    private readonly TextWriter _to;
    private readonly ConsoleCancelEventHandler _interrupted;
    private readonly UnhandledExceptionEventHandler _crashed;

    private ConsoleRestore(TextWriter to)
    {
        _to = to;
        _interrupted = (_, _) => Restore(_to);
        _crashed = (_, _) => Restore(_to);

        // **Deliberately does not set Cancel.** The handler runs and the process then terminates as
        // it always did, which is what TerminalCommand's reasoning requires: this is here to put
        // the terminal back, not to take over what Ctrl+C means.
        Console.CancelKeyPress += _interrupted;
        AppDomain.CurrentDomain.UnhandledException += _crashed;
    }

    /// <summary>Arms the restore for as long as the surface is running.</summary>
    /// <param name="to">Where the sequences go; standard output when nothing else is given.</param>
    /// <returns>A handle that disarms it.</returns>
    internal static ConsoleRestore Arm(TextWriter? to = null) => new(to ?? Console.Out);

    /// <summary>Writes the sequences that undo what the toolkit switched on.</summary>
    /// <param name="to">Where they go.</param>
    /// <remarks>
    /// <para>
    /// The cursor is shown again as well. A toolkit that hid it and died leaves somebody typing
    /// blind, which is the same class of damage as the mouse reports and is invisible in exactly
    /// the same way — the shell is working perfectly and the terminal is not.
    /// </para>
    /// <para>
    /// Every failure here is swallowed. This runs while a process is on its way out, frequently
    /// because something has already gone wrong, and an exception thrown from a Ctrl+C handler or
    /// an unhandled-exception handler replaces whatever the person was about to be told with a
    /// second crash. A terminal left in a bad state is worse than one restored; neither is worth
    /// losing the report of the original fault.
    /// </para>
    /// </remarks>
    internal static void Restore(TextWriter to)
    {
        ArgumentNullException.ThrowIfNull(to);

        try
        {
            foreach (var mode in Modes)
            {
                to.Write($"[?{mode}l");
            }

            to.Write("[?25h");
            to.Flush();
        }
        catch (IOException)
        {
            // The console has gone. Nothing to put back.
        }
        catch (ObjectDisposedException)
        {
            // Same, reached the other way: the writer was closed on the way out.
        }
    }

    /// <summary>Takes the handlers off; writes nothing.</summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= _interrupted;
        AppDomain.CurrentDomain.UnhandledException -= _crashed;
    }
}
