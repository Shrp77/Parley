using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Setup;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// A machine with no project of its own, joining one over an address: the real tool on both sides of a loopback port
/// (SPEC.md §4.1.2).
/// </summary>
/// <remarks>
/// Two roots stand for two machines, with <c>PARLEY_MACHINE</c> naming the second, because what is being held is that the
/// second holds nothing of the first. The address is a loopback one; a tunnel looks the same from here.
/// </remarks>
[Integration]
public sealed class AMachineWithOnlyATunnelTests : IAsyncDisposable
{
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-tunnel-").FullName;
    private readonly List<Process> _running = [];
    private readonly int _port = Loopback.FreePort();

    public AMachineWithOnlyATunnelTests()
    {
        Directory.CreateDirectory(Path.Combine(BrokerRoot, "projects", "alpha"));
        Directory.CreateDirectory(MachineRoot);
        Directory.CreateDirectory(Path.Combine(Checkout, ".git"));

        File.WriteAllText(
            Path.Combine(BrokerRoot, "projects", "alpha", "project.json"),
            $$"""{ "endpoint": "{{Address}}" }""");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string BrokerRoot => Path.Combine(_base, "broker");

    private string MachineRoot => Path.Combine(_base, "server");

    private string Checkouts => Path.Combine(_base, "checkouts");

    private string Checkout => Path.Combine(Checkouts, "infra");

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
            async () => (await BrokerProject.AskAsync(Address, Ct)).Project == "alpha",
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

    private static Task SaidAsync(ConcurrentQueue<string> lines, string text) =>
        UntilAsync(
            () => Task.FromResult(lines.Any(line => line.Contains(text, StringComparison.Ordinal))),
            $"never said '{text}'. It said: {string.Join(" | ", lines)}");

    private void WriteRepository(string extra = "")
    {
        File.WriteAllText(
            Path.Combine(Checkout, ".mcp.json"),
            $$"""
            { "parleyAgentId": "{{Id}}",
              "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "alpha" } } {{extra}} },
              "parleyMachines": { "server": { "parley-alpha": { "PARLEY_BROKER": "{{Address}}" } } } }
            """);
    }

    private void WriteCatalogue(string command) =>
        File.WriteAllText(
            Catalogue,
            new JsonObject
            {
                ["agents"] = new JsonObject
                {
                    [Id] = new JsonObject { ["command"] = command, ["workspace"] = Checkout },
                },
            }.ToJsonString());

    [Fact]
    public async Task Enrolling_by_address_writes_this_machines_block_and_makes_nothing_here()
    {
        await ServeAsync();

        var (code, output, error) = ToolProcess.RunFully(
            OnThatMachine, "enroll", Checkouts, "--all", "--project", "alpha", "--broker", Address);

        Assert.True(code == 0, output + error);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Checkout, ".mcp.json")))!;

        Assert.Equal(Address, (string?)root["parleyMachines"]!["server"]!["parley-alpha"]!["PARLEY_BROKER"]);
        Assert.Equal("alpha", (string?)root["mcpServers"]!["parley-alpha"]!["env"]!["PARLEY_PROJECT"]);
        Assert.DoesNotContain(Checkouts.Replace('\\', '/'), root.ToJsonString(), StringComparison.Ordinal);

        // This machine's catalogue holds the folder, and this machine holds no project.
        Assert.Contains(JsonNode.Parse(File.ReadAllText(Catalogue))!["agents"]!.AsObject(), pair => pair.Key.Length > 0);
        Assert.Empty(Directory.GetFiles(MachineRoot, "parley.db", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(MachineRoot, "project.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Enrolling_into_a_project_the_broker_does_not_serve_writes_nothing()
    {
        await ServeAsync();

        var (code, _, error) = ToolProcess.RunFully(
            OnThatMachine, "enroll", Checkouts, "--all", "--project", "beta", "--broker", Address);

        Assert.Equal(78, code);
        Assert.Contains("alpha", error, StringComparison.Ordinal);
        Assert.Contains("beta", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Checkout, ".mcp.json")));
    }

    [Fact]
    public void Enrolling_with_nobody_answering_writes_nothing()
    {
        var (code, _, error) = ToolProcess.RunFully(
            OnThatMachine, "enroll", Checkouts, "--all", "--project", "alpha", "--broker", Address);

        Assert.Equal(78, code);
        Assert.Contains("No broker answered", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Checkout, ".mcp.json")));
    }

    [Fact]
    public void Enrolling_by_address_alone_asks_for_the_project_rather_than_falling_over()
    {
        var (code, _, error) = ToolProcess.RunFully(OnThatMachine, "enroll", Checkouts, "--all", "--broker", Address);

        Assert.Equal(78, code);
        Assert.Contains("--project", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_other_verb_still_refuses_a_project_this_machine_does_not_describe()
    {
        await ServeAsync();

        var (code, _, error) = ToolProcess.RunFully(OnThatMachine, "registry", "--project", "alpha", "--broker", Address);

        Assert.Equal(78, code);
        Assert.Contains("No project named 'alpha'", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repositorys_block_redirects_no_command_but_waiting()
    {
        // A clone's file would otherwise receive the person's files and their node's join. A dead address is named so that this
        // can reach nothing of the machine running it, whatever goes wrong.
        await ServeAsync();
        WriteRepository();

        var (_, output, error) = ToolProcess.RunFullyIn(
            Checkout, OnThatMachine, "--launch-key", "parley-alpha", "--broker", "http://127.0.0.1:1", "registry");

        Assert.DoesNotContain("human-user@alpha", output, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:1", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launch_key_finds_the_address_for_the_hook()
    {
        await ServeAsync();
        WriteRepository();

        var (code, output, error) = ToolProcess.RunFullyIn(
            Checkout, OnThatMachine, "--launch-key", "parley-alpha", "--project", "alpha", "waiting", "infra", "--with-files");

        Assert.True(code == 0, output + error);
        Assert.StartsWith("0 0 0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_by_address_announces_only_the_project_it_was_told()
    {
        await ServeAsync();
        WriteRepository(
            """, "parley-beta": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "other", "PARLEY_PROJECT": "beta" } } """);
        WriteCatalogue(StandingAgent.Command);

        var (_, lines) = Up("--project", "alpha");

        await SaidAsync(lines, "Can start:");

        var joined = lines.First(line => line.Contains("Can start:", StringComparison.Ordinal));

        Assert.Contains("infra", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("other", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_an_entry_is_checked_against_refuses_a_project_that_broker_is_not()
    {
        // Asked of the check and never through `autostart install`: if this guard ever broke, that command would register a real
        // entry on whoever's machine ran the test. A mutation did exactly that once.
        await ServeAsync();

        var (project, refusal) = await BrokerProject.ConfirmAsync("beta", Address, Ct);

        Assert.Null(project);
        Assert.Contains("alpha", refusal, StringComparison.Ordinal);
        Assert.Contains("beta", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_for_a_node_names_its_project()
    {
        var (code, _, error) = ToolProcess.RunFully(OnThatMachine, "autostart", "install", "--broker", Address);

        Assert.Equal(78, code);
        Assert.Contains("--project", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_node_waits_for_the_first_to_go_rather_than_leaving_the_machine_with_none()
    {
        // A broker that has not noticed this machine's last connection die says exactly this, after a reboot or a dead tunnel.
        await ServeAsync();
        WriteRepository();
        WriteCatalogue(StandingAgent.Command);

        var (first, firstSaid) = Up("--project", "alpha", "--keep-trying");

        await SaidAsync(firstSaid, "Can start:");

        var (second, secondSaid) = Up("--project", "alpha", "--keep-trying");

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
        WriteRepository();
        WriteCatalogue(StandingAgent.Command);

        var (_, firstSaid) = Up("--project", "alpha", "--keep-trying");

        await SaidAsync(firstSaid, "Can start:");

        var (code, _, _) = ToolProcess.RunFully(OnThatMachine, "up", "--project", "alpha", "--broker", Address);

        Assert.Equal(78, code);
    }

    [Fact]
    public async Task A_stray_project_variable_does_not_defeat_the_block_the_hook_reads()
    {
        await ServeAsync();
        WriteRepository();

        var (code, output, error) = ToolProcess.RunFullyIn(
            Checkout,
            [.. OnThatMachine, ("PARLEY_PROJECT", "alpha")],
            "--launch-key", "parley-alpha", "waiting", "infra", "--with-files");

        Assert.True(code == 0, output + error);
    }

    [Fact]
    public async Task A_node_for_another_project_stops_and_says_so()
    {
        await ServeAsync();
        WriteRepository();
        WriteCatalogue(StandingAgent.Command);

        var (node, lines) = Up("--project", "beta");

        await UntilAsync(() => Task.FromResult(node.HasExited), "the node never stopped");

        Assert.Equal(78, node.ExitCode);
        await SaidAsync(lines, "serves project 'alpha'");
    }

    [Fact]
    [Slow]
    public async Task A_tunnel_that_goes_silent_is_noticed_and_the_broker_lets_go()
    {
        // The hard case: nothing closes. A dead machine behind ssh leaves sockets open and silent on both sides.
        await ServeAsync();
        WriteRepository();
        WriteCatalogue(StandingAgent.Command);

        using var relay = new SilentRelay(_port);

        var (first, firstSaid) = UpAt($"http://127.0.0.1:{relay.Port}", "--project", "alpha", "--keep-trying");

        await SaidAsync(firstSaid, "Can start:");

        relay.GoSilent();

        await UntilAsync(
            () => Task.FromResult(firstSaid.Any(line => line.Contains("No broker is answering", StringComparison.Ordinal))),
            $"it never noticed. It said: {string.Join(" | ", firstSaid)}",
            seconds: 150);

        first.Kill(entireProcessTree: true);
        Assert.True(first.WaitForExit(10_000));

        // The broker pings too, so a node back on a working path joins by itself.
        var (second, secondSaid) = Up("--project", "alpha", "--keep-trying");

        await UntilAsync(
            () => Task.FromResult(secondSaid.Any(line => line.Contains("Can start:", StringComparison.Ordinal))),
            $"the broker never let go. It said: {string.Join(" | ", secondSaid)}",
            seconds: 150);

        Assert.False(second.HasExited);
    }

    [Fact]
    public void An_address_that_is_not_one_is_said_rather_than_crashing()
    {
        var (code, _, error) = ToolProcess.RunFully(
            OnThatMachine, "up", "--broker", "not a url", "--project", "alpha", "--keep-trying");

        Assert.Equal(78, code);
        Assert.Contains("not an address", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_keep_trying_a_node_says_nobody_is_there_and_stops()
    {
        WriteRepository();
        WriteCatalogue(StandingAgent.Command);

        var (code, output, error) = ToolProcess.RunFully(OnThatMachine, "up", "--broker", Address);

        Assert.True(code is 69 or 78, $"{code}: {output}{error}");
    }

    [Fact]
    public async Task A_node_waits_for_a_broker_that_comes_later_keeps_its_agent_through_an_outage_and_rejoins()
    {
        var evidence = Path.Combine(_base, "launched.txt");

        WriteRepository();
        WriteCatalogue(StandingAgent.Recording(evidence, 1));

        // Before any broker, as a machine that comes up before its tunnel does.
        var (node, lines) = Up("--project", "alpha", "--keep-trying");

        await SaidAsync(lines, "No broker is answering");
        Assert.False(node.HasExited);

        var serve = await ServeAsync();

        await SaidAsync(lines, "Can start:");

        // The row a request to this agent needs, as an adapter's first attach would make it.
        using var channel = GrpcChannel.ForAddress(Address);

        using (var attach = new Wire.Agents.AgentsClient(channel).Attach(
            new Wire.AttachRequest
            {
                Agent = "infra@server",
                Adapter = "mcp",
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                Workspace = Checkout,
                Identity = Id,
            },
            cancellationToken: Ct))
        {
            Assert.True(await attach.ResponseStream.MoveNext(Ct));
        }

        // **A node joined by address starts an agent, in the folder its own catalogue names.**
        await UntilAsync(
            async () =>
            {
                try
                {
                    return (await new Wire.Operators.OperatorsClient(channel)
                        .StartAsync(new Wire.StartRequest { Agent = "infra@server" }, cancellationToken: Ct)).Changed;
                }
                catch (Grpc.Core.RpcException)
                {
                    return false;
                }
            },
            "the broker never agreed to start it");

        await UntilAsync(() => Task.FromResult(File.Exists(evidence)), "the node never started the agent");

        var launchedBefore = File.ReadAllLines(evidence).Length;

        // The tunnel drops.
        serve.Kill(entireProcessTree: true);
        Assert.True(serve.WaitForExit(10_000));

        await UntilAsync(
            () => Task.FromResult(lines.Count(line => line.Contains("No broker is answering", StringComparison.Ordinal)) >= 1),
            "it never noticed");

        Assert.False(node.HasExited);

        // And comes back.
        await ServeAsync();
        await SaidAsync(lines, "answering again");

        Assert.False(node.HasExited);
        Assert.Equal(launchedBefore, File.ReadAllLines(evidence).Length);
    }
}
