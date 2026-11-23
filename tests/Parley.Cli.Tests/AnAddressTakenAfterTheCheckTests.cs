using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a web UI is announced only when it says it is serving, never because something answers at its address.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> The broker asks whether the web UI's address is taken, starts the web UI, and used to wait for a
/// connection to the address to be accepted. A program that took the address in between accepted that connection, and
/// the broker told the person to open it as this broker's web UI while the web UI failed to bind and exited (review,
/// lens 1 run B on 6561e9ea: two brokers on one port both announced, and a stand-in that bound late was announced three
/// times in three while the web UI logged AddressAlreadyInUse).
/// </para>
/// <para>
/// <b>The address is taken by this test, at the moment that matters.</b> The web UI writes its first line before it
/// builds its host and binds; the listener here is started inside the handler that reads that line, so it is up before
/// the web UI reaches its bind, and after the broker's check, which runs before the web UI is started at all.
/// </para>
/// </remarks>
[Integration]
public sealed class AnAddressTakenAfterTheCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"parley-taken-late-{Guid.NewGuid():N}");
    private readonly string _brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";
    private readonly int _webUiPort = Loopback.FreePort();

    public AnAddressTakenAfterTheCheckTests()
    {
        Directory.CreateDirectory(_root);

        var probe = ProjectName.Create("probe");

        ProjectManifest.Write(_root, [new ProjectSettings(probe, ProjectFolder.StoreFor(_root, probe), _brokerAt, null)]);
    }

    private string WebUiAt => $"http://127.0.0.1:{_webUiPort}";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; it is in a temporary directory.
        }
    }

    [Fact]
    public async Task A_web_ui_whose_address_another_program_takes_before_it_binds_is_not_announced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = new TcpListener(IPAddress.Loopback, _webUiPort);
        var said = new StringBuilder();
        var tookIt = false;

        // The real web UI, which says where it will listen before it builds its host.
        using var broker = ToolProcess.Launch(
            [("PATH", ChildLifetimeTests.WebUiOnPath())],
            "serve", "--config", _root, "--with-web-ui", "--web-ui", WebUiAt);

        void Keep(object sender, DataReceivedEventArgs line)
        {
            lock (said)
            {
                if (!tookIt && line.Data?.Contains("Parley web UI on", StringComparison.Ordinal) == true)
                {
                    held.Start();
                    tookIt = true;
                }

                said.AppendLine(line.Data);
            }
        }

        broker.OutputDataReceived += Keep;
        broker.ErrorDataReceived += Keep;
        broker.BeginOutputReadLine();
        broker.BeginErrorReadLine();

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var output = await BannerAsync(said, ct);
            bool taken;

            lock (said)
            {
                taken = tookIt;
            }

            Assert.True(taken, "the web UI never said where it would listen, so the address was never taken: " + output);
            Assert.Contains("Web UI:   did not start", output, StringComparison.Ordinal);
            Assert.Contains("already in use by another program", output, StringComparison.Ordinal);
            Assert.Contains("this broker's web UI is not running", output, StringComparison.Ordinal);
            Assert.Contains("The broker is still serving.", output, StringComparison.Ordinal);
            Assert.Contains("Stop that program, or give the web UI another address: for the logon task, `parley autostart install --web-ui <address>`; by hand, `parley serve --with-web-ui --web-ui <address>`.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Open that one in a browser", output, StringComparison.Ordinal);
            Assert.False(broker.HasExited, "the broker exited because its web UI could not bind.");
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task A_stand_in_that_binds_late_while_the_address_is_taken_is_not_announced()
    {
        // The reviewer's probe as it was run: a web UI that waits before it binds, and an address taken meanwhile.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in is a shell script.");

        var ct = TestContext.Current.CancellationToken;
        var standIn = Path.Combine(_root, "stand-in");

        Directory.CreateDirectory(standIn);

        // It says it is starting, waits a second and a half, and fails to bind as the real one does: with exit code 69.
        WriteScript(
            Path.Combine(standIn, "parley-web-ui"),
            "echo 'Parley web UI on its address. Ctrl+C stops it.'",
            "sleep 1.5",
            "echo 'The web UI did not start: Address already in use' >&2",
            "exit 69");

        using var held = new TcpListener(IPAddress.Loopback, _webUiPort);
        var said = new StringBuilder();

        using var broker = ToolProcess.Launch(
            [("PATH", standIn + Path.PathSeparator + "/usr/bin:/bin")],
            "serve", "--config", _root, "--with-web-ui", "--web-ui", WebUiAt);

        void Keep(object sender, DataReceivedEventArgs line)
        {
            lock (said)
            {
                if (line.Data?.Contains("Parley web UI on", StringComparison.Ordinal) == true)
                {
                    held.Start();
                }

                said.AppendLine(line.Data);
            }
        }

        broker.OutputDataReceived += Keep;
        broker.ErrorDataReceived += Keep;
        broker.BeginOutputReadLine();
        broker.BeginErrorReadLine();

        try
        {
            var output = await BannerAsync(said, ct);

            Assert.Contains("Web UI:   did not start", output, StringComparison.Ordinal);
            Assert.Contains("already in use by another program", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Open that one in a browser", output, StringComparison.Ordinal);
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task A_web_ui_is_announced_on_the_line_it_writes_when_it_is_serving()
    {
        // A stand-in that binds nothing at all and says it is serving: announced, because what it says is the answer.
        // Paired with the test above, where something answers at the address and nothing is announced.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in is a shell script.");

        var ct = TestContext.Current.CancellationToken;
        var standIn = Path.Combine(_root, "stand-in");

        Directory.CreateDirectory(standIn);

        // Reads its standard input until the broker closes it, as the real one does with --until-input-closes.
        WriteScript(
            Path.Combine(standIn, "parley-web-ui"),
            "echo 'a line of its own, passed on'",
            $"echo '{WebUiAddress.ServingMarker}{WebUiAt}'",
            "exec cat > /dev/null");

        var said = new StringBuilder();

        using var broker = ToolProcess.Launch(
            [("PATH", standIn + Path.PathSeparator + "/usr/bin:/bin")],
            "serve", "--config", _root, "--with-web-ui", "--web-ui", WebUiAt);

        void Keep(object sender, DataReceivedEventArgs line)
        {
            lock (said)
            {
                said.AppendLine(line.Data);
            }
        }

        broker.OutputDataReceived += Keep;
        broker.ErrorDataReceived += Keep;
        broker.BeginOutputReadLine();
        broker.BeginErrorReadLine();

        try
        {
            var output = await BannerAsync(said, ct);

            Assert.True(Loopback.CanBind(WebUiAt), "something listens at the address, so this proves nothing.");
            Assert.Contains($"Web UI:   {WebUiAt}", output, StringComparison.Ordinal);
            Assert.Contains("Open that one in a browser.", output, StringComparison.Ordinal);

            // Its other output reaches the broker's, and the line it is announced on does not.
            Assert.Contains("a line of its own, passed on", output, StringComparison.Ordinal);
            Assert.DoesNotContain(WebUiAddress.ServingMarker, output, StringComparison.Ordinal);
        }
        finally
        {
            End(broker);
        }
    }

    // A stand-in web UI. Serve asks the tool for its version as it starts, so that is answered and nothing else is.
    private static void WriteScript(string path, params string[] lines)
    {
        File.WriteAllLines(path, ["#!/bin/sh", "[ \"$1\" = --version ] && exit 0", .. lines]);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task<string> BannerAsync(StringBuilder said, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();

        // The closing rule, so the whole block is read: the reason is on the line after the address.
        while (!Read(said).Contains("no browser needed", StringComparison.Ordinal) && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(100, ct);
        }

        var output = Read(said);

        Assert.True(output.Contains("Web UI:", StringComparison.Ordinal), "no Web UI line within 60s. It said: " + output);

        return output;
    }

    private static string Read(StringBuilder said)
    {
        lock (said)
        {
            return said.ToString();
        }
    }

    private static void End(Process broker)
    {
        if (!broker.HasExited)
        {
            broker.Kill(entireProcessTree: true);
            broker.WaitForExit(10_000);
        }
    }
}
