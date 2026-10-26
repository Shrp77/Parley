using System.Runtime.InteropServices;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// That what a broker logs reaches <c>serve --log</c>, and that <c>--heartbeat</c> adds a line a second.
/// </summary>
/// <remarks>
/// A broker logged to the console, and a broker started hidden at logon has none, so what it logged went nowhere.
/// </remarks>
public sealed class ServeLogsWhatTheBrokerSaysTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-serve-log-" + Guid.NewGuid().ToString("N"));

    public ServeLogsWhatTheBrokerSaysTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> ServeAsync(bool heartbeat, TimeSpan forAbout)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var orders = new ProjectSettings(name, ProjectFolder.StoreFor(_root, name), $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);

        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var log = Path.Combine(_root, "broker.log");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            initial, log, new StringWriter(), new StringWriter(), heartbeat: heartbeat, cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "never came up");
            await Task.Delay(forAbout, ct);
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        return await File.ReadAllTextAsync(log, ct);
    }

    [Fact]
    public async Task The_brokers_own_lines_reach_the_log_and_a_heartbeat_only_when_asked()
    {
        var plain = await ServeAsync(heartbeat: false, TimeSpan.FromMilliseconds(1500));

        Assert.Contains("orders Parley.Server: listening on http://127.0.0.1:", plain, StringComparison.Ordinal);
        Assert.Contains("orders Parley.Server: stopped", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Parley.Server.Heartbeat", plain, StringComparison.Ordinal);

        File.Delete(Path.Combine(_root, "broker.log"));

        var beating = await ServeAsync(heartbeat: true, TimeSpan.FromMilliseconds(2500));

        Assert.Contains("orders Parley.Server.Heartbeat: heartbeat 1: up ", beating, StringComparison.Ordinal);
        Assert.Contains("orders Parley.Server.Connections: connection ", beating, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PosixSignal.SIGINT, "Ctrl+C")]
    [InlineData(PosixSignal.SIGHUP, "its console window being closed")]
    [InlineData(PosixSignal.SIGTERM, "logoff or shutdown")]
    public void A_signal_is_named_as_a_person_on_Windows_would_know_it(PosixSignal signal, string onWindows)
    {
        Assert.Equal(OperatingSystem.IsWindows() ? onWindows : signal.ToString(), ProcessWatch.Describe(signal));
    }
}
