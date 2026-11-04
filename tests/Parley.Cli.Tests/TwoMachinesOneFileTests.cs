using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Setup;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// One file, two checkouts, the real adapter under two machine names, and a real broker neither machine describes
/// (SPEC.md §4.1.2): the owner's example, as far as one computer can show it.
/// </summary>
[Integration]
public sealed class TwoMachinesOneFileTests : IAsyncDisposable
{
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-two-").FullName;
    private readonly List<Process> _running = [];
    private readonly int _port = Loopback.FreePort();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => $"http://127.0.0.1:{_port}";

    private string BrokerRoot => Path.Combine(_base, "broker");

    private string NoProjects => Path.Combine(_base, "no-projects");

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

    private static string AdapterBinary()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var root = here;

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        return Path.Combine(
            root.FullName, "src", "Parley.Mcp", "bin", here.Parent!.Name, here.Name,
            OperatingSystem.IsWindows() ? "parley-mcp.exe" : "parley-mcp");
    }

    private async Task ServeAsync()
    {
        Directory.CreateDirectory(Path.Combine(BrokerRoot, "projects", "alpha"));
        File.WriteAllText(
            Path.Combine(BrokerRoot, "projects", "alpha", "project.json"),
            $$"""{ "endpoint": "{{Address}}" }""");

        var serve = ToolProcess.Launch([(ProjectManifest.PathVariable, BrokerRoot)], "serve", "--no-web-ui");
        _running.Add(serve);
        serve.OutputDataReceived += (_, _) => { };
        serve.ErrorDataReceived += (_, _) => { };
        serve.BeginOutputReadLine();
        serve.BeginErrorReadLine();

        await UntilAsync(
            async () => (await BrokerProject.AskAsync(Address, Ct)).Project == "alpha",
            "the broker never answered");
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string otherwise)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

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

    private string Checkout(string name, string file)
    {
        var folder = Path.Combine(_base, name);

        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".mcp.json"), file);

        return folder;
    }

    private Process Adapter(string machine, string folder, string project = "alpha")
    {
        var start = new ProcessStartInfo(AdapterBinary())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = folder,
        };

        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("PARLEY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        // What a client hands the adapter from the entry, and nothing else.
        start.Environment["PARLEY_AGENT"] = "infra";
        start.Environment["PARLEY_PROJECT"] = project;
        start.Environment["PARLEY_MACHINE"] = machine;
        start.Environment[ProjectManifest.PathVariable] = NoProjects;

        var process = Process.Start(start)!;

        _running.Add(process);

        return process;
    }

    private string File_(string entryProject = "alpha") =>
        $$"""
        { "parleyAgentId": "{{Id}}",
          "mcpServers": { "parley-{{entryProject}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "{{entryProject}}" } } },
          "parleyMachines": {
            "laptop": { "parley-{{entryProject}}": { "PARLEY_OWNS": "Answers questions about the system.", "PARLEY_BROKER": "{{Address}}" } },
            "server": { "parley-{{entryProject}}": { "PARLEY_OWNS": "Makes changes on this host.", "PARLEY_BROKER": "{{Address}}" } } } }
        """;

    [Fact]
    public async Task One_file_gives_each_machine_its_own_line_and_folder()
    {
        await ServeAsync();

        var onLaptop = Checkout("on-laptop", File_());
        var onServer = Checkout("on-server", File_());

        _ = Adapter("laptop", onLaptop);
        _ = Adapter("server", onServer);

        using var channel = GrpcChannel.ForAddress(Address);
        var operators = new Wire.Operators.OperatorsClient(channel);

        Wire.RegistryReply registry = new();

        await UntilAsync(
            async () =>
            {
                registry = await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct);

                return registry.Rows.Any(row => row.Name == "infra@laptop") && registry.Rows.Any(row => row.Name == "infra@server");
            },
            "both never registered");

        var laptop = registry.Rows.Single(row => row.Name == "infra@laptop");
        var server = registry.Rows.Single(row => row.Name == "infra@server");

        Assert.Equal("Answers questions about the system.", laptop.Owns);
        Assert.Equal("Makes changes on this host.", server.Owns);
        Assert.Equal(onLaptop, laptop.Workspace);
        Assert.Equal(onServer, server.Workspace);
    }

    [Fact]
    public async Task The_address_is_the_membership_whatever_the_entry_is_filed_under()
    {
        // SPEC.md §4.1: the protocol has no concept of a project, so an adapter is in whichever project answers.
        await ServeAsync();

        _ = Adapter("server", Checkout("on-server", File_("beta")), "beta");

        using var channel = GrpcChannel.ForAddress(Address);
        var operators = new Wire.Operators.OperatorsClient(channel);

        await UntilAsync(
            async () => (await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct))
                .Rows.Any(row => row.Name == "infra@server"),
            "it never joined the broker at its address");
    }
}
