using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Parley.Cli;

/// <summary>
/// Whether the web UI a broker started is answering, and whether its address was taken before it started.
/// </summary>
/// <remarks>
/// <para>
/// <b>A started process is not a serving web UI.</b> The broker used to announce the address the moment the child
/// process started. A web UI whose port was held by somebody else then exited a second later, and the broker had
/// already told the person to open an address that served the other program, or nothing. The likeliest holder is a
/// web UI an earlier broker left behind when it was killed, still serving that broker's settings.
/// </para>
/// <para>
/// <b>A connection on loopback is the test, not an HTTP request.</b> Kestrel accepts a connection only once it has
/// bound, and the question is whether it bound. An HTTP request would also have to satisfy the console's host
/// filter, which is a second thing that could fail for reasons that are not this.
/// </para>
/// <para>
/// Not part of the block in <c>Program.cs</c> that starts the child, because that block is held to having no loop:
/// it must not wait on or restart the child. This waits on an address, once, with a deadline.
/// </para>
/// </remarks>
internal static class WebUiReadiness
{
    /// <summary>How long a web UI is given to answer before the broker says it has not.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>What became of a web UI the broker started.</summary>
    internal enum Outcome
    {
        /// <summary>It answered on its address while still running.</summary>
        Answered,

        /// <summary>It exited before it answered.</summary>
        Exited,

        /// <summary>It is still running and has not answered within the deadline.</summary>
        Silent,
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

    /// <summary>Waits until the web UI answers, exits, or runs out of time.</summary>
    /// <param name="address">The address it was told to bind.</param>
    /// <param name="child">The web UI's process.</param>
    /// <param name="deadline">How long to wait.</param>
    /// <param name="cancellationToken">The broker stopping, which ends the wait without throwing.</param>
    /// <returns>What became of it.</returns>
    internal static async Task<Outcome> AwaitAsync(
        string address, Process child, TimeSpan deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);

        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (child.HasExited)
            {
                return Outcome.Exited;
            }

            // Asked again after the connection, so a child that failed while somebody else's listener accepted the
            // connection is not taken for one that answered.
            if (await TakenAsync(address, cancellationToken).ConfigureAwait(false))
            {
                return child.HasExited ? Outcome.Exited : Outcome.Answered;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The broker is stopping; what the child did no longer matters to anyone.
                break;
            }
        }

        return child.HasExited ? Outcome.Exited : Outcome.Silent;
    }

    /// <summary>The words for a web UI that did not start, after it exited.</summary>
    /// <param name="exitCode">Its exit code.</param>
    /// <param name="address">Its address.</param>
    /// <param name="takenNow">Whether something else answers at that address now.</param>
    /// <returns>The reason, as a phrase.</returns>
    internal static string Exited(int exitCode, string address, bool takenNow) =>
        takenNow
            ? Taken(address)
            : string.Create(CultureInfo.InvariantCulture, $"parley-web-ui exited with code {exitCode}");

    /// <summary>The words for an address another program already holds.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The reason, as a phrase.</returns>
    internal static string Taken(string address) =>
        $"{address} is already in use by another program, perhaps a web UI an earlier broker left running";

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
}
