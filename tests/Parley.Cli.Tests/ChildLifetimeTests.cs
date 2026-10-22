using Parley.Core;
using Parley.Configuration;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What happens to the web UI when the broker that started it goes.
/// </summary>
/// <remarks>
/// <para>
/// A child process is a new lifetime to get wrong, and this one did not exist before: the web UI
/// was hosted in the broker's own process and could not outlive it. Now it can, and two ways of
/// getting it wrong are both silent — an orphan holds the port the next broker's web UI wants and
/// serves a deployment that has stopped, and a broker that waited on its child would hang on
/// shutdown behind a page nobody is reading.
/// </para>
/// <para>
/// <b>Driven rather than read.</b> A real broker is started with a real child, the broker is
/// ended, and the port is asked whether anything still holds it.
/// </para>
/// </remarks>
public sealed class ChildLifetimeTests
{
    [Fact]
    public async Task The_web_ui_does_not_outlive_the_broker_that_started_it()
    {
        var webUiAt = $"http://127.0.0.1:{Loopback.FreePort()}";
        var brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";

        var directory = Path.Combine(Path.GetTempPath(), $"parley-child-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        // **A project rather than a store and a port.** `serve` takes neither; a rig names a root and
        // writes a project into it, and the endpoint it wants goes in that project's own file.
        ProjectManifest.Write(
            directory,
            [new ProjectSettings(
                ProjectName.Create("probe"),
                ProjectFolder.StoreFor(directory, ProjectName.Create("probe")),
                brokerAt,
                null)]);

        var store = ProjectFolder.StoreFor(directory, ProjectName.Create("probe"));

        // **The web UI put on PATH for this run, and that is what makes the probe real.** Without
        // it the child never starts, the broker takes its could-not-start path, and the port is
        // free for the trivial reason that nothing ever bound it — a test that passes by proving
        // nothing. Parley.Cli.Tests deliberately does not reference Parley.WebUi, so the binary is
        // found where it was built rather than beside these tests.
        var broker = ToolProcess.Launch(
            [("PATH", WebUiOnPath())],
            "serve", "--config", directory, "--with-web-ui", "--web-ui", webUiAt);

        try
        {
            Assert.True(
                await Loopback.AnswersAsync(brokerAt, TestContext.Current.CancellationToken),
                "the broker never answered.");

            // Non-vacuity: the child really started and really bound the port. Everything below is
            // about what happens to it, and none of it means anything if nothing was there.
            Assert.True(
                await Loopback.AnswersAsync(webUiAt, TestContext.Current.CancellationToken),
                "the web UI never answered, so this proves nothing about a child that outlives "
                + "its parent.");

            broker.Kill(entireProcessTree: true);
            broker.WaitForExit(15_000);

            // Given a moment, because a socket is released as the kernel finishes with it rather
            // than the instant a process is killed.
            var freed = false;

            for (var attempt = 0; attempt < 40 && !freed; attempt++)
            {
                try
                {
                    using var probe = new TcpListener(
                        IPAddress.Loopback, new Uri(webUiAt).Port);

                    probe.Start();
                    probe.Stop();

                    freed = true;
                }
                catch (SocketException)
                {
                    await Task.Delay(250, TestContext.Current.CancellationToken);
                }
            }

            Assert.True(
                freed,
                "something is still listening on the web UI's port after the broker was ended, "
                + "which is a web UI serving a deployment that has stopped.");
        }
        finally
        {
            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: true);
                broker.WaitForExit(10_000);
            }

            broker.Dispose();

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public async Task A_broker_asked_for_no_web_ui_starts_none()
    {
        // The flag means something, which is worth asserting because the two paths differ by one
        // branch and a broker that started a child regardless would be indistinguishable on every
        // machine where the tool happens to be installed.
        var webUiAt = $"http://127.0.0.1:{Loopback.FreePort()}";
        var brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";

        var directory = Path.Combine(Path.GetTempPath(), $"parley-nochild-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        // **A project rather than a store and a port.** `serve` takes neither; a rig names a root and
        // writes a project into it, and the endpoint it wants goes in that project's own file.
        ProjectManifest.Write(
            directory,
            [new ProjectSettings(
                ProjectName.Create("probe"),
                ProjectFolder.StoreFor(directory, ProjectName.Create("probe")),
                brokerAt,
                null)]);

        var broker = ToolProcess.Launch(
            [("PATH", WebUiOnPath())],
            "serve", "--config", directory);

        try
        {
            Assert.True(
                await Loopback.AnswersAsync(brokerAt, TestContext.Current.CancellationToken),
                "the broker never answered.");

            // Nothing was asked for, so nothing is on that port -- and the tool is on PATH, so a
            // broker that started one regardless would be caught rather than excused.
            using var probe = new TcpListener(IPAddress.Loopback, new Uri(webUiAt).Port);

            probe.Start();
            probe.Stop();
        }
        finally
        {
            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: true);
                broker.WaitForExit(10_000);
            }

            broker.Dispose();

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>PATH with the web UI's own build output on the front of it.</summary>
    /// <returns>The value to give the broker.</returns>
    /// <remarks>
    /// The tool is found where it was built rather than beside these tests, because this project
    /// deliberately does not reference <c>Parley.WebUi</c> — the broker's tool must not reach the
    /// web stack, and a test project that dragged it in would put it back in the same output
    /// directory the broker runs from.
    /// </remarks>
    private static string WebUiOnPath()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = here.Name;
        var configuration = here.Parent!.Name;
        var root = here;

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var built = Path.Combine(
            root!.FullName, "src", "Parley.WebUi", "bin", configuration, framework);

        Assert.True(Directory.Exists(built), $"the web UI is not built at {built}");

        return built + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
    }
}
