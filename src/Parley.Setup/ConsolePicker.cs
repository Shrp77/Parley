namespace Parley.Setup;

/// <summary>
/// Drives a <see cref="RepositorySelection"/> against a real terminal.
/// </summary>
/// <remarks>
/// <para>
/// Everything that decides anything is in <see cref="RepositorySelection"/>. This owns the two
/// things a test cannot have: a console to draw on and a key to read. Keeping it this thin is
/// deliberate — it is the one part of enrollment with no test, so it should contain nothing
/// worth testing.
/// </para>
/// <para>
/// <b>It refuses to run when there is no terminal.</b> A redirected stdin means a script, a CI
/// job or a pipe, and a prompt there either blocks forever or reads the wrong thing. The caller
/// is told so and falls back to the flags, rather than this guessing that nobody minds.
/// </para>
/// </remarks>
public static class ConsolePicker
{
    /// <summary>
    /// Says whether a picker can be shown at all.
    /// </summary>
    /// <remarks>
    /// Output as well as input: a picker redraws by rewriting lines, and a redirected stdout
    /// turns that into thousands of lines of frames in somebody's log file.
    /// </remarks>
    public static bool Available => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// Shows the picker and returns what the person chose.
    /// </summary>
    /// <param name="selection">The repositories to offer.</param>
    /// <returns>The final state, confirmed or cancelled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">There is no terminal to draw on.</exception>
    public static RepositorySelection Show(RepositorySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (!Available)
        {
            throw new InvalidOperationException("There is no terminal to show a picker on.");
        }

        var drawn = 0;

        while (selection.Status == SelectionStatus.Choosing)
        {
            // Redrawn in place rather than appended, so the list does not march down the screen
            // on every keypress. Only the lines that were drawn are cleared, because clearing
            // the console would take whatever the person had above this with it.
            if (drawn > 0)
            {
                Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - drawn));
            }

            var width = Console.WindowWidth > 20 ? Console.WindowWidth - 1 : 100;
            var lines = selection.Render(width);

            foreach (var line in lines)
            {
                Console.WriteLine(line.PadRight(width));
            }

            drawn = lines.Count;
            selection = selection.Press(Console.ReadKey(intercept: true).Key);
        }

        return selection;
    }
}
