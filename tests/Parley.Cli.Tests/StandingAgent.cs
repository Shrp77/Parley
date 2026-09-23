namespace Parley.Cli.Tests;

/// <summary>
/// The catalogue command a probe uses when it needs an agent that is genuinely running.
/// </summary>
/// <remarks>
/// <para>
/// <b>One copy, because getting it wrong is silent.</b> Three probes needed this within a day and each
/// spelled it out, two of them identically. What the string encodes is not obvious from reading it, and a
/// copy that drifts does not fail — it launches something that exits immediately, and the probe then
/// measures nothing while passing.
/// </para>
/// <para>
/// <b><c>--input-format</c> is load-bearing and is not a typo.</b> <c>InvocationCommand.Parse</c> appends
/// Claude Code's protocol arguments to every template that does not name its own input format —
/// deliberately, since appending them to a different runner produces a command line that means nothing to
/// either. A fixture that does not opt out is therefore launched as
/// <c>ping … -p --input-format stream-json …</c>, which ping refuses. That is not hypothetical: it is why
/// <c>OneCheckoutOneProcessTests</c> was flaky, believing it had launched something that lived thirty
/// seconds when the process had exited in twenty milliseconds.
/// </para>
/// <para>
/// <b>The token rides in on an <c>echo</c> to nul, and <c>rem</c> was tried first and is wrong.</b>
/// <c>rem</c> comments out the remainder of the line, the <c>&amp;</c> included, so the real command never
/// runs at all.
/// </para>
/// <para>
/// <b>It waits on standard input rather than sleeping.</b> A sleep is alive for a number somebody guessed
/// and has to be killed once the shutdown grace expires, which put forty seconds on this suite. Waiting on
/// the stream means the process is alive exactly while something holds it — which is what a resident agent
/// *is* — and it exits the moment that closes, through the polite ending <c>ResidentAgent.ShutdownAsync</c>
/// is written around rather than the kill that backs it up.
/// </para>
/// <para>
/// Measured rather than reasoned, on Windows: 4.07s for a bare five-count ping, 0.020s with the protocol
/// arguments appended, 0.021s with <c>rem</c>, and 5.04s blocked against an open pipe against 0.044s at
/// end of input.
/// </para>
/// </remarks>
internal static class StandingAgent
{
    /// <summary>A command that stays alive exactly as long as something holds its input.</summary>
    public static string Command =>
        OperatingSystem.IsWindows()
            ? $"cmd /c {OptOut} & more"
            : $"sh -c \"cat\" {Token}";

    /// <summary>The same, after appending one line to a file — for counting what was launched.</summary>
    /// <param name="evidence">The file to append to.</param>
    /// <param name="line">What to write, one line per launch.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    /// The append comes first so that a launch is recorded even if what follows never runs, which is what
    /// makes the count a count of launches rather than of survivors.
    /// </remarks>
    public static string Recording(string evidence, object line) =>
        OperatingSystem.IsWindows()
            ? $"cmd /c echo {line} >> \"{evidence}\" & {OptOut} & more"
            : $"sh -c \"echo {line} >> '{evidence}'; cat\" {Token}";

    /// <summary>The argument whose presence stops the protocol arguments being appended.</summary>
    private const string Token = "--input-format";

    /// <summary>Carries <see cref="Token"/> into the command line without consuming the rest of it.</summary>
    private const string OptOut = $"echo {Token} >nul";
}
