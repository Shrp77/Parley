using System.Diagnostics;
using System.Text;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley up</c> stopped as a supervisor or a terminal stops it ends in order and exits 0, as <c>serve</c> does.
/// </summary>
/// <remarks>
/// The node's stream, cancelled by the stop, ends as an <c>RpcException</c> carrying <c>Cancelled</c> rather than as an
/// <c>OperationCanceledException</c>; nothing caught it, so it reached the command line's last handler, which said
/// "The broker refused that: Call canceled by the client." and exited 74 (lens 1 run A on 6561e9ea).
/// </remarks>
[Integration]
public sealed class UpStoppedBySignalExitsZeroTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"parley-upstop-{Guid.NewGuid():N}");
    private readonly string _brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";

    public UpStoppedBySignalExitsZeroTests()
    {
        Directory.CreateDirectory(_root);
        var probe = ProjectName.Create("probe");
        ProjectManifest.Write(_root, [new ProjectSettings(probe, ProjectFolder.StoreFor(_root, probe), _brokerAt, null)]);
        File.WriteAllText(Path.Combine(_root, "node.json"), """{ "agents": {} }""");
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

    [Theory]
    [InlineData("TERM", false)]
    [InlineData("TERM", true)]
    [InlineData("HUP", false)]
    [InlineData("INT", true)]
    public async Task Up_stopped_by_a_signal_exits_zero(string signal, bool keepTrying)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Signals are how Linux and macOS supervisors and terminals stop a node.");

        var ct = TestContext.Current.CancellationToken;
        using var broker = Launch(["serve", "--config", _root], out var brokerSaid);

        try
        {
            Assert.True(await Loopback.AnswersAsync(_brokerAt, ct), "the broker never answered.");

            string[] up = ["up", "--config", _root, "--project", "probe", "--node-config", Path.Combine(_root, "node.json")];
            using var node = Launch(keepTrying ? [.. up, "--keep-trying"] : up, out var said);

            try
            {
                var deadline = Stopwatch.StartNew();

                while (!Read(said).Contains("joined", StringComparison.Ordinal) && deadline.Elapsed < TimeSpan.FromSeconds(60))
                {
                    await Task.Delay(100, ct);
                }

                Assert.Contains("joined", Read(said), StringComparison.Ordinal);

                using (var kill = Process.Start("/bin/sh", ["-c", $"kill -{signal} {node.Id}"]))
                {
                    await kill.WaitForExitAsync(ct);
                }

                Assert.True(node.WaitForExit(TimeSpan.FromSeconds(60)), "up was still running a minute later: " + Read(said));
                node.WaitForExit();

                Assert.DoesNotContain("refused", Read(said), StringComparison.Ordinal);
                Assert.Equal(0, node.ExitCode);
            }
            finally
            {
                End(node);
            }
        }
        finally
        {
            End(broker);
        }
    }

    private static Process Launch(string[] arguments, out StringBuilder said)
    {
        var process = ToolProcess.Launch([], arguments);
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

    private static void End(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
    }
}
