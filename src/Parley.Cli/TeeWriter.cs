using System.Text;

namespace Parley.Cli;

/// <summary>
/// Writes to two places at once.
/// </summary>
/// <param name="first">The first destination.</param>
/// <param name="second">The second.</param>
/// <remarks>
/// <para>
/// So that a broker started from a terminal still prints to it while also leaving a record for a
/// broker started by a supervisor. The obvious alternative is to redirect, and redirecting means
/// wrapping the broker in a shell — which puts a path inside a command line inside a task
/// definition, three layers of quoting for a feature whose absence is silent.
/// </para>
/// <para>
/// Neither destination is disposed here. Both are owned by whoever opened them, and the console
/// writer in particular must outlive this: closing it would take standard output with it.
/// </para>
/// <para>
/// <b>The second is written even when the first throws.</b> The first is the console, and a console that has gone
/// (a terminal closed under a running broker) must not cost the log its line; the failure still reaches the caller.
/// </para>
/// </remarks>
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    /// <inheritdoc />
    public override Encoding Encoding => first.Encoding;

    /// <inheritdoc />
    public override void Write(char value)
    {
        try
        {
            first.Write(value);
        }
        finally
        {
            second.Write(value);
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        try
        {
            first.Write(value);
        }
        finally
        {
            second.Write(value);
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        try
        {
            first.WriteLine(value);
        }
        finally
        {
            second.WriteLine(value);
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        try
        {
            first.Flush();
        }
        finally
        {
            second.Flush();
        }
    }
}
