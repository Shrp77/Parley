using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>That a web UI a broker started stops when the broker's end of its standard input closes, and one run by
/// hand does not.</summary>
/// <remarks>
/// <para>
/// A broker that was killed outright left its web UI running, holding the port with that broker's settings, so the next
/// broker's web UI could not bind. The broker now hands the web UI a pipe as its standard input and passes
/// <c>--until-input-closes</c>; the operating system closes the pipe when the broker dies however it dies. Closing the
/// pipe here is what the broker's death does to it.
/// </para>
/// <para>
/// And a web UI started without the flag reads nothing, so <c>parley web-ui</c> run by hand, whose standard input may be
/// anything, is unchanged.
/// </para>
/// </remarks>
[Integration]
public sealed class TheWebUiEndsWithItsParentTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-parent-" + Path.GetRandomFileName());

    public TheWebUiEndsWithItsParentTests() => Directory.CreateDirectory(_root);

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
    public async Task Closing_its_input_stops_a_web_ui_told_to_watch_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        using var process = Start(port, watch: true);

        try
        {
            Assert.True(await AnswersAsync(port, process, ct), "the web UI never answered, so this proves nothing.");

            process.StandardInput.Close();

            await process.WaitForExitAsync(ct).WaitAsync(Patience, ct);

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            End(process);
        }
    }

    [Fact]
    public async Task A_web_ui_run_without_the_flag_ignores_its_input_closing()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        using var process = Start(port, watch: false);

        try
        {
            Assert.True(await AnswersAsync(port, process, ct), "the web UI never answered.");

            process.StandardInput.Close();

            // Long enough for a watcher to have noticed: the watched case above ends in well under a second.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            Assert.False(process.HasExited, "a web UI run by hand stopped because its standard input closed.");
        }
        finally
        {
            End(process);
        }
    }

    private Process Start(int port, bool watch)
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
        start.ArgumentList.Add($"http://127.0.0.1:{port}");

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

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static async Task<bool> AnswersAsync(int port, Process process, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < Patience && !process.HasExited)
        {
            try
            {
                using var client = new TcpClient();

                await client.ConnectAsync(IPAddress.Loopback, port, ct);

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
