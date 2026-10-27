using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That a broker which accepts a connection and never answers is given up on, rather than holding a surface for ever.
/// </summary>
/// <remarks>
/// Every call has a deadline, because the terminal runs its work one item at a time and one wedged broker would
/// otherwise stop every send, open and toggle in every project. Driven against a listener that accepts and says nothing.
/// </remarks>
public sealed class ASilentBrokerIsGivenUpOnTests
{
    [Fact]
    public async Task A_call_to_a_broker_that_never_answers_ends_at_its_deadline()
    {
        var ct = TestContext.Current.CancellationToken;

        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();

        // Every connection accepted is held until the test ends. A discarded one can be collected and closed, and the
        // client then sees a dropped connection (Unavailable) rather than a broker that says nothing.
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var held = new List<TcpClient>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await silent.AcceptTcpClientAsync(listening.Token);

                    lock (held)
                    {
                        held.Add(client);
                    }
                }
            }
            // Stopped as well as cancelled at the end, and the loop can come round to accept on a stopped listener, which
            // says it is not listening (macOS CI, 2026-10-27).
            catch (Exception ended) when (ended is OperationCanceledException or SocketException or ObjectDisposedException
                                              or InvalidOperationException)
            {
            }
        }, ct);

        var endpoint = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}";
        using var channel = BrokerDirectory.Connect(endpoint);
        var operators = new Wire.Operators.OperatorsClient(channel.Intercept(new Patience(TimeSpan.FromSeconds(1))));

        // A call with no deadline never returns, so the wait is bounded here: that case fails as a timeout, not a hang.
        var clock = Stopwatch.StartNew();
        var gaveUp = await Assert.ThrowsAsync<RpcException>(async () =>
            await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: ct).ResponseAsync.WaitAsync(TimeSpan.FromMinutes(1), ct));

        // The bound tells this call's one-second deadline from the thirty-second default, not one second from two. On a
        // loaded runner the deadline's timer fires late: Windows CI took eleven seconds once, beside the terminal suite.
        Assert.Equal(StatusCode.DeadlineExceeded, gaveUp.StatusCode);
        Assert.True(clock.Elapsed < Patience.Ordinary, $"gave up only after {clock.Elapsed}");

        await listening.CancelAsync();
        silent.Stop();
        await accepting;

        lock (held)
        {
            held.ForEach(client => client.Dispose());
        }
    }

    [Fact]
    public void Every_client_a_surface_is_handed_carries_the_deadline()
    {
        // The check above is of Patience; this is that the directory applies it, so a client made the old way — straight
        // over the channel — fails here rather than in somebody's terminal.
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Parley.Client", "BrokerDirectory.cs"));

        Assert.Contains("new Wire.Operators.OperatorsClient(channel.Value.Intercept(new Patience()))", source, StringComparison.Ordinal);
        Assert.Contains("new Wire.Agents.AgentsClient(channel.Value.Intercept(new Patience()))", source, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Parley.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
