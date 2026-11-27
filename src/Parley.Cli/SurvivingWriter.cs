using System.Text;

namespace Parley.Cli;

/// <summary>
/// Writes to a terminal until the terminal has gone, and then writes nothing.
/// </summary>
/// <param name="inner">The terminal's writer, usually <see cref="Console.Out"/> or <see cref="Console.Error"/>.</param>
/// <remarks>
/// <para>
/// <b>A closed terminal must not end a node.</b> A <c>parley up</c> or <c>parley serve --with-node</c> started from a
/// terminal and left running after the terminal closed (disowned, or under <c>nohup</c> with its output still on the
/// terminal) gets an <see cref="IOException"/> on its next console write. The node writes a line when it starts an agent,
/// so the first agent it started after that threw out of the start: the node lost the agent it had just started, or
/// ended (review, lens 1 run B next to 8459f8a3). What it writes is a report, and a report nobody can read is not
/// worth the node.
/// </para>
/// <para>
/// <b>Once a write fails, every later one is dropped.</b> A terminal that has gone does not come back, and asking it
/// again on every line would spend a failed system call per line for the rest of the process's life. Only an
/// <see cref="IOException"/> is taken as the terminal going; anything else is a fault and still throws.
/// </para>
/// <para>
/// The writer is not disposed here: it is the console's.
/// </para>
/// </remarks>
internal sealed class SurvivingWriter(TextWriter inner) : TextWriter
{
    private int _gone;

    /// <summary>Gets whether a write has failed, so nothing more is written.</summary>
    internal bool Gone => Volatile.Read(ref _gone) == 1;

    /// <inheritdoc />
    public override Encoding Encoding => inner.Encoding;

    /// <inheritdoc />
    public override IFormatProvider FormatProvider => inner.FormatProvider;

    /// <inheritdoc />
    public override void Write(char value) => Attempt(() => inner.Write(value));

    /// <inheritdoc />
    public override void Write(string? value) => Attempt(() => inner.Write(value));

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Attempt(() => inner.Write(buffer, index, count));

    /// <inheritdoc />
    public override void WriteLine() => Attempt(inner.WriteLine);

    /// <inheritdoc />
    public override void WriteLine(string? value) => Attempt(() => inner.WriteLine(value));

    /// <inheritdoc />
    public override void Flush() => Attempt(inner.Flush);

    private void Attempt(Action write)
    {
        if (Gone)
        {
            return;
        }

        try
        {
            write();
        }
        catch (IOException)
        {
            // The terminal has gone. Said nowhere, because the only place to say it is the terminal.
            _ = Interlocked.Exchange(ref _gone, 1);
        }
    }
}
