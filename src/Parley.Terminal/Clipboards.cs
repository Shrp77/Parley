using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Putting text on the machine's clipboard, and saying whether it went.
/// </summary>
/// <remarks>
/// <para>
/// <b>So a transcript can leave this screen.</b> A terminal is where a person reads a
/// conversation and not where they act on it: what comes next is quoting it to a colleague, or
/// into a chat with the agent that is going to fix something, and re-typing exchanges is not a
/// workflow.
/// </para>
/// <para>
/// <b>Through <c>IApplication</c> rather than the static <c>Clipboard</c>.</b> The static one is
/// marked obsolete in 2.4.17 and would need the suppression <c>Entry</c> carries; the application's
/// own is the supported route and is per-instance, which is also what lets a driven test hold its
/// own.
/// </para>
/// <para>
/// <b>Failure is ordinary and is reported, never thrown.</b> A clipboard is an operating-system
/// integration the toolkit reaches through <c>xclip</c>, <c>pbcopy</c> or a Win32 call — absent on
/// a bare Linux box, and refusable by another process holding it open. This surface draws a
/// terminal: an exception escaping a keystroke handler ends the program, and ending somebody's
/// session because a copy failed would be a worse outcome than the copy failing.
/// </para>
/// </remarks>
internal static class Clipboards
{
    /// <summary>Puts text on the clipboard.</summary>
    /// <param name="app">The application, whose driver owns the clipboard.</param>
    /// <param name="text">What to put on it.</param>
    /// <returns><see langword="true"/> when it went.</returns>
    internal static bool Write(IApplication app, string text)
    {
        ArgumentNullException.ThrowIfNull(app);

        try
        {
            return app.Clipboard is { } board && board.TrySetClipboardData(text);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031
        {
            // **Deliberately everything.** What is behind this is another process, a shell tool
            // that may not be installed, and P/Invoke — three things whose failure modes are not
            // enumerable from here, and none of which is worth a terminal's life. The caller says
            // the copy did not happen; nothing else is claimed.
            return false;
        }
    }
}
