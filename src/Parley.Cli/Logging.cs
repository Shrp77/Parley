namespace Parley.Cli;

/// <summary>
/// The broker's own log file, when one was asked for.
/// </summary>
/// <remarks>
/// A supervisor's Exec action runs a program rather than a command line, so a broker started at
/// logon has nowhere for its output to go unless it writes somewhere itself. That is the whole
/// of why this exists: without it, a broker that failed to start under a scheduled task would
/// fail silently, and the only symptom would be agents refusing to launch a week later.
/// </remarks>
internal static class Logging
{
    /// <summary>
    /// Opens a log file for appending.
    /// </summary>
    /// <param name="path">Where to write, or <see langword="null"/> for nowhere.</param>
    /// <param name="error">Where to report that it could not be opened.</param>
    /// <returns>The writer, or <see langword="null"/> if none was asked for or it failed.</returns>
    /// <remarks>
    /// <para>
    /// A failure to open the log is reported and then ignored. A broker that refused to start
    /// because it could not write a log would be refusing to do the thing it exists for in order
    /// to protect the record of it — and the person who would have read that log is the person
    /// whose deployment just stopped working.
    /// </para>
    /// <para>
    /// Appended rather than truncated, and flushed on every write. A log that is rewritten each
    /// start loses exactly the run somebody is investigating, and one that buffers loses the
    /// last thing a crashing process said, which is the line that mattered.
    /// </para>
    /// </remarks>
    public static TextWriter? Open(string? path, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            return new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error.WriteLine($"Could not open the log at '{path}': {exception.Message}");

            return null;
        }
    }
}
