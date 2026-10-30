using System.Globalization;

namespace Parley.Headless.Tests;

/// <summary>
/// A fake agent runner: a script that speaks the turn protocol and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A real runner cannot be used here. It would need credentials, cost money on every assertion,
/// and make the outcome depend on a model's judgement — which is the one thing a test must never
/// wait on. What is under test is the process handling: does the fleet keep the agent alive
/// between turns, does the stream stay in step when a turn is abandoned, does a closed standard
/// input stop it.
/// </para>
/// <para>
/// Each stub names <c>--input-format</c> in its own template, which is also how
/// <see cref="InvocationCommand.Parse"/> is told not to append Claude Code's flags. That these
/// tests need the escape hatch is the evidence that the escape hatch works.
/// </para>
/// </remarks>
internal static class StubRunner
{
    /// <summary>A result line reporting a clean turn with a known cost.</summary>
    internal const string Result =
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"," +
        "\"num_turns\":2,\"total_cost_usd\":0.25,\"session_id\":\"stub-session\"," +
        "\"permission_denials\":[]}";

    /// <summary>A result line reporting a turn the agent itself considered failed.</summary>
    internal const string ErrorResult =
        "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true," +
        "\"result\":\"no\",\"num_turns\":1,\"total_cost_usd\":0.01," +
        "\"session_id\":\"stub-session\",\"permission_denials\":[\"Bash\",\"Edit\"]}";

    /// <summary>The init line, carrying the session identifier a runner reports.</summary>
    internal const string Init =
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"stub-session\"," +
        "\"model\":\"stub\"}";

    private const string Assistant = "{\"type\":\"assistant\"}";

    /// <summary>Writes a stub that answers every turn, and returns its command template.</summary>
    /// <param name="directory">Where to put the script.</param>
    /// <param name="reply">The result line to answer each turn with.</param>
    /// <param name="delaySeconds">How long to think before answering.</param>
    /// <returns>The template, in the form a launch configuration would carry.</returns>
    internal static string Answering(
        string directory, string reply = Result, int delaySeconds = 0)
    {
        var seconds = delaySeconds.ToString(CultureInfo.InvariantCulture);

        var windows = new List<string> { Emit(Init), "while ($null -ne ($line = [Console]::In.ReadLine())) {" };

        if (delaySeconds > 0)
        {
            windows.Add("  Start-Sleep -Seconds " + seconds);
        }

        windows.Add("  " + Emit(Assistant));
        windows.Add("  " + Emit(reply));
        windows.Add("}");

        var unix = new List<string> { Print(Init), "while IFS= read -r line; do" };

        if (delaySeconds > 0)
        {
            unix.Add("  sleep " + seconds);
        }

        unix.Add("  " + Print(Assistant));
        unix.Add("  " + Print(reply));
        unix.Add("done");

        return Write(directory, windows, unix);
    }

    /// <summary>Writes a stub that exits immediately, and returns its command template.</summary>
    /// <param name="directory">Where to put the script.</param>
    /// <returns>The template.</returns>
    internal static string Dying(string directory) =>
        Write(directory, ["exit 0"], ["exit 0"]);

    /// <summary>Writes a stub that reads its input and never answers.</summary>
    /// <param name="directory">Where to put the script.</param>
    /// <returns>The template.</returns>
    /// <remarks>
    /// The shape of an agent that has wedged rather than one that has crashed, and the two want
    /// different handling: a wedged agent must not be killed on a turn timeout, because killing
    /// it discards the context that is the whole reason it is resident.
    /// </remarks>
    internal static string Silent(string directory) =>
        Write(
            directory,
            ["while ($null -ne ($line = [Console]::In.ReadLine())) { }"],
            ["while IFS= read -r line; do :; done"]);

    /// <summary>
    /// Writes a stub that answers each prompt after a delay with a result whose cost is the prompt's number, and returns
    /// its command template.
    /// </summary>
    /// <param name="directory">Where to put the script.</param>
    /// <param name="delaySeconds">How long to think before answering.</param>
    /// <returns>The template.</returns>
    /// <remarks>
    /// The cost is the one number a turn's outcome carries from the reply, so it says which prompt a turn's reply
    /// answered: a turn that read the reply to the one before it shows that one's number.
    /// </remarks>
    internal static string Numbering(string directory, int delaySeconds)
    {
        var seconds = delaySeconds.ToString(CultureInfo.InvariantCulture);
        const string Before = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\",\"num_turns\":1,\"total_cost_usd\":";
        const string After = ",\"session_id\":\"stub-session\",\"permission_denials\":[]}";

        var windows = new List<string>
        {
            Emit(Init),
            "$n = 0",
            "while ($null -ne ($line = [Console]::In.ReadLine())) {",
            "  $n++",
            "  Start-Sleep -Seconds " + seconds,
            "  [Console]::Out.WriteLine('" + Before + "' + $n + '" + After + "'); [Console]::Out.Flush()",
            "}",
        };

        var unix = new List<string>
        {
            Print(Init),
            "n=0",
            "while IFS= read -r line; do",
            "  n=$((n+1))",
            "  sleep " + seconds,
            "  printf '%s%s%s\n' '" + Before + "' \"$n\" '" + After + "'",
            "done",
        };

        return Write(directory, windows, unix);
    }

    /// <summary>Emits one line from PowerShell and flushes it.</summary>
    /// <param name="json">The line.</param>
    /// <returns>The statement.</returns>
    /// <remarks>
    /// Written through <c>[Console]::Out</c> with an explicit flush rather than
    /// <c>Write-Output</c>. A buffered runner is indistinguishable from a slow one, and the
    /// difference would surface as an intermittent timeout in whichever test happened to run
    /// first.
    /// </remarks>
    private static string Emit(string json) =>
        "[Console]::Out.WriteLine('" + json + "'); [Console]::Out.Flush()";

    private static string Print(string json) => "printf '%s\\n' '" + json + "'";

    private static string Write(
        string directory, IEnumerable<string> windows, IEnumerable<string> unix)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, "stub.ps1");
            File.WriteAllLines(path, windows);

            return "pwsh -NoProfile -NonInteractive -File \"" + path +
                   "\" --input-format stream-json";
        }

        var script = Path.Combine(directory, "stub.sh");
        File.WriteAllLines(script, unix);

        return "/bin/sh \"" + script + "\" --input-format stream-json";
    }
}
