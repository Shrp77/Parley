using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// How a broker ends, and what it says about its web UI: SIGTERM is an orderly stop, a hard kill takes the web UI with
/// it, and a web UI that could not bind is never announced as serving.
/// </summary>
/// <remarks>
/// <para>
/// <b>SIGTERM never ended <c>parley serve</c>.</b> Each project's web host took the signal and stopped itself, and
/// nothing else moved: the process stayed up with its node retrying the broker it had just stopped, until the
/// supervisor (systemd, launchd) killed it outright, and the web UI was orphaned.
/// </para>
/// <para>
/// <b>A broker killed outright left its web UI running,</b> on the old settings and the port the next broker's web UI
/// wanted; and that broker then announced its own web UI as serving, because it announced once the process started.
/// </para>
/// <para>
/// <see cref="ChildLifetimeTests"/> ends the broker with its whole process tree, which ends the web UI by name; these
/// end the broker alone, the way a supervisor or Task Manager does.
/// </para>
/// </remarks>
[Integration]
public sealed class AStoppedBrokerLeavesNoWebUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"parley-stopped-{Guid.NewGuid():N}");
    private readonly string _brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";
    private readonly string _webUiAt = $"http://127.0.0.1:{Loopback.FreePort()}";

    public AStoppedBrokerLeavesNoWebUiTests()
    {
        Directory.CreateDirectory(_root);

        var probe = ProjectName.Create("probe");

        ProjectManifest.Write(_root, [new ProjectSettings(probe, ProjectFolder.StoreFor(_root, probe), _brokerAt, null)]);
    }

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
    public async Task Killing_the_broker_alone_ends_the_web_ui_it_started()
    {
        var ct = TestContext.Current.CancellationToken;
        using var broker = Serve(withWebUi: true, out var said);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");
            Assert.True(await AcceptsAsync(_webUiAt, ct), "the web UI never answered, so this proves nothing.");

            // The broker alone: no code of its own runs, as under SIGKILL or Stop-Process -Force.
            broker.Kill(entireProcessTree: false);
            await broker.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);

            Assert.True(
                await FreedAsync(_webUiAt, ct),
                "the web UI is still listening after its broker was killed, holding the port the next broker's web UI "
                + "wants, with this broker's settings. It said: " + Read(said));
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task A_web_ui_whose_port_is_taken_is_reported_as_not_started_and_the_broker_serves()
    {
        var ct = TestContext.Current.CancellationToken;

        using var held = new TcpListener(IPAddress.Loopback, new Uri(_webUiAt).Port);
        held.Start();

        using var broker = Serve(withWebUi: true, out var said);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var deadline = Stopwatch.StartNew();

            while (!Read(said).Contains("Web UI:", StringComparison.Ordinal) && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100, ct);
            }

            var output = Read(said);

            Assert.Contains("Web UI:   did not start", output, StringComparison.Ordinal);
            Assert.Contains("already in use by another program", output, StringComparison.Ordinal);
            Assert.Contains("The broker is still serving.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Open that one in a browser", output, StringComparison.Ordinal);

            // The tool is installed; telling the person to install it sends them the wrong way.
            Assert.DoesNotContain("install.ps1", output, StringComparison.Ordinal);
            Assert.False(broker.HasExited, "the broker exited because a web page could not bind.");
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task A_web_ui_that_answers_is_announced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var broker = Serve(withWebUi: true, out var said);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var deadline = Stopwatch.StartNew();

            while (!Read(said).Contains("Web UI:", StringComparison.Ordinal) && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100, ct);
            }

            var output = Read(said);

            Assert.Contains($"Web UI:   {_webUiAt}", output, StringComparison.Ordinal);
            Assert.Contains("Open that one in a browser.", output, StringComparison.Ordinal);

            // Announced only once it answered, so it answers now.
            Assert.True(await AcceptsAsync(_webUiAt, ct), "the broker announced a web UI that does not answer.");
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task The_web_ui_answers_the_moment_it_is_announced()
    {
        // The broker waits for its web UI to answer before naming it. Without the wait it named the address the moment the
        // process started, and every test still passed, because the one above retries for half a minute (review, lens 3 run
        // B on 6561e9ea).
        var ct = TestContext.Current.CancellationToken;
        using var broker = Serve(withWebUi: true, out var said);

        try
        {
            // Watched closely, so the one connection below is made within a few milliseconds of the announcement.
            var deadline = Stopwatch.StartNew();

            while (!Read(said).Contains("Open that one in a browser.", StringComparison.Ordinal)
                   && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(5, ct);
            }

            Assert.Contains($"Web UI:   {_webUiAt}", Read(said), StringComparison.Ordinal);

            // Once, briefly, with no retry: a web UI takes far longer than this to bind, so an address named before it
            // answered is caught. Bounded by a token because Windows retries a refused loopback connection for about two
            // seconds before it gives up.
            using var client = new TcpClient();
            using var brief = CancellationTokenSource.CreateLinkedTokenSource(ct);
            brief.CancelAfter(TimeSpan.FromMilliseconds(250));
            var answered = true;

            try
            {
                await client.ConnectAsync(IPAddress.Loopback, new Uri(_webUiAt).Port, brief.Token);
            }
            catch (Exception refused) when (refused is SocketException || (refused is OperationCanceledException && !ct.IsCancellationRequested))
            {
                answered = false;
            }

            Assert.True(answered, "the broker announced its web UI before it answered. It said: " + Read(said));
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task A_web_ui_that_is_not_installed_is_named_with_how_to_install_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var nowhere = Path.Combine(_root, "no-tools");

        Directory.CreateDirectory(nowhere);

        using var broker = Serve(withWebUi: true, out var said, path: nowhere);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var output = await BannerAsync(said, TimeSpan.FromSeconds(60), ct);

            Assert.Contains("Web UI:   did not start", output, StringComparison.Ordinal);
            Assert.Contains("The broker is still serving.", output, StringComparison.Ordinal);
            Assert.Contains("install.ps1 -Only parley-web-ui", output, StringComparison.Ordinal);
            Assert.Contains("./install.sh --only parley-web-ui", output, StringComparison.Ordinal);
        }
        finally
        {
            End(broker);
        }
    }

    [Fact]
    public async Task A_web_ui_that_exits_before_it_answers_is_named_as_not_started_at_once()
    {
        var ct = TestContext.Current.CancellationToken;

        // An address the broker passes on and the web UI refuses at its bind (Kestrel takes no path), so the child starts,
        // exits, and its port was never taken by anything: only the wait on the child can tell.
        var refused = _webUiAt + "/zz";

        using var broker = Serve(withWebUi: true, out var said, webUiAt: refused);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            // Well inside the readiness deadline: an exit noticed only when the deadline ran out is the person told late.
            var output = await BannerAsync(said, TimeSpan.FromSeconds(20), ct);

            Assert.Contains("Web UI:   did not start (parley-web-ui exited with code", output, StringComparison.Ordinal);
            Assert.Contains("The broker is still serving.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Open that one in a browser", output, StringComparison.Ordinal);
            Assert.DoesNotContain("install.ps1", output, StringComparison.Ordinal);
        }
        finally
        {
            End(broker);
        }
    }

    [Theory]
    [InlineData("TERM", false)]
    [InlineData("TERM", true)]
    [InlineData("HUP", false)]
    [InlineData("HUP", true)]
    public async Task A_signal_to_stop_stops_serve_in_order_and_it_exits_zero(string signal, bool withWebUi)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM and SIGHUP are how Linux and macOS stop a service or a terminal's job.");

        var ct = TestContext.Current.CancellationToken;
        using var broker = Serve(withWebUi, out var said);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            if (withWebUi)
            {
                Assert.True(await AcceptsAsync(_webUiAt, ct), "the web UI never answered.");
            }

            await SignalAsync(signal, broker, ct);

            var exited = broker.WaitForExit(TimeSpan.FromSeconds(60));

            Assert.True(exited, $"serve was still running a minute after SIG{signal}. It said: " + Read(said));
            Assert.Equal(0, broker.ExitCode);

            if (withWebUi)
            {
                Assert.True(await FreedAsync(_webUiAt, ct), $"the web UI outlived a broker stopped by SIG{signal}.");
            }
        }
        finally
        {
            End(broker);
        }
    }

    // SIGHUP only. A second SIGTERM in this window was seen to be swallowed (serve ran its stop to the end and exited
    // zero): reported by review run r10-surf-a for the owner, not held here.
    [Theory]
    [InlineData("HUP")]
    public async Task A_second_signal_while_stopping_ends_serve_at_once(string signal)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM and SIGHUP are Linux and macOS signals.");

        var ct = TestContext.Current.CancellationToken;
        var standIn = Path.Combine(_root, "stand-in");
        var pidFile = Path.Combine(_root, "stand-in.pid");

        Directory.CreateDirectory(standIn);

        // A web UI that never reads its pipe, so an orderly stop waits out its grace before ending it: a stop that has
        // wedged for five seconds, which is the window the second signal must land in. A stop with nothing to wait for
        // is over in well under a tenth of a second, and two signals sent together arrive as one.
        WriteScript(Path.Combine(standIn, "parley-web-ui"), $"echo $$ > '{pidFile}'", "exec sleep 300");

        using var broker = Serve(withWebUi: true, out var said, path: standIn + Path.PathSeparator + "/usr/bin:/bin");

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var started = Stopwatch.StartNew();

            while (!File.Exists(pidFile) && started.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(100, ct);
            }

            Assert.True(File.Exists(pidFile), "the stand-in web UI never started, so serve has nothing to wait for.");

            await SignalAsync(signal, broker, ct);

            // The broker stops answering before it waits on the web UI, so this is serve inside its wedged stop.
            var stopping = Stopwatch.StartNew();

            while (Loopback.CanBind(_brokerAt) is false && stopping.Elapsed < TimeSpan.FromSeconds(4) && !broker.HasExited)
            {
                await Task.Delay(50, ct);
            }

            await SignalAsync(signal, broker, ct);

            Assert.True(broker.WaitForExit(TimeSpan.FromSeconds(60)), $"serve outlived two SIG{signal}s. It said: " + Read(said));

            // Ended by the second signal, not by the orderly stop running to its end, which exits zero.
            Assert.NotEqual(0, broker.ExitCode);
        }
        finally
        {
            End(broker);

            if (File.Exists(pidFile)
                && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)
                && Alive(pid))
            {
                using var left = Process.GetProcessById(pid);
                left.Kill();
            }
        }
    }

    [Fact]
    public async Task A_web_ui_that_ignores_its_input_closing_is_ended_when_serve_stops()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM is the orderly stop a test can send; on Windows it is Ctrl+C.");

        var ct = TestContext.Current.CancellationToken;
        var standIn = Path.Combine(_root, "stand-in");
        var pidFile = Path.Combine(_root, "stand-in.pid");

        Directory.CreateDirectory(standIn);

        // Starts, never listens, never reads its input: a web UI wedged before it could watch the pipe.
        var tool = Path.Combine(standIn, "parley-web-ui");

        WriteScript(tool, $"echo $$ > '{pidFile}'", "exec sleep 300");

        using var broker = Serve(withWebUi: true, out var said, path: standIn + Path.PathSeparator + "/usr/bin:/bin");
        int? standInPid = null;

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var deadline = Stopwatch.StartNew();

            while (deadline.Elapsed < TimeSpan.FromSeconds(30)
                   && !(File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out _)))
            {
                await Task.Delay(100, ct);
            }

            Assert.True(File.Exists(pidFile), "the stand-in web UI never started, so this proves nothing.");

            standInPid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

            await SignalAsync("TERM", broker, ct);

            Assert.True(broker.WaitForExit(TimeSpan.FromSeconds(60)), "serve was still running a minute after SIGTERM.");

            var gone = Stopwatch.StartNew();

            while (Alive(standInPid.Value) && gone.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100, ct);
            }

            Assert.False(
                Alive(standInPid.Value),
                "a web UI that did not stop when its pipe closed outlived the broker that started it. It said: " + Read(said));
        }
        finally
        {
            End(broker);

            if (standInPid is { } pid && Alive(pid))
            {
                using var left = Process.GetProcessById(pid);
                left.Kill();
            }
        }
    }

    [Fact]
    [Slow]
    public async Task A_web_ui_that_runs_and_never_answers_is_announced_as_not_answering_yet()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in is a shell script.");

        var ct = TestContext.Current.CancellationToken;
        var standIn = Path.Combine(_root, "stand-in");

        Directory.CreateDirectory(standIn);

        var tool = Path.Combine(standIn, "parley-web-ui");
        var pidFile = Path.Combine(_root, "stand-in.pid");

        // Says it is serving ten seconds after the thirty the broker waits, which the broker then says.
        WriteScript(
            tool,
            $"echo $$ > '{pidFile}'",
            "sleep 40",
            $"echo '{WebUiAddress.ServingMarker}{_webUiAt}'",
            "exec sleep 300");

        using var broker = Serve(withWebUi: true, out var said, path: standIn + Path.PathSeparator + "/usr/bin:/bin");

        try
        {
            // The readiness deadline is thirty seconds; given twice that.
            var output = await BannerAsync(said, TimeSpan.FromSeconds(60), ct);

            Assert.Contains($"Web UI:   {_webUiAt}", output, StringComparison.Ordinal);
            Assert.Contains("Started, but not answering yet.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Open that one in a browser.", output, StringComparison.Ordinal);

            // Followed up once it says so, rather than left at "not answering yet" for good.
            var followed = Stopwatch.StartNew();
            var answering = $"Web UI:   {_webUiAt} is answering now. Open that one in a browser.";

            while (!Read(said).Contains(answering, StringComparison.Ordinal) && followed.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100, ct);
            }

            Assert.Contains(answering, Read(said), StringComparison.Ordinal);
        }
        finally
        {
            End(broker);

            // Best effort: the stand-in reads no pipe, so nothing else ends it if the broker did not.
            if (File.Exists(pidFile)
                && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)
                && Alive(pid))
            {
                using var left = Process.GetProcessById(pid);
                left.Kill();
            }
        }
    }

    // A stand-in web UI. Serve asks the tool for its version as it starts, so that is answered and nothing else is: the
    // lines given are what the started web UI does.
    private static void WriteScript(string path, params string[] lines)
    {
        File.WriteAllLines(path, ["#!/bin/sh", "[ \"$1\" = --version ] && exit 0", .. lines]);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task SignalAsync(string signal, Process broker, CancellationToken ct)
    {
        using var kill = Process.Start("/bin/sh", ["-c", $"kill -{signal} {broker.Id}"]);

        await kill.WaitForExitAsync(ct);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string> BannerAsync(StringBuilder said, TimeSpan patience, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();

        while (!Read(said).Contains("Web UI:", StringComparison.Ordinal) && deadline.Elapsed < patience)
        {
            await Task.Delay(100, ct);
        }

        var output = Read(said);

        Assert.True(
            output.Contains("Web UI:", StringComparison.Ordinal),
            $"no Web UI line within {patience.TotalSeconds:0}s. It said: {output}");

        return output;
    }

    private Process Serve(bool withWebUi, out StringBuilder said, string? path = null, string? webUiAt = null)
    {
        string[] arguments = withWebUi
            ? ["serve", "--config", _root, "--with-web-ui", "--web-ui", webUiAt ?? _webUiAt]
            : ["serve", "--config", _root];

        var process = ToolProcess.Launch([("PATH", path ?? ChildLifetimeTests.WebUiOnPath())], arguments);
        var output = new StringBuilder();

        void Keep(object sender, DataReceivedEventArgs line)
        {
            lock (output)
            {
                output.AppendLine(line.Data);
            }
        }

        process.OutputDataReceived += Keep;
        process.ErrorDataReceived += Keep;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        said = output;

        return process;
    }

    private static string Read(StringBuilder said)
    {
        lock (said)
        {
            return said.ToString();
        }
    }

    private static async Task<bool> AcceptsAsync(string address, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 300; attempt++)
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

    private static async Task<bool> FreedAsync(string address, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (Loopback.CanBind(address))
            {
                return true;
            }

            await Task.Delay(250, ct);
        }

        return false;
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
