using System.Diagnostics;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the web UI writes on its standard error reaches the broker's error output with its control characters
/// neutralised, through the handler <c>WebUiReadiness.Watch</c> attaches, not only through <c>Pass</c> called alone.
/// </summary>
/// <remarks>
/// The real web UI quotes a refused <c>webUi.hosts</c> entry as JSON, which already escapes a control character, so a
/// test driving the real web UI never puts a raw one on the pipe. A stand-in writes one raw.
/// </remarks>
[Integration]
public sealed class TheWebUisErrorsAreInertTests
{
    [Fact]
    public async Task A_control_character_the_web_ui_writes_on_its_errors_reaches_the_broker_neutralised()
    {
        var ct = TestContext.Current.CancellationToken;
        var escape = (char)0x1b;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/d /c echo warn: {escape}[2J a name left out 1>&2")
            : new ProcessStartInfo("/bin/sh", ["-c", "printf 'warn: \\033[2J a name left out\\n' >&2"]);

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        using var child = Process.Start(start) ?? throw new InvalidOperationException("The stand-in did not start.");
        await using var output = new StringWriter();
        await using var errors = new StringWriter();

        _ = WebUiReadiness.Watch(child, output, errors);

        await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Until both readers have reached the end of what it wrote.
        child.WaitForExit();

        var said = errors.ToString();

        // Non-vacuity: the line arrived.
        Assert.Contains("a name left out", said, StringComparison.Ordinal);
        Assert.DoesNotContain(escape, said);
    }
}
