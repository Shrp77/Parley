namespace Parley.Setup;

/// <summary>
/// An enrolment or withdrawal that stopped after it had already changed files.
/// </summary>
/// <remarks>
/// <para>
/// <b>A type rather than a message, because a surface has to be able to ask.</b> Three files are
/// written in order and the last one can fail with the first already replaced, so "it failed" and
/// "nothing happened" are different facts. The message says which files changed, which is enough for a
/// shell that prints it; a screen asks the type, so it never says <i>Nothing was written.</i> on the one
/// path where something was. Parsing the prose would be a second implementation of a fact the throwing
/// code already has.
/// </para>
/// <para>
/// <b>It derives from <see cref="IOException"/> because <see cref="InvalidDataException"/> is sealed
/// — and that was checked against every caller rather than assumed.</b> Both terminal surfaces catch
/// <see cref="IOException"/> already, so neither loses a failure it handles; the bulk command
/// takes <c>WriteAsync</c>, which does not raise this. The parent is also the honest one: what
/// happened is an I/O failure that arrived part-way through, not a file whose contents were wrong.
/// </para>
/// </remarks>
public sealed class InterruptedEnrolment : IOException
{
    /// <summary>Creates one with no detail.</summary>
    public InterruptedEnrolment() => Written = [];

    /// <summary>Creates one with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public InterruptedEnrolment(string message)
        : base(message) => Written = [];

    /// <summary>Creates one with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public InterruptedEnrolment(string message, Exception innerException)
        : base(message, innerException) => Written = [];

    /// <summary>Creates one naming the files that had already changed.</summary>
    /// <param name="message">What went wrong, and what had changed.</param>
    /// <param name="innerException">The failure that stopped it.</param>
    /// <param name="written">Every file changed before it stopped, in the order written.</param>
    public InterruptedEnrolment(
        string message,
        Exception innerException,
        IReadOnlyList<string> written)
        : base(message, innerException) => Written = written ?? [];

    /// <summary>Every file changed before it stopped, in the order written.</summary>
    public IReadOnlyList<string> Written { get; }
}
