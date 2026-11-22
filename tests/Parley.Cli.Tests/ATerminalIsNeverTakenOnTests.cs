using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>That a repository's file linked to a terminal never becomes the node's controlling terminal.</summary>
/// <remarks>
/// Found by review on Linux (lens 2). On Linux a session leader with no controlling terminal that opens a terminal
/// without <c>O_NOCTTY</c> takes it as its own. The autostart entry runs <c>parley serve</c> as such a leader, and the node
/// opens every catalogued repository's <c>.mcp.json</c> on each join and start. An agent that links its file to a
/// pseudo-terminal it holds the other end of was refused the read, but the broker had taken the terminal, and the agent
/// closing its end hung the broker up: every project it served, and every agent on the machine, lost it.
/// </remarks>
[Integration]
public sealed class ATerminalIsNeverTakenOnTests : IAsyncDisposable
{
    private const string Orders = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";
    private const string Billing = "1c3e5a77-3b41-4e0e-8a6f-7c4b2d9e5012";
    private const int ReadWrite = 2;
    private const int NoControllingTerminal = 0x100;

    private readonly string _base = Directory.CreateTempSubdirectory("parley-tty-").FullName;
    private readonly int _port = Loopback.FreePort();
    private Process? _serve;
    private int _master = -1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_master >= 0)
        {
            _ = close(_master);
        }

        if (_serve is { } serve)
        {
            try
            {
                if (!serve.HasExited)
                {
                    serve.Kill(entireProcessTree: true);
                    await serve.WaitForExitAsync(Ct);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            serve.Dispose();
        }

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    [Fact]
    public async Task A_launch_configuration_linked_to_a_terminal_leaves_the_broker_without_one()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux hands a session leader a terminal it merely opens.");
        Assert.SkipUnless(File.Exists("/usr/bin/setsid"), "setsid starts the broker as its own session, as systemd does.");

        _master = posix_openpt(ReadWrite | NoControllingTerminal);
        Assert.True(_master >= 0, "no pseudo-terminal");
        Assert.Equal(0, grantpt(_master));
        Assert.Equal(0, unlockpt(_master));
        var terminal = Marshal.PtrToStringAnsi(ptsname(_master))!;

        var broker = Path.Combine(_base, "broker");
        var machine = Path.Combine(_base, "machine");
        var orders = Path.Combine(_base, "orders");
        var billing = Path.Combine(_base, "billing");
        _ = Directory.CreateDirectory(Path.Combine(broker, "projects", "alpha"));
        _ = Directory.CreateDirectory(machine);
        _ = Directory.CreateDirectory(Path.Combine(orders, ".git"));
        _ = Directory.CreateDirectory(Path.Combine(billing, ".git"));
        File.WriteAllText(
            Path.Combine(broker, "projects", "alpha", "project.json"),
            $$"""{ "endpoint": "http://127.0.0.1:{{_port}}" }""");
        File.WriteAllText(
            Path.Combine(billing, ".mcp.json"),
            $$"""
            { "parleyAgentId": "{{Billing}}",
              "mcpServers": { "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } } } }
            """);
        _ = File.CreateSymbolicLink(Path.Combine(orders, ".mcp.json"), terminal);

        var catalogue = Path.Combine(machine, "node.json");
        File.WriteAllText(
            catalogue,
            new JsonObject
            {
                ["agents"] = new JsonObject
                {
                    [Orders] = new JsonObject { ["command"] = StandingAgent.Command, ["workspace"] = orders },
                    [Billing] = new JsonObject { ["command"] = StandingAgent.Command, ["workspace"] = billing },
                },
            }.ToJsonString());

        var lines = new ConcurrentQueue<string>();
        _serve = Setsid(
            [(ProjectManifest.PathVariable, broker), ("PARLEY_MACHINE", TestMachine.Name), (NodeCatalogue.PathVariable, catalogue)],
            "serve", "--with-node", "--no-web-ui");
        _serve.OutputDataReceived += (_, line) => lines.Enqueue(line.Data ?? string.Empty);
        _serve.ErrorDataReceived += (_, line) => lines.Enqueue(line.Data ?? string.Empty);
        _serve.BeginOutputReadLine();
        _serve.BeginErrorReadLine();

        // The node has joined and read the linked file once it says it refused it.
        var watch = Stopwatch.StartNew();

        while (!lines.Any(line => line.Contains("not an ordinary file", StringComparison.Ordinal)))
        {
            Assert.False(_serve.HasExited, "the broker ended: " + string.Join("\n", lines));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "the node never read the file: " + string.Join("\n", lines));
            await Task.Delay(50, Ct);
        }

        Assert.Equal("0", File.ReadAllText($"/proc/{_serve.Id}/stat").Split(' ')[6]);

        // And the agent hanging up its end ends nothing.
        Assert.Equal(0, close(_master));
        _master = -1;

        Assert.False(_serve.WaitForExit(TimeSpan.FromSeconds(2)), "the broker ended when the agent hung up: " + string.Join("\n", lines));
    }

    private static Process Setsid((string Name, string Value)[] environment, params string[] arguments)
    {
        var directory = Path.GetDirectoryName(typeof(ATerminalIsNeverTakenOnTests).Assembly.Location)!;
        var apphost = Path.Combine(directory, "parley");
        var start = new ProcessStartInfo("/usr/bin/setsid")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };

        if (File.Exists(apphost))
        {
            start.ArgumentList.Add(apphost);
        }
        else
        {
            start.ArgumentList.Add("dotnet");
            start.ArgumentList.Add(Path.Combine(directory, "parley.dll"));
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("PARLEY_", StringComparison.Ordinal)))
        {
            start.Environment[name] = "";
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        return Process.Start(start)!;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
