using System.Diagnostics;

namespace Parley.Cli;

/// <summary>
/// Starts another Parley tool and hands it the terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>The surfaces are separate tools now, and <c>parley terminal</c> still works.</b> Windows cannot
/// replace a running executable, so while the terminal lived in this binary every terminal change
/// required stopping the broker — which SPEC.md §12 makes expensive: the node stops, tells every
/// agent it started to quit, and the process is what held everything each agent had learned. The
/// packaging changed; the command a person types did not have to.
/// </para>
/// <para>
/// <b>Nothing is parsed here, and that is the whole design.</b> Arguments are passed through
/// exactly as given, so there is one parser per tool rather than one parser and one imitation of
/// it. A wrapper that understood <c>--config</c> would be a second thing to keep in step with the
/// tool it forwards to, and the two are installed and versioned apart now precisely so they do not
/// have to move together.
/// </para>
/// <para>
/// <b>The child inherits this console rather than being redirected.</b> A terminal surface needs
/// the real one: redirecting its output would make it refuse to start, which is the correct
/// behaviour for a surface that draws. It follows that nothing here can read what the child says,
/// which is why the only thing reported is the case where it never started at all.
/// </para>
/// </remarks>
public static class Forwarding
{
    /// <summary>What to say when the tool providing a command is not installed.</summary>
    /// <param name="command">The command as somebody typed it, such as <c>parley terminal</c>.</param>
    /// <param name="tool">The executable that provides it.</param>
    /// <param name="package">The package to install.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// Names the tool and how to get it rather than saying the command is unknown, which would be
    /// untrue: the command exists and this machine has not been given the half that runs it. That
    /// distinction is the whole difference between a person installing one package and a person
    /// concluding the documentation is wrong.
    /// </remarks>
    public static string NotInstalled(string command, string tool, string package) =>
        $"{command} is provided by {tool}, which is not installed. "
        + $"Install it with: dotnet tool install --global {package}";

    /// <summary>
    /// Runs another tool with the arguments given and returns what it returned.
    /// </summary>
    /// <param name="tool">The executable name, without an extension.</param>
    /// <param name="package">The package that provides it, named when it is missing.</param>
    /// <param name="command">The command as typed, named when the tool is missing.</param>
    /// <param name="arguments">Passed through unparsed.</param>
    /// <param name="complain">Where to report that the tool is not installed.</param>
    /// <returns>The tool's exit code, or 69 when it could not be started.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tool"/> is blank.</exception>
    public static async Task<int> RunAsync(
        string tool,
        string package,
        string command,
        IReadOnlyList<string> arguments,
        TextWriter complain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(complain);

        var start = new ProcessStartInfo(tool) { UseShellExecute = false };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var child = Process.Start(start);

            if (child is null)
            {
                await complain.WriteLineAsync(NotInstalled(command, tool, package))
                    .ConfigureAwait(false);

                return 69;
            }

            await child.WaitForExitAsync().ConfigureAwait(false);

            return child.ExitCode;
        }
        catch (Exception starting) when (starting is System.ComponentModel.Win32Exception
                                             or InvalidOperationException
                                             or System.IO.FileNotFoundException)
        {
            // Every one of these means the same thing to the person who typed the command: the
            // tool is not on PATH. They are caught together rather than distinguished because the
            // platforms disagree about which is thrown and the answer is identical.
            await complain.WriteLineAsync(NotInstalled(command, tool, package)).ConfigureAwait(false);

            return 69;
        }
    }
}
