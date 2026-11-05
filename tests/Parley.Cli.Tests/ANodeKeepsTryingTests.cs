using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A node nobody is watching, waiting for a broker it cannot reach: the real tool on both sides of a loopback port.
/// </summary>
/// <remarks>
/// Two roots stand for two machines, with <c>PARLEY_MACHINE</c> naming the second. The address is a loopback one; a tunnel
/// looks the same from here.
/// </remarks>
[Integration]
public sealed class ANodeKeepsTryingTests : IAsyncDisposable
{
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-trying-").FullName;
    private readonly List<Process> _running = [];
    private readonly int _port = Loopback.FreePort();

    public ANodeKeepsTryingTests()
    {
        Directory.CreateDirectory(Path.Combine(BrokerRoot, "projects", "alpha"));
        Directory.CreateDirectory(MachineRoot);
        Directory.CreateDirectory(Path.Combine(Checkout, ".git"));

        File.WriteAllText(
            Path.Combine(BrokerRoot, "projects", "alpha", "project.json"),
            $$"""{ "endpoint": "{{Address}}" }""");

        File.WriteAllText(
            Path.Combine(Checkout, ".mcp.json"),
            $$"""
            { "parleyAgentId": "{{Id}}",
              "mcpServers": { "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } } }
            """);

        File.WriteAllText(
            Catalogue,
            new JsonObject
            {
                ["agents"] = new JsonObject
                {
                    [Id] = new JsonObject { ["command"] = StandingAgent.Command, ["workspace"] = Checkout },
                },
            }.ToJsonString());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string BrokerRoot => Path.Combine(_base, "broker");

    private string MachineRoot => Path.Combine(_base, "server");

    private string Checkout => Path.Combine(_base, "checkouts", "orders");

    private string Catalogue => Path.Combine(MachineRoot, "node.json");

    private string Address => $"http://127.0.0.1:{_port}";

    private (string, string)[] OnThatMachine =>
    [
        (ProjectManifest.PathVariable, MachineRoot),
        ("PARLEY_MACHINE", "server"),
        ("PARLEY_NODE_CONFIG", Catalogue),
    ];

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _running)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }

        await Task.Yield();

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private async Task<Process> ServeAsync()
    {
        var serve = ToolProcess.Launch([(ProjectManifest.PathVariable, BrokerRoot)], "serve", "--no-web-ui");
        _running.Add(serve);
        serve.OutputDataReceived += (_, _) => { };
        serve.ErrorDataReceived += (_, _) => { };
        serve.BeginOutputReadLine();
        serve.BeginErrorReadLine();

        await UntilAsync(
            async () =>
            {
                try
                {
                    using var probe = new System.Net.Sockets.TcpClient();
                    await probe.ConnectAsync(System.Net.IPAddress.Loopback, _port, Ct);
                    return true;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    return false;
                }
            },
            "the broker never answered");

        return serve;
    }

    private (Process Node, ConcurrentQueue<string> Lines) Up(params string[] arguments) => UpAt(Address, arguments);

    private (Process Node, ConcurrentQueue<string> Lines) UpAt(string address, params string[] arguments)
    {
        var node = ToolProcess.Launch(OnThatMachine, ["up", "--broker", address, .. arguments]);
        var lines = new ConcurrentQueue<string>();

        _running.Add(node);
        node.OutputDataReceived += (_, line) =>
        {
            if (line.Data is { } said)
            {
                lines.Enqueue(said);
            }
        };
        node.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } said)
            {
                lines.Enqueue(said);
            }
        };
        node.BeginOutputReadLine();
        node.BeginErrorReadLine();

        return (node, lines);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string otherwise, int seconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(100, Ct).ConfigureAwait(false);
        }

        Assert.Fail(otherwise);
    }

    private static Task SaidAsync(ConcurrentQueue<string> lines, string text, int seconds = 45) =>
        UntilAsync(
            () => Task.FromResult(lines.Any(line => line.Contains(text, StringComparison.Ordinal))),
            $"never said '{text}'. It said: {string.Join(" | ", lines)}",
            seconds);

    /// <summary>A loopback relay that can stop carrying bytes while keeping every socket open, as a dead far end does.</summary>
    private sealed class SilentRelay : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<System.Net.Sockets.TcpClient> _held = [];
        private volatile bool _silent;

        public SilentRelay(int target)
        {
            _listener.Start();
            _ = Task.Run(() => AcceptAsync(target));
        }

        public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        public void GoSilent() => _silent = true;

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();

            lock (_held)
            {
                _held.ForEach(client => client.Dispose());
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync(int target)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var outbound = new System.Net.Sockets.TcpClient();

                    await outbound.ConnectAsync(System.Net.IPAddress.Loopback, target, _stop.Token);

                    lock (_held)
                    {
                        _held.Add(inbound);
                        _held.Add(outbound);
                    }

                    _ = Task.Run(() => PumpAsync(inbound, outbound));
                    _ = Task.Run(() => PumpAsync(outbound, inbound));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                // Stopped, which is how this ends.
            }
        }

        private async Task PumpAsync(System.Net.Sockets.TcpClient from, System.Net.Sockets.TcpClient to)
        {
            var buffer = new byte[16 * 1024];

            try
            {
                while (true)
                {
                    var read = await from.GetStream().ReadAsync(buffer, _stop.Token);

                    if (read == 0)
                    {
                        return;
                    }

                    // Read and dropped: the sockets stay open and nothing arrives.
                    if (!_silent)
                    {
                        await to.GetStream().WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped, which is how this ends.
            }
        }
    }

    [Fact]
    public async Task It_waits_for_a_broker_that_comes_later_and_rejoins_after_an_outage()
    {
        // Before any broker, as a machine that comes up before its tunnel does.
        var (node, lines) = Up("--keep-trying");

        await SaidAsync(lines, "No broker is answering");
        Assert.False(node.HasExited);

        var serve = await ServeAsync();

        await SaidAsync(lines, "Can start:");

        static int Times(ConcurrentQueue<string> said, string text) =>
            said.Count(line => line.Contains(text, StringComparison.Ordinal));

        var noticedBefore = Times(lines, "No broker is answering");
        var backBefore = Times(lines, "answering again");

        // The tunnel drops.
        serve.Kill(entireProcessTree: true);
        Assert.True(serve.WaitForExit(10_000));

        await UntilAsync(
            () => Task.FromResult(Times(lines, "No broker is answering") > noticedBefore),
            $"it never noticed. It said: {string.Join(" | ", lines)}");

        Assert.False(node.HasExited);

        // And comes back: said again, after the outage rather than after the first join.
        await ServeAsync();

        await UntilAsync(
            () => Task.FromResult(Times(lines, "answering again") > backBefore),
            $"it never rejoined. It said: {string.Join(" | ", lines)}");

        Assert.False(node.HasExited);
    }

    [Fact]
    public async Task A_second_node_waits_for_the_first_to_go_rather_than_leaving_the_machine_with_none()
    {
        // A broker that has not noticed this machine's last connection die says exactly this, after a reboot or a dead tunnel.
        await ServeAsync();

        var (first, firstSaid) = Up("--keep-trying");

        await SaidAsync(firstSaid, "Can start:");

        var (second, secondSaid) = Up("--keep-trying");

        await SaidAsync(secondSaid, "keeps trying");
        Assert.False(second.HasExited);

        first.Kill(entireProcessTree: true);
        Assert.True(first.WaitForExit(10_000));

        await SaidAsync(secondSaid, "Can start:");
        Assert.False(second.HasExited);
    }

    [Fact]
    public async Task A_person_typing_it_is_told_about_a_second_node_and_it_stops()
    {
        await ServeAsync();

        var (_, firstSaid) = Up("--keep-trying");

        await SaidAsync(firstSaid, "Can start:");

        var (code, _, _) = ToolProcess.RunFully(OnThatMachine, "up", "--broker", Address);

        Assert.Equal(78, code);
    }

    [Fact]
    [Slow]
    public async Task A_tunnel_that_goes_silent_is_noticed_and_the_broker_lets_go()
    {
        // The hard case: nothing closes. A dead machine behind ssh leaves sockets open and silent on both sides.
        await ServeAsync();

        using var relay = new SilentRelay(_port);

        var (first, firstSaid) = UpAt($"http://127.0.0.1:{relay.Port}", "--keep-trying");

        await SaidAsync(firstSaid, "Can start:");

        relay.GoSilent();

        await SaidAsync(firstSaid, "No broker is answering", seconds: 150);

        first.Kill(entireProcessTree: true);
        Assert.True(first.WaitForExit(10_000));

        // The broker pings too, so a node back on a working path joins by itself.
        var (second, secondSaid) = Up("--keep-trying");

        await SaidAsync(secondSaid, "Can start:", seconds: 150);
        Assert.False(second.HasExited);
    }

    /// <summary>A broker that ends every node's stream cleanly, with no error for the node to read as an outage.</summary>
    private sealed class EndingBroker : Parley.Transport.Nodes.NodesBase
    {
        private static int _joins;

        public static int Joins => Volatile.Read(ref _joins);

        public static void Reset() => Volatile.Write(ref _joins, 0);

        public override Task Join(
            Parley.Transport.JoinRequest request,
            Grpc.Core.IServerStreamWriter<Parley.Transport.NodeCommand> responseStream,
            Grpc.Core.ServerCallContext context)
        {
            _ = Interlocked.Increment(ref _joins);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_stream_the_broker_ended_cleanly_is_waited_out_like_an_outage()
    {
        EndingBroker.Reset();

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();

        Microsoft.Extensions.DependencyInjection.GrpcServicesExtensions.AddGrpc(builder.Services);
        Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.ConfigureKestrel(
            builder.WebHost,
            options => options.Listen(
                System.Net.IPAddress.Loopback,
                _port,
                listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));

        await using var broker = builder.Build();

        Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions.MapGrpcService<EndingBroker>(broker);
        await broker.StartAsync(Ct);

        var (node, lines) = Up("--keep-trying");

        await UntilAsync(
            () => Task.FromResult(EndingBroker.Joins >= 2),
            $"it never came back after the stream ended. It said: {string.Join(" | ", lines)}");

        Assert.False(node.HasExited);
        Assert.Contains(lines, line => line.Contains("No broker is answering", StringComparison.Ordinal));

        await broker.StopAsync(Ct);
    }

    [Fact]
    public void Without_keep_trying_a_node_says_nobody_is_there_and_stops()
    {
        var (code, output, error) = ToolProcess.RunFully(OnThatMachine, "up", "--broker", Address);

        Assert.True(code is 69 or 78, $"{code}: {output}{error}");
    }

    [Fact]
    public void Another_command_does_not_have_the_flag()
    {
        var (code, _, error) = ToolProcess.RunFully(OnThatMachine, "registry", "--keep-trying");

        Assert.Equal(64, code);
        Assert.Contains("--keep-trying is only for", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://user:secret@127.0.0.1:1")]
    [InlineData("http://127.0.0.1:1/path")]
    [InlineData("http://127.0.0.1:1/?query=1")]
    [InlineData("http://127.0.0.1:1/#fragment")]
    public void Anything_more_than_a_host_and_port_is_refused_and_said(string address)
    {
        var (code, _, error) = ToolProcess.RunFully(OnThatMachine, "up", "--broker", address, "--keep-trying");

        Assert.Equal(78, code);
        Assert.Contains("not an address", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled", error, StringComparison.Ordinal);
    }
}
