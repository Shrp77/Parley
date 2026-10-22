using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Parley.Core;

namespace Parley.Cli;

/// <summary>
/// Whether the other Parley programs on this machine are from the same release as the broker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked of the tools themselves, not inferred.</b> The broker, the terminal, the web UI and an agent's adapter are
/// four packages installed one at a time, and installing one does not move the others. A machine where only some were
/// upgraded works until the day a release changes something they share, and then fails in a way that points at none of
/// them. Each is asked what it is, on the machine the broker runs on, because that is the machine that has all of them.
/// </para>
/// <para>
/// <b>Only a warning, and only for what is installed.</b> A machine that runs agents and never a terminal has no
/// <c>parley-terminal</c>, and that is not a fault. A tool that does not answer in time is skipped for the same reason:
/// this runs while the broker starts and must never be the reason it does not.
/// </para>
/// </remarks>
internal static partial class InstalledTools
{
    /// <summary>The tools a deployment can have beside the broker.</summary>
    internal static readonly string[] Names = ["parley-terminal", "parley-web-ui", "parley-mcp"];

    /// <summary>How long a tool has to say what it is.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>Writes a warning for each installed tool that is from another release than the broker.</summary>
    /// <param name="output">Where the broker's startup lines go.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>When every tool has been asked.</returns>
    internal static async Task CheckAsync(TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        var broker = Compatibility.VersionOf(typeof(InstalledTools).Assembly);

        try
        {
            var asked = await Task.WhenAll(Names.Select(name => VersionOfAsync(name, cancellationToken)))
                .ConfigureAwait(false);

            for (var i = 0; i < Names.Length; i++)
            {
                if (asked[i] is { } version && !Compatibility.Agree(version, broker))
                {
                    await output.WriteLineAsync(Warning(Names[i], version, broker)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never the reason a broker fails to start.
            await output.WriteLineAsync($"Could not check the installed Parley tools: {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>What is said about one tool from another release.</summary>
    /// <param name="tool">Its command name.</param>
    /// <param name="version">What it said it is.</param>
    /// <param name="broker">What the broker is.</param>
    /// <returns>One line.</returns>
    internal static string Warning(string tool, string version, string broker) =>
        $"WARNING -- {tool} is {version} and this broker is {broker}. They are from different releases, so they may "
        + "disagree about what each other sends. Install the same release of every Parley tool.";

    /// <summary>Reads the version out of what a tool printed.</summary>
    /// <param name="text">Its <c>--version</c> output, such as <c>parley-terminal 0.42.1, speaking Parley protocol 0.8</c>.</param>
    /// <returns>The first three-part version in it, or null.</returns>
    internal static string? Read(string? text)
    {
        var found = Number().Match(text ?? string.Empty);

        return found.Success ? found.Value : null;
    }

    /// <summary>The version a tool reports, or null when it is not installed or did not say.</summary>
    /// <param name="tool">Its command name.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The version, such as <c>0.42.1</c>.</returns>
    private static async Task<string?> VersionOfAsync(string tool, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);

            var text = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return Read(text);
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or OperationCanceledException)
        {
            // Not installed, or too slow to be worth waiting for. Neither is a fault of the deployment.
            cancellationToken.ThrowIfCancellationRequested();

            return null;
        }
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Number();
}
