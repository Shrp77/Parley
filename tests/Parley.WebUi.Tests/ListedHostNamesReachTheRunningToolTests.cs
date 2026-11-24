using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Parley.Configuration;
using Parley.Web;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>That <c>webUi.hosts</c> reaches the console a person runs, and is never announced when it is not used.</summary>
/// <remarks>
/// <para>
/// <see cref="ListedHostNamesTests"/> hands <see cref="ConsoleHost.Build"/> its root as a seam, so it passes when
/// <see cref="ConsoleHost.RunAsync"/> passes none, which is the console every person runs: the owner's Tailscale name
/// would be refused again with every test green (review, lens 3 surface A on d99806d9). This starts the real tool with
/// the deployment root named the way a person's is, through <c>PARLEY_CONFIG</c>.
/// </para>
/// <para>
/// And the warning that the console also answers to the listed names is said only when they are used: a deployment that
/// sets <c>AllowedHosts</c> itself keeps its own list, so saying "also answering to" would tell the person something untrue.
/// </para>
/// </remarks>
[Integration]
public sealed class ListedHostNamesReachTheRunningToolTests : IDisposable
{
    private const string Listed = "workstation.tail0000.ts.net";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-listed-run-" + Path.GetRandomFileName());

    public ListedHostNamesReachTheRunningToolTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, ParleySettings.FileName),
            $$"""{ "webUi": { "hosts": [ "{{Listed}}" ] } }""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_deployment_that_chose_its_own_hosts_is_not_told_the_listed_names_are_answered()
    {
        var warned = new StringWriter();

        _ = ConsoleHost.Build(["--AllowedHosts=localhost"], "http://127.0.0.1:0", warned, deploymentRoot: _root);

        Assert.DoesNotContain("also answering to", warned.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_running_tool_reads_the_names_from_its_deployment_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        var tool = Path.Combine(
            AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "parley-web-ui.exe" : "parley-web-ui");

        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };

        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");

        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("PARLEY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment[ProjectManifest.PathVariable] = _root;

        var error = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, line) =>
        {
            lock (error)
            {
                error.AppendLine(line.Data);
            }
        };
        process.OutputDataReceived += (_, _) => { };

        Assert.True(process.Start());
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            var address = new Uri($"http://127.0.0.1:{port}/");
            var deadline = Stopwatch.StartNew();
            HttpStatusCode? listed = null;

            while (listed is null && deadline.Elapsed < Patience && !process.HasExited)
            {
                try
                {
                    listed = await StatusAsync(address, Listed, ct);
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100, ct);
                }
            }

            string said;

            lock (error)
            {
                said = error.ToString();
            }

            Assert.True(listed is not null, $"the tool never answered; it said: {said}");
            Assert.NotEqual(HttpStatusCode.BadRequest, listed);
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(address, "rebound.attacker.test", ct));
            Assert.Contains("also answering to " + Listed, said, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct).WaitAsync(Patience, ct);
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<HttpStatusCode> StatusAsync(Uri address, string host, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, "/favicon.ico"));
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, ct);

        return response.StatusCode;
    }
}
