using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Cli;

/// <summary>
/// Whether the web UI a broker started is serving, and whether its address was taken before it started.
/// </summary>
/// <remarks>
/// <para>
/// <b>A started process is not a serving web UI.</b> The broker used to announce the address the moment the child
/// process started. A web UI whose port was held by somebody else then exited a second later, and the broker had
/// already told the person to open an address that served the other program, or nothing.
/// </para>
/// <para>
/// <b>Nor is an address that accepts a connection.</b> The broker then waited for a connection to the address to be
/// accepted. A program that took the address after the broker looked and before the web UI bound accepted it too, and
/// was announced as this broker's web UI while the web UI failed to bind and exited (review, lens 1 run B on
/// 6561e9ea). So the web UI says itself that it is serving, in one line on its standard output
/// (<see cref="WebUiAddress.ServingMarker"/>). A program another user runs cannot write there, but one the same
/// person runs can (on Linux, through <c>/proc</c>), so the line is trusted as far as the person's own processes are, and no
/// further.
/// </para>
/// <para>
/// <b>A connection is still how the address is asked about before the web UI starts.</b> Something already answering
/// there means a web UI started now could only fail to bind, so it is not started and the reason is known.
/// </para>
/// <para>
/// Not part of the block in <c>Program.cs</c> that starts the child, because that block is held to having no loop:
/// it must not wait on or restart the child. This waits on the child's first line, once, with a deadline.
/// </para>
/// </remarks>
internal static class WebUiReadiness
{
    /// <summary>What a person does when another program holds the web UI's address: one ASCII line.</summary>
    /// <remarks>
    /// Both ways, because <c>serve</c> reads <c>--web-ui</c> only beside <c>--with-web-ui</c>: advice to serve with
    /// <c>--web-ui</c> alone changed nothing, and the broker a logon task runs is changed by installing the task
    /// again, not by a command typed once.
    /// </remarks>
    internal const string ElsewhereAdvice =
        "Stop that program, or give the web UI another address: for the logon task, "
        + "`parley autostart install --web-ui <address>`; by hand, `parley serve --with-web-ui --web-ui <address>`.";

    /// <summary>How long a web UI is given to say it is serving before the broker says it has not.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>What became of a web UI the broker started.</summary>
    internal enum Outcome
    {
        /// <summary>It said it was serving, and was still running.</summary>
        Answered,

        /// <summary>It exited before it said so.</summary>
        Exited,

        /// <summary>It is still running and has not said so within the deadline.</summary>
        Silent,

        /// <summary>The broker began stopping first, so what the web UI did no longer matters to anyone.</summary>
        Stopping,
    }

    /// <summary>Reads a started web UI's standard output and standard error for as long as it runs.</summary>
    /// <param name="child">
    /// The web UI's process, started with its standard output and standard error redirected and neither yet read.
    /// </param>
    /// <param name="log">Where every other line it writes on its standard output goes: the broker's own output.</param>
    /// <param name="errors">
    /// Where every line it writes on its standard error goes: the broker's error output, which is copied into
    /// <c>--log</c>.
    /// </param>
    /// <returns>What it has said.</returns>
    /// <remarks>
    /// <para>
    /// Read for the child's whole life, not only until it says it is serving: a pipe nobody reads fills, and a web UI
    /// whose writes block stops answering.
    /// </para>
    /// <para>
    /// <b>Its errors are read too, so its warnings reach the log.</b> Under a hidden logon task the broker has no
    /// console, and a web UI writing to an inherited error handle wrote to nothing: a <c>webUi.hosts</c> entry it
    /// refused, or the names it also answers to, were seen by nobody. Each line is copied as the output's are, with
    /// its control characters neutralised.
    /// </para>
    /// </remarks>
    internal static Said Watch(Process child, TextWriter log, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(errors);

        var said = new Said(log);

        child.OutputDataReceived += (_, line) => said.Line(line.Data);
        child.ErrorDataReceived += (_, line) => Pass(errors, line.Data);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        return said;
    }

    /// <summary>Copies one line the web UI wrote to one of the broker's writers, with nothing in it that could act.</summary>
    /// <param name="to">The broker's writer.</param>
    /// <param name="line">The line, or null at the end of the child's output.</param>
    /// <remarks>
    /// <para>
    /// <b>Neutralised, because the web UI's log carries text it did not write.</b> A request path is logged as it was
    /// asked for, decoded, by a request the console then refuses: a cross-site page's image, a rebinding name. Copied
    /// raw, an escape sequence in one clears or retitles the person's terminal, or hides a line in the log.
    /// </para>
    /// <para>
    /// Runs on a thread the process's reader owns, where an exception would end the broker; so a broker output that
    /// has gone is not this method's concern.
    /// </para>
    /// </remarks>
    internal static void Pass(TextWriter to, string? line)
    {
        if (line is null)
        {
            return;
        }

        try
        {
            to.WriteLine(HistoryText.Neutralised(line));
        }
        catch (Exception gone) when (gone is ObjectDisposedException or IOException)
        {
            // The broker's output closed as it stopped; the web UI is being ended with it.
        }
    }

    /// <summary>Waits until the web UI says it is serving, exits, or runs out of time.</summary>
    /// <param name="said">What it has said, from <see cref="Watch"/>.</param>
    /// <param name="child">The web UI's process.</param>
    /// <param name="deadline">How long to wait.</param>
    /// <param name="cancellationToken">The broker stopping, which ends the wait without throwing.</param>
    /// <returns>What became of it.</returns>
    internal static async Task<Outcome> AwaitAsync(
        Said said, Process child, TimeSpan deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(said);
        ArgumentNullException.ThrowIfNull(child);

        if (!child.HasExited && !cancellationToken.IsCancellationRequested)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            limit.CancelAfter(deadline);

            await Task.WhenAny(said.Serving, child.WaitForExitAsync(limit.Token), Task.Delay(Timeout.Infinite, limit.Token))
                .ConfigureAwait(false);
        }

        // Asked in this order: a child that exited is not serving whatever it said, and a broker that is stopping has
        // nobody to tell.
        if (child.HasExited)
        {
            return Outcome.Exited;
        }

        if (said.Serving.IsCompleted)
        {
            return Outcome.Answered;
        }

        return cancellationToken.IsCancellationRequested ? Outcome.Stopping : Outcome.Silent;
    }

    /// <summary>
    /// After a web UI was announced as not answering yet, says once what became of it: that it is serving now, or that
    /// it exited. Says nothing once the broker is stopping.
    /// </summary>
    /// <param name="said">What it has said, from <see cref="Watch"/>.</param>
    /// <param name="child">The web UI's process.</param>
    /// <param name="address">Its address.</param>
    /// <param name="log">The broker's output.</param>
    /// <param name="stopping">The broker stopping.</param>
    /// <returns>A task completing once that is said, or once the broker is stopping.</returns>
    internal static async Task FollowUpAsync(
        Said said, Process child, string address, TextWriter log, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(said);
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            await Task.WhenAny(said.Serving, child.WaitForExitAsync(stopping), Task.Delay(Timeout.Infinite, stopping))
                .ConfigureAwait(false);

            if (stopping.IsCancellationRequested)
            {
                return;
            }

            var taken = child.HasExited && await TakenAsync(address, stopping).ConfigureAwait(false);

            var line = child.HasExited
                ? $"   Web UI:   did not start ({Exited(child.ExitCode, address, taken)})."
                    + " The broker is still serving."
                    + (taken ? $" {ElsewhereAdvice}" : string.Empty)
                : $"   Web UI:   {address} is answering now. Open that one in a browser.";

            if (!stopping.IsCancellationRequested)
            {
                await log.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception gone) when (gone is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // The child or the broker's output went away while the broker stopped; there is nobody left to tell.
        }
    }

    /// <summary>Whether something already accepts connections at a web UI's address.</summary>
    /// <param name="address">The address the web UI would bind, such as <c>http://127.0.0.1:5280</c>.</param>
    /// <param name="cancellationToken">Stops the probe.</param>
    /// <returns>True when a connection was accepted there.</returns>
    internal static async Task<bool> TakenAsync(string address, CancellationToken cancellationToken)
    {
        if (Probe(address) is not var (host, port))
        {
            return false;
        }

        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        limit.CancelAfter(TimeSpan.FromSeconds(1));

        try
        {
            await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);

            return true;
        }
        catch (Exception failure) when (failure is SocketException or OperationCanceledException)
        {
            // The broker stopping answers "no" rather than throwing, so the caller still reaches the finally that
            // ends the child it started.
            return false;
        }
    }

    /// <summary>The words for a web UI that did not start, after it exited.</summary>
    /// <param name="exitCode">Its exit code.</param>
    /// <param name="address">Its address.</param>
    /// <param name="takenNow">Whether something else answers at that address now.</param>
    /// <returns>The reason, as a phrase.</returns>
    /// <remarks>
    /// Something answering after this broker's web UI exited is not this broker's web UI, so it is named as another
    /// program's: the console is not running, and the address is held.
    /// </remarks>
    internal static string Exited(int exitCode, string address, bool takenNow) =>
        takenNow
            ? Taken(address)
            : string.Create(CultureInfo.InvariantCulture, $"parley-web-ui exited with code {exitCode}");

    /// <summary>The words for an address another program holds.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The reason, as a phrase.</returns>
    internal static string Taken(string address) =>
        $"{address} is already in use by another program, so this broker's web UI is not running";

    /// <summary>Where to connect to reach what is listening at an address.</summary>
    /// <param name="address">The web UI's address.</param>
    /// <returns>A host and port, or null when the address names none.</returns>
    /// <remarks>
    /// A bind on every interface is reached on loopback: a wildcard is not something a connection can be made to,
    /// and the broker is on the same machine as the web UI it started.
    /// </remarks>
    internal static (string Host, int Port)? Probe(string address)
    {
        if (!Uri.TryCreate(address.Replace("://*", "://0.0.0.0", StringComparison.Ordinal)
                .Replace("://+", "://0.0.0.0", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            || uri.Port <= 0)
        {
            return null;
        }

        var host = uri.IdnHost.Trim('[', ']');

        if (IPAddress.TryParse(host, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
        {
            host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1";
        }

        return (host, uri.Port);
    }

    /// <summary>What a started web UI has said on its standard output.</summary>
    /// <param name="log">Where every line other than the first serving line goes.</param>
    internal sealed class Said(TextWriter log)
    {
        private readonly TaskCompletionSource<string> _serving = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes with the addresses it bound, once it says it is serving.</summary>
        internal Task<string> Serving => _serving.Task;

        /// <summary>Takes one line the web UI wrote.</summary>
        /// <param name="line">The line, or null at the end of its output.</param>
        /// <remarks>
        /// Every line but the serving one is passed on through <see cref="Pass"/>, neutralised.
        /// </remarks>
        internal void Line(string? line)
        {
            if (line is null)
            {
                return;
            }

            if (line.StartsWith(WebUiAddress.ServingMarker, StringComparison.Ordinal)
                && _serving.TrySetResult(line[WebUiAddress.ServingMarker.Length..]))
            {
                return;
            }

            Pass(log, line);
        }
    }
}
