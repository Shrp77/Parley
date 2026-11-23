using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Where the broker connects to see whether a web UI's address is taken, how it reads what its web UI says, and the
/// words it uses when the web UI did not start.
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

        // Said plainly: the console is not running. It used to guess at "a web UI an earlier broker left running", which
        // is wrong whenever the holder is anything else, and since a web UI ends with its broker it rarely is.
        Assert.Contains("this broker's web UI is not running", WebUiReadiness.Taken(address), StringComparison.Ordinal);
        Assert.DoesNotContain("earlier broker", WebUiReadiness.Taken(address), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_serving_line_is_taken_once_and_everything_else_is_passed_on()
    {
        await using var log = new StringWriter();
        var said = new WebUiReadiness.Said(log);

        said.Line("Parley web UI on http://127.0.0.1:5280. Ctrl+C stops it.");
        Assert.False(said.Serving.IsCompleted, "a line that is not the serving line was taken for it.");

        said.Line(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5280");
        said.Line(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5281");
        said.Line("info: Microsoft.Hosting.Lifetime");
        said.Line(null);

        Assert.True(said.Serving.IsCompletedSuccessfully);
        Assert.Equal("http://127.0.0.1:5280", await said.Serving);

        var passed = log.ToString();

        Assert.Contains("Parley web UI on http://127.0.0.1:5280. Ctrl+C stops it.", passed, StringComparison.Ordinal);
        Assert.Contains("info: Microsoft.Hosting.Lifetime", passed, StringComparison.Ordinal);
        Assert.DoesNotContain("http://127.0.0.1:5280" + Environment.NewLine + "info", passed, StringComparison.Ordinal);

        // Only the first is the answer; a second is passed on like any other line.
        Assert.Contains(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5281", passed, StringComparison.Ordinal);
        Assert.DoesNotContain(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5280", passed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_the_broker_can_no_longer_write_is_dropped_rather_than_thrown()
    {
        // The line arrives on the process reader's thread, where an exception would end the broker.
        var log = new StringWriter();
        log.Dispose();

        var said = new WebUiReadiness.Said(log);

        said.Line("a line written after the broker's output closed");
    }

    [Fact]
    public void A_line_the_broker_cannot_write_after_a_hangup_is_dropped_rather_than_thrown()
    {
        // After a terminal hangs up every write to it fails with an I/O error, not a disposed writer. Thrown on the
        // process reader's thread, it would end the broker the way the node's "stopped." line once ended `parley up`.
        var said = new WebUiReadiness.Said(new HungUp());

        said.Line("a line written after the terminal hung up");
    }

    private sealed class HungUp : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("Input/output error");

        public override void WriteLine(string? value) => throw new IOException("Input/output error");

        public override Task WriteLineAsync(string? value) => throw new IOException("Input/output error");
    }

    [Fact]
    public void An_exited_web_ui_whose_address_is_free_is_named_by_its_exit_code()
    {
        var said = WebUiReadiness.Exited(69, "http://127.0.0.1:5280", takenNow: false);

        Assert.Equal("parley-web-ui exited with code 69", said);
    }
}

/// <summary>
/// What the broker makes of a web UI it started: answered, exited, silent or not worth saying, decided by what the web UI
/// said and whether it is running, never by whether its address answers.
/// </summary>
/// <remarks>
/// The child here is a process that cannot listen (this test host, or one that has already exited), and what it says is
/// handed to <see cref="WebUiReadiness.Said"/> directly. A process that exited is never waited on for the whole deadline,
/// and is never taken for one that answered; another program listening at the address is never taken for it either
/// (review, lens 1 run B on 6561e9ea).
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
    public async Task A_running_web_ui_that_says_it_is_serving_has_answered()
    {
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();
        var said = new WebUiReadiness.Said(TextWriter.Null);

        var waiting = WebUiReadiness.AwaitAsync(said, running, Patience, ct);

        said.Line(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5280");

        Assert.Equal(WebUiReadiness.Outcome.Answered, await waiting.WaitAsync(Patience, ct));
    }

    [Fact]
    public async Task Another_program_listening_at_the_address_is_not_the_web_ui_answering()
    {
        // The defect this replaced: a program that took the address after the broker's check accepted the connection
        // the broker took for its web UI answering, and was announced while the web UI failed to bind.
        var ct = TestContext.Current.CancellationToken;
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();

        Assert.True(
            await WebUiReadiness.TakenAsync($"http://127.0.0.1:{((IPEndPoint)held.LocalEndpoint).Port}", ct),
            "nothing listens at the address, so this proves nothing.");

        using var running = Process.GetCurrentProcess();

        var outcome = await WebUiReadiness.AwaitAsync(
            new WebUiReadiness.Said(TextWriter.Null), running, TimeSpan.FromSeconds(1), ct).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Silent, outcome);
    }

    [Fact]
    public async Task A_running_web_ui_that_says_nothing_is_silent_at_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();

        var outcome = await WebUiReadiness.AwaitAsync(
            new WebUiReadiness.Said(TextWriter.Null), running, TimeSpan.FromSeconds(1), ct).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Silent, outcome);
    }

    [Fact]
    public async Task A_broker_that_begins_stopping_is_told_so_rather_than_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();
        using var stopping = new CancellationTokenSource();

        var waiting = WebUiReadiness.AwaitAsync(new WebUiReadiness.Said(TextWriter.Null), running, Patience, stopping.Token);

        await stopping.CancelAsync();

        Assert.Equal(WebUiReadiness.Outcome.Stopping, await waiting.WaitAsync(Patience, ct));
    }

    [Fact]
    public async Task A_silent_web_ui_that_says_it_is_serving_later_is_followed_up_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();
        using var stopping = new CancellationTokenSource();
        await using var log = new StringWriter();
        var said = new WebUiReadiness.Said(TextWriter.Null);

        var following = WebUiReadiness.FollowUpAsync(said, running, "http://127.0.0.1:5280", log, stopping.Token);

        Assert.False(following.IsCompleted, "the follow-up finished before the web UI said anything.");

        said.Line(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5280");
        await following.WaitAsync(Patience, ct);

        Assert.Equal(
            "   Web UI:   http://127.0.0.1:5280 is answering now. Open that one in a browser." + Environment.NewLine,
            log.ToString());
    }

    [Fact]
    public async Task A_follow_up_says_nothing_once_the_broker_is_stopping()
    {
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();
        using var stopping = new CancellationTokenSource();
        await using var log = new StringWriter();
        var said = new WebUiReadiness.Said(TextWriter.Null);

        var following = WebUiReadiness.FollowUpAsync(said, running, "http://127.0.0.1:5280", log, stopping.Token);

        await stopping.CancelAsync();
        await following.WaitAsync(Patience, ct);

        Assert.Equal(string.Empty, log.ToString());
    }

    [Fact]
    public async Task A_silent_web_ui_that_exits_is_followed_up_as_not_started()
    {
        var ct = TestContext.Current.CancellationToken;
        using var exited = await ExitedAsync(ct);
        await using var log = new StringWriter();

        await WebUiReadiness.FollowUpAsync(
            new WebUiReadiness.Said(TextWriter.Null), exited, $"http://127.0.0.1:{Loopback.FreePort()}", log, ct)
            .WaitAsync(Patience, ct);

        Assert.Contains("Web UI:   did not start (parley-web-ui exited with code 3)", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_web_ui_that_exited_is_said_to_have_exited_at_once_not_at_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var exited = await ExitedAsync(ct);

        // A deadline far longer than the patience: an exit noticed only when the deadline ran out fails here.
        var outcome = await WebUiReadiness.AwaitAsync(
            new WebUiReadiness.Said(TextWriter.Null), exited, TimeSpan.FromMinutes(10), ct).WaitAsync(Patience, ct);

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
            new WebUiReadiness.Said(TextWriter.Null), exited, Patience, stopping.Token).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Exited, outcome);
    }

    [Fact]
    public async Task A_web_ui_that_said_it_was_serving_and_then_exited_has_exited()
    {
        // Asked in that order on purpose: a web UI that said so and is gone is not serving, and announcing its address
        // sends the person to nothing, or to whoever takes the port next.
        var ct = TestContext.Current.CancellationToken;
        using var exited = await ExitedAsync(ct);
        var said = new WebUiReadiness.Said(TextWriter.Null);

        said.Line(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5280");

        var outcome = await WebUiReadiness.AwaitAsync(said, exited, Patience, ct).WaitAsync(Patience, ct);

        Assert.Equal(WebUiReadiness.Outcome.Exited, outcome);
    }

    [Fact]
    public async Task A_silent_web_ui_that_exits_while_another_program_holds_its_address_is_followed_up_as_taken()
    {
        // The follow-up names an address held after the web UI exited as another program's, as the banner does, rather
        // than by an exit code that says nothing of where the address went.
        var ct = TestContext.Current.CancellationToken;
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();

        var heldAt = $"http://127.0.0.1:{((IPEndPoint)held.LocalEndpoint).Port}";

        using var exited = await ExitedAsync(ct);
        await using var log = new StringWriter();

        await WebUiReadiness.FollowUpAsync(new WebUiReadiness.Said(TextWriter.Null), exited, heldAt, log, ct)
            .WaitAsync(Patience, ct);

        Assert.Contains($"Web UI:   did not start ({WebUiReadiness.Taken(heldAt)})", log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("exited with code", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_whose_output_has_gone_ends_quietly()
    {
        // Awaited in serve's last finally: thrown there, it would skip disposing the child and end serve with a stack
        // trace on the stop it was meant to finish.
        var ct = TestContext.Current.CancellationToken;
        using var running = Process.GetCurrentProcess();
        var said = new WebUiReadiness.Said(TextWriter.Null);

        said.Line(Parley.Configuration.WebUiAddress.ServingMarker + "http://127.0.0.1:5280");

        await WebUiReadiness.FollowUpAsync(said, running, "http://127.0.0.1:5280", new GoneOutput(), CancellationToken.None)
            .WaitAsync(Patience, ct);
    }

    private sealed class GoneOutput : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("Input/output error");

        public override Task WriteLineAsync(string? value) => throw new IOException("Input/output error");
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
