using Grpc.Net.Client;
using Parley.Broker;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What a broker given a log writes there: its start and stop always, and with the heartbeat a line a second and every
/// connection.
/// </summary>
/// <remarks>
/// Asked for after a Windows CI run where a fresh broker reset a connection and nothing said why. The heartbeat is what
/// such a run is read with, so it is held to saying what it claims.
/// </remarks>
public sealed class BrokerDiagnosticsTests
{
    private static readonly System.Text.RegularExpressions.Regex Beat = new(@" heartbeat \d+: ");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ParleyServer Server, StringWriter Log)> StartAsync(bool heartbeat)
    {
        var log = new StringWriter();
        var server = await ParleyServer.StartAsync(
            BrokerRuntime.OpenInMemory(BrokerOptions.Default, TimeProvider.System),
            "http://127.0.0.1:0",
            Ct,
            new ServerDiagnostics(log, heartbeat, "[test]"));

        return (server, log);
    }

    private static async Task CallAsync(ParleyServer server)
    {
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        _ = await new Wire.Operators.OperatorsClient(channel).RoadmapsAsync(new Wire.Empty(), cancellationToken: Ct);
    }

    private static string[] Lines(StringWriter log)
    {
        lock (log)
        {
            return log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    [Fact]
    public async Task With_the_heartbeat_a_broker_beats_every_second_and_logs_each_connection()
    {
        var (server, log) = await StartAsync(heartbeat: true);

        await CallAsync(server);
        await Task.Delay(TimeSpan.FromMilliseconds(3500), Ct);
        await server.DisposeAsync();

        var lines = Lines(log);
        var beats = lines.Count(l => Beat.IsMatch(l));

        Assert.True(beats >= 2, $"{beats} beats in three and a half seconds:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
        Assert.Contains(lines, l => l.Contains("[test] Parley.Server: listening on http://127.0.0.1:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(" opened from 127.0.0.1:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(" closed after ", StringComparison.Ordinal));

        // The beat says how the process is doing, and a stop is said, last.
        Assert.Contains(lines, l => l.Contains(", gap ", StringComparison.Ordinal) && l.Contains(" queued", StringComparison.Ordinal));
        Assert.EndsWith("Parley.Server: stopped", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_heartbeat_a_broker_with_a_log_says_only_that_it_started_and_stopped()
    {
        var (server, log) = await StartAsync(heartbeat: false);

        await CallAsync(server);
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
        await server.DisposeAsync();

        var lines = Lines(log);

        Assert.DoesNotContain(lines, l => Beat.IsMatch(l));
        Assert.DoesNotContain(lines, l => l.Contains("connection ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Parley.Server: listening on", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Parley.Server: stopping, with 0 agents attached", StringComparison.Ordinal));
    }
}
