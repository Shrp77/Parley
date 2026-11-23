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

            // A taken port is not a missing tool: telling the person to install parley-web-ui sends them the wrong way.
            Assert.DoesNotContain("-Only parley-web-ui", output, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SIGTERM_stops_serve_in_order_and_it_exits_zero(bool withWebUi)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM is how Linux and macOS supervisors stop a service.");

        var ct = TestContext.Current.CancellationToken;
        using var broker = Serve(withWebUi, out var said);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            if (withWebUi)
            {
                Assert.True(await AcceptsAsync(_webUiAt, ct), "the web UI never answered.");
            }

            using (var kill = Process.Start("/bin/sh", ["-c", $"kill -TERM {broker.Id}"]))
            {
                await kill.WaitForExitAsync(ct);
            }

            var exited = broker.WaitForExit(TimeSpan.FromSeconds(60));

            Assert.True(exited, "serve was still running a minute after SIGTERM. It said: " + Read(said));
            Assert.Equal(0, broker.ExitCode);

            if (withWebUi)
            {
                Assert.True(await FreedAsync(_webUiAt, ct), "the web UI outlived a broker stopped by SIGTERM.");
            }
        }
        finally
        {
            End(broker);
        }
    }

    private Process Serve(bool withWebUi, out StringBuilder said)
    {
        string[] arguments = withWebUi
            ? ["serve", "--config", _root, "--with-web-ui", "--web-ui", _webUiAt]
            : ["serve", "--config", _root];

        var process = ToolProcess.Launch([("PATH", ChildLifetimeTests.WebUiOnPath())], arguments);
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
