using System.Net;
using System.Net.Sockets;
using Grpc.Net.Client;

namespace Parley.Cli.Tests;

/// <summary>
/// The three questions every test that serves a real port asks: a free port, whether an
/// endpoint answers, and whether a port has been let go.
/// </summary>
/// <remarks>
/// One copy. Four test files each grew their own within an afternoon, and two had already begun
/// to differ in how long they waited — which is the state this repository has been bitten by
/// before, and the reason its wait helper was consolidated once already. A duplicated wait is
/// worse than most duplication: the copy that drifts does not fail, it waits differently, and
/// stops passing only on somebody else's machine.
/// </remarks>
internal static class Loopback
{
    /// <summary>Takes a port the kernel is willing to give, and gives it straight back.</summary>
    /// <returns>A port number, free at the moment of asking.</returns>
    /// <remarks>
    /// Racy in principle and settled in practice: another process could take the port between
    /// the release and the bind. Port zero cannot be used where the address has to go into a
    /// configuration before anything starts. A collision surfaces as a test failing, not as a
    /// silent pass.
    /// </remarks>
    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>An endpoint nothing in this suite can end up listening on.</summary>
    /// <returns>A loopback address that refuses a connection.</returns>
    /// <remarks>
    /// <para>
    /// <b>Not <see cref="FreePort"/>, and the difference is a real failure this suite had.</b> That
    /// one returns a port the kernel just offered, which is fine when something is about to bind it
    /// and wrong when the test needs it to stay empty: another process can take it in between, and
    /// then the endpoint a test called dead answers. It surfaced as
    /// <c>UnreachableProjectTests</c> failing in a full-suite run on Linux and passing six times out
    /// of six in isolation — the ephemeral range is shared, and splitting the terminal into its own
    /// host added an eleventh process competing for it.
    /// </para>
    /// <para>
    /// Port 1 instead, which is outside the ephemeral range every helper here draws from, is
    /// privileged on Unix, and is bound by nothing in this repository. Connecting to it refuses
    /// immediately rather than hanging, which is what the tests using this are waiting on.
    /// </para>
    /// </remarks>
    public static string NothingListens() => "http://127.0.0.1:1";

    /// <summary>Polls until an endpoint accepts a connection, or gives up.</summary>
    /// <param name="endpoint">Where to connect.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="attempts">How many times to try, a tenth of a second apart plus the connect.</param>
    /// <returns><see langword="true"/> if it answered within the attempts.</returns>
    public static async Task<bool> AnswersAsync(
        string endpoint, CancellationToken cancellationToken, int attempts = 80)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                using var channel = GrpcChannel.ForAddress(endpoint);
                await channel.ConnectAsync(timeout.Token);

                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>Whether an endpoint's port can be bound right now, which is whether it was let go.</summary>
    /// <param name="endpoint">The endpoint whose port to try.</param>
    /// <returns><see langword="true"/> if nothing holds it.</returns>
    public static bool CanBind(string endpoint)
    {
        var port = new Uri(endpoint).Port;

        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
