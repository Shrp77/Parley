namespace Parley.Top.Tests;

/// <summary>
/// Somewhere for a screen to write while a test reads it back.
/// </summary>
/// <remarks>
/// The new surface hands its output to an <see cref="Action{T}"/> rather than to a console object,
/// which is what let the log follower and the browser launcher stop depending on a drawing library
/// at all. This is what a test passes in their place; the Spectre <c>TestConsole</c> it replaces
/// was doing nothing else for them.
/// </remarks>
internal sealed class Collected
{
    private readonly List<string> _lines = [];

    /// <summary>Takes a line, as a screen would.</summary>
    /// <param name="line">What was written.</param>
    public void Write(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    /// <summary>Every line written so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Everything written, as one string.</summary>
    public string Output => string.Join(Environment.NewLine, Lines);
}
