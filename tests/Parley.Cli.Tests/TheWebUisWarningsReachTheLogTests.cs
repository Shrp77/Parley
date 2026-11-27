using System.Diagnostics;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the web UI a broker started writes on its standard error reaches the broker's <c>--log</c>, neutralised.
/// </summary>
/// <remarks>
/// docs/operating.md says the log keeps everything that console writes, its warnings included (a name in
/// <c>webUi.hosts</c> it left out, the other names it answers to), with any control characters made harmless. Those
/// warnings are written on the web UI's standard error, which a hidden logon task points at nothing unless the broker
/// reads it. Driven with a real broker and a real web UI, because the unit tests hold <c>WebUiReadiness.Watch</c> alone
/// and not what <c>serve</c> hands it.
/// </remarks>
[Integration]
public sealed class TheWebUisWarningsReachTheLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-warn-" + Path.GetRandomFileName());

    public TheWebUisWarningsReachTheLogTests() => Directory.CreateDirectory(_root);

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
    public async Task The_web_uis_warnings_reach_the_log_with_no_control_character()
    {
        var ct = TestContext.Current.CancellationToken;
        var webUiAt = $"http://127.0.0.1:{Loopback.FreePort()}";
        var log = Path.Combine(_root, "broker.log");

        // A wildcard is left out with a warning, and one with an escape in it is left out with a warning that quotes it.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "settings.json"),
            """{ "webUi": { "hosts": [ "*.example.test", "zz\u001b[2Jbad.example.test" ] } }""",
            ct);

        using var broker = ToolProcess.Launch(
            [("PATH", ChildLifetimeTests.WebUiOnPath())],
            "serve", "--config", _root, "--with-web-ui", "--web-ui", webUiAt, "--log", log);

        try
        {
            var deadline = Stopwatch.StartNew();
            var text = string.Empty;

            while (deadline.Elapsed < TimeSpan.FromSeconds(45) && !broker.HasExited)
            {
                text = Read(log);

                if (text.Contains("Open that one", StringComparison.Ordinal)
                    && text.Contains("bad.example.test", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(200, ct);
            }

            // Non-vacuity: the web UI really started and was announced.
            Assert.Contains("Open that one", text, StringComparison.Ordinal);

            Assert.Contains("\"*.example.test\" in \"webUi.hosts\"", text, StringComparison.Ordinal);
            Assert.Contains("bad.example.test", text, StringComparison.Ordinal);
            Assert.DoesNotContain((char)0x1b, text);
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

    private static string Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
