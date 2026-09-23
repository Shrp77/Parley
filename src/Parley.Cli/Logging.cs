using System.Text;

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
    /// Opens a log file for appending, shared with any other process appending to it.
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
    /// Appended rather than truncated, and written a line at a time. A log that is rewritten each
    /// start loses exactly the run somebody is investigating, and one that buffers loses the last
    /// thing a crashing process said, which is the line that mattered.
    /// </para>
    /// <para>
    /// <b>Shared, because two brokers naming one log is ordinary and the second one's line is the
    /// one somebody needs.</b> A scheduled task and a terminal that restores its last session both
    /// start <c>parley serve</c> with the same arguments at logon. This used to hold the file open
    /// for writing, so whichever started second could not open it and said why only to a console
    /// nobody could see — while the first went on to report a deployment that the second had
    /// already damaged. Each line is appended by opening, writing and closing, rather than through
    /// a writer held open, because a held .NET writer keeps its own position and would write its
    /// next line over whatever another process had appended since.
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
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            // Opened once now, so a path that cannot be written is reported at start where somebody
            // is looking, rather than lost line by line afterwards.
            using (Appending(full))
            {
            }

            return new SharedLog(full);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error.WriteLine($"Could not open the log at '{path}': {exception.Message}");

            return null;
        }
    }

    /// <summary>Opens the log to append one line, leaving it open to every other writer.</summary>
    /// <param name="path">The log.</param>
    /// <returns>The stream, positioned at the end.</returns>
    internal static FileStream Appending(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
}

/// <summary>
/// A log that several processes may append to at once, one whole line at a time.
/// </summary>
/// <param name="path">The log file.</param>
/// <remarks>
/// <para>
/// Line-buffered, and a line is written only once it is complete, so two processes interleave
/// whole lines rather than characters. The buffer is guarded here rather than by the callers:
/// <c>serve</c> wraps its output and its error writers separately, and both of them end here.
/// </para>
/// <para>
/// A line that cannot be written — the file deleted, a disk full — is dropped rather than
/// thrown. It has already reached the console, since this is always the second half of a tee, and
/// a broker that stopped serving because its log went away would be trading a deployment for a
/// record of it.
/// </para>
/// </remarks>
internal sealed class SharedLog(string path) : TextWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StringBuilder _line = new();

    private readonly Lock _gate = new();

    /// <inheritdoc />
    public override Encoding Encoding => Utf8;

    /// <inheritdoc />
    public override void Write(char value)
    {
        lock (_gate)
        {
            _ = _line.Append(value);

            if (value == '\n')
            {
                WriteOut();
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            _ = _line.Append(value);

            if (value.Contains('\n', StringComparison.Ordinal))
            {
                WriteOut();
            }
        }
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_gate)
        {
            _ = _line.Append(buffer, index, count);

            if (Array.IndexOf(buffer, '\n', index, count) >= 0)
            {
                WriteOut();
            }
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        lock (_gate)
        {
            if (_line.Length > 0)
            {
                WriteOut();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    /// <summary>Appends what is buffered and empties the buffer. Called under the gate.</summary>
    private void WriteOut()
    {
        var bytes = Utf8.GetBytes(_line.ToString());

        _ = _line.Clear();

        try
        {
            using var stream = Logging.Appending(path);

            stream.Write(bytes);
        }
        catch (IOException)
        {
            // Dropped, for the reason the type's remarks give: the console already has it.
        }
        catch (UnauthorizedAccessException)
        {
            // The same.
        }
    }
}
