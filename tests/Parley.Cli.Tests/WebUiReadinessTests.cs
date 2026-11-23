using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Where the broker connects to see whether its web UI answers, and the words it uses when it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>A wildcard is not somewhere a connection can be made to.</b> A web UI told to listen on <c>*</c>, <c>+</c>,
/// <c>0.0.0.0</c> or <c>[::]</c> is reached on loopback; without that mapping the probe never connects, and a web UI that
/// is serving is announced as "not answering yet" after the whole deadline. Nothing here binds anything: the mapping is a
/// parse, and a wildcard is never bound by a review probe.
/// </para>
/// <para>
/// <b>An exited web UI's reason depends on whether something else holds its address now.</b> Taken, it is named as
/// another program's; free, by its exit code.
/// </para>
/// </remarks>
[Unit]
public sealed class WebUiReadinessWordsTests
{
    [Theory]
    [InlineData("http://*:5280", "127.0.0.1", 5280)]
    [InlineData("http://+:5281", "127.0.0.1", 5281)]
    [InlineData("http://0.0.0.0:5282", "127.0.0.1", 5282)]
    [InlineData("http://[::]:5283", "::1", 5283)]
    [InlineData("http://127.0.0.1:5284", "127.0.0.1", 5284)]
    [InlineData("http://[::1]:5285", "::1", 5285)]
    [InlineData("http://localhost:5286", "localhost", 5286)]
    [InlineData("http://127.0.0.1", "127.0.0.1", 80)]
    public void A_web_ui_address_is_reached_where_a_connection_can_be_made(string address, string host, int port)
    {
        Assert.Equal((host, port), WebUiReadiness.Probe(address));
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("127.0.0.1:5280")]
    public void An_address_that_names_no_host_and_port_is_not_probed(string address)
    {
        Assert.Null(WebUiReadiness.Probe(address));
    }

    [Fact]
    public void An_exited_web_ui_whose_address_is_held_now_is_named_as_somebody_elses()
    {
        const string address = "http://127.0.0.1:5280";

        Assert.Equal(WebUiReadiness.Taken(address), WebUiReadiness.Exited(3, address, takenNow: true));
        Assert.Contains("already in use by another program", WebUiReadiness.Taken(address), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exited_web_ui_whose_address_is_free_is_named_by_its_exit_code()
    {
        var said = WebUiReadiness.Exited(69, "http://127.0.0.1:5280", takenNow: false);

        Assert.Equal("parley-web-ui exited with code 69", said);
    }
}

/// <summary>
/// What the broker makes of a web UI it started: answered, exited, or silent, decided by a connection on loopback.
/// </summary>
/// <remarks>
/// The child here is a process that cannot listen (this test host, or one that has already exited), so the outcome rests
/// only on the address and on whether the process is running. A process that exited is never waited on for the whole
/// deadline, and is never taken for one that answered.
/// </remarks>
[Integration]
public sealed class WebUiReadinessTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Something_listening_is_taken_and_a_free_port_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();

        var heldAt = $"http://127.0.0.1:{((IPEndPoint)held.LocalEndpoint).Port}";

        Assert.True(await WebUiReadiness.TakenAsync(heldAt, ct));
        Assert.False(await WebUiReadiness.TakenAsync($"http://127.0.0.1:{Loopback.FreePort()}", ct));
    }

    [Fact]
    public async Task A_running_web_ui_whose_address_answers_has_answered()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();

        using var running = Process.GetCurrentProcess();

        var outcome = await WebUiReadiness.AwaitAsync(
            $"http://127.0.0.1:{((IPEndPoint)held.LocalEndpoint).Port}", running, Patience, ct).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Answered, outcome);
    }

    [Fact]
    public async Task A_running_web_ui_that_never_answers_is_silent_at_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();

        var outcome = await WebUiReadiness.AwaitAsync(
            $"http://127.0.0.1:{Loopback.FreePort()}", running, TimeSpan.FromSeconds(1), ct).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Silent, outcome);
    }

    [Fact]
    public async Task A_web_ui_that_exited_is_said_to_have_exited_at_once_not_at_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var exited = await ExitedAsync(ct);

        // A deadline far longer than the patience: an exit noticed only when the deadline ran out fails here.
        var outcome = await WebUiReadiness.AwaitAsync(
            $"http://127.0.0.1:{Loopback.FreePort()}", exited, TimeSpan.FromMinutes(10), ct).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Exited, outcome);
    }

    [Fact]
    public async Task A_web_ui_that_exited_is_said_to_have_exited_when_the_broker_is_already_stopping()
    {
        var ct = TestContext.Current.CancellationToken;
        using var exited = await ExitedAsync(ct);
        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        var outcome = await WebUiReadiness.AwaitAsync(
            $"http://127.0.0.1:{Loopback.FreePort()}", exited, Patience, stopping.Token).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Exited, outcome);
    }

    private static async Task<Process> ExitedAsync(CancellationToken ct)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 3")
            : new ProcessStartInfo("/bin/sh", ["-c", "exit 3"]);

        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        var process = Process.Start(start) ?? throw new InvalidOperationException("The stand-in did not start.");

        await process.WaitForExitAsync(ct).WaitAsync(Patience, ct);

        return process;
    }
}
