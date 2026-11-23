using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Parley.Configuration;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>
/// That a web UI started the way a broker starts it says on its standard output, once it is serving, which addresses it
/// bound; and that one run by hand says nothing of the kind.
/// </summary>
/// <remarks>
/// <para>
/// A broker announces its web UI only on that line (<see cref="WebUiAddress.ServingMarker"/>), never because the address
/// answers: the address may be another program's. The line is documented as carrying the addresses bound, so what follows
/// the marker is held here too, though the broker does not read it today.
/// </para>
/// <para>
/// Only with <c>--until-input-closes</c>, the flag a broker passes. A person running <c>parley-web-ui</c> in a terminal
/// would otherwise see a line meant for a program (its usage says so).
/// </para>
/// </remarks>
[Integration]
public sealed class TheWebUiSaysWhenItIsServingTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-serving-" + Path.GetRandomFileName());

    public TheWebUiSaysWhenItIsServingTests() => Directory.CreateDirectory(_root);

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
    public async Task A_web_ui_a_broker_started_names_what_it_bound_once_it_is_serving()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = $"http://127.0.0.1:{FreePort()}";
        var output = new StringBuilder();
        using var process = Start(address, watch: true, output);

        try
        {
            var deadline = Stopwatch.StartNew();

            while (!Read(output).Contains(WebUiAddress.ServingMarker, StringComparison.Ordinal)
                   && deadline.Elapsed < Patience
                   && !process.HasExited)
            {
                await Task.Delay(50, ct);
            }

            var line = Read(output)
                .Split('\n', StringSplitOptions.TrimEntries)
                .SingleOrDefault(each => each.StartsWith(WebUiAddress.ServingMarker, StringComparison.Ordinal));

            Assert.True(line is not null, "the web UI never said it was serving. It said: " + Read(output));
            Assert.Equal(WebUiAddress.ServingMarker + address, line);

            // Said once it was serving, so it accepts a connection now.
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, new Uri(address).Port, ct);
        }
        finally
        {
            End(process);
        }
    }

    [Fact]
    public async Task A_web_ui_run_by_hand_says_nothing_meant_for_a_broker()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = $"http://127.0.0.1:{FreePort()}";
        var output = new StringBuilder();
        using var process = Start(address, watch: false, output);

        try
        {
            Assert.True(await AnswersAsync(address, process, ct), "the web UI never answered, so this proves nothing.");

            // The line comes straight after the bind when it comes at all; watched for a while past that.
            var window = Stopwatch.StartNew();

            while (!Read(output).Contains(WebUiAddress.ServingMarker, StringComparison.Ordinal)
                   && window.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(50, ct);
            }

            Assert.Contains("Parley web UI on", Read(output), StringComparison.Ordinal);
            Assert.DoesNotContain(WebUiAddress.ServingMarker, Read(output), StringComparison.Ordinal);
        }
        finally
        {
            End(process);
        }
    }

    private Process Start(string address, bool watch, StringBuilder output)
    {
        var tool = Path.Combine(
            AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "parley-web-ui.exe" : "parley-web-ui");

        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };

        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add(address);

        if (watch)
        {
            start.ArgumentList.Add("--until-input-closes");
        }

        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("PARLEY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment[ProjectManifest.PathVariable] = _root;

        var process = new Process { StartInfo = start };

        // Standard output only: the line is on it, and errors are not where a broker looks.
        process.OutputDataReceived += (_, line) =>
        {
            lock (output)
            {
                output.Append(line.Data).Append('\n');
            }
        };
        process.ErrorDataReceived += (_, _) => { };

        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static string Read(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static async Task<bool> AnswersAsync(string address, Process process, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < Patience && !process.HasExited)
        {
            try
            {
                using var client = new TcpClient();

                await client.ConnectAsync(IPAddress.Loopback, new Uri(address).Port, ct);

                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100, ct);
            }
        }

        return false;
    }

    private static void End(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(Patience);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
