using System.Diagnostics;
using System.Text;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>That a web UI which exits before it answers is reported as not started, never announced.</summary>
/// <remarks>
/// The broker used to announce its web UI as soon as the process started. A stand-in that exits at once (an apphost
/// copied away from its assembly) is the one case the other tests leave open: the address is free, so the check made
/// before starting passes, and only waiting for the child to answer can tell (review, lens 3 run C on 6561e9ea).
/// </remarks>
[Integration]
public sealed class AWebUiThatExitsBeforeItAnswersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"parley-readiness-{Guid.NewGuid():N}");
    private readonly string _standIn;
    private readonly string _brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";
    private readonly string _webUiAt = $"http://127.0.0.1:{Loopback.FreePort()}";

    public AWebUiThatExitsBeforeItAnswersTests()
    {
        Directory.CreateDirectory(_root);
        _standIn = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_standIn);

        // The broker's own apphost under the web UI's name, with no assembly beside it: it starts and exits at once.
        var apphost = OperatingSystem.IsWindows() ? "parley.exe" : "parley";
        var copy = Path.Combine(_standIn, OperatingSystem.IsWindows() ? "parley-web-ui.exe" : "parley-web-ui");

        File.Copy(Path.Combine(AppContext.BaseDirectory, apphost), copy);

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
    public async Task A_web_ui_that_exits_before_it_answers_is_reported_as_not_started()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _standIn + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var broker = ToolProcess.Launch(
            [("PATH", path)], "serve", "--config", _root, "--with-web-ui", "--web-ui", _webUiAt);
        var said = new StringBuilder();

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

        string Read()
        {
            lock (said)
            {
                return said.ToString();
            }
        }

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            var deadline = Stopwatch.StartNew();

            while (!Read().Contains("Web UI:", StringComparison.Ordinal) && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100, ct);
            }

            var output = Read();

            Assert.Contains("Web UI:   did not start (parley-web-ui exited with code", output, StringComparison.Ordinal);
            Assert.Contains("The broker is still serving.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Open that one in a browser", output, StringComparison.Ordinal);
            Assert.False(broker.HasExited, "the broker exited because its web UI did.");
        }
        finally
        {
            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: true);
                broker.WaitForExit(10_000);
            }
        }
    }
}
