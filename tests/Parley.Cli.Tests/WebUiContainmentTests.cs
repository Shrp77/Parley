using Parley.Core;
using Parley.Configuration;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a web UI which cannot start leaves the broker serving.
/// </summary>
/// <remarks>
/// <para>
/// FR-017 and SC-008. The web UI is a child process now rather than a host inside the broker, and
/// the containment matters as much either way: its likeliest failures are a port already in use
/// and a tool that is not installed on this machine — and both are ordinary, since the surfaces
/// are chosen per machine. A broker that refused to serve for either would take a whole deployment
/// down for the one part of it no agent uses.
/// </para>
/// <para>
/// <b>Probed rather than read.</b> The port is genuinely held by this test for the length of the
/// run, so the child genuinely fails to bind — which is the condition, not a simulation of it.
/// </para>
/// </remarks>
public sealed class WebUiContainmentTests
{
    [Fact]
    public async Task A_web_ui_that_cannot_bind_leaves_the_broker_answering()
    {
        var port = Loopback.FreePort();
        var address = $"http://127.0.0.1:{port}";

        // Held for the whole run. Taking it and giving it back would be a race; holding it is the
        // condition itself.
        using var held = new TcpListener(IPAddress.Loopback, port);

        held.Start();

        var root = Path.Combine(Path.GetTempPath(), $"parley-contain-{Guid.NewGuid():N}");
        var brokerAt = $"http://127.0.0.1:{Loopback.FreePort()}";

        // A project rather than a store and a port: `serve` takes neither, and the endpoint this test
        // needs to dial goes in the project's own file.
        var probe = Parley.Core.ProjectName.Create("probe");

        ProjectManifest.Write(
            root,
            [new ProjectSettings(probe, ProjectFolder.StoreFor(root, probe), brokerAt, null)]);

        using var broker = ToolProcess.Launch(
            [],
            "serve", "--config", root, "--with-web-ui", "--web-ui", address);

        try
        {
            // The claim: the broker answers. Not that it started, not that it printed something —
            // that a client can reach it, which is what an agent needs.
            var answered = await Loopback.AnswersAsync(
                brokerAt, TestContext.Current.CancellationToken);

            Assert.True(
                answered,
                "the broker never answered while the web UI's port was held, which is the whole "
                + "failure this containment exists to prevent.");

            Assert.False(broker.HasExited, "the broker exited because a web page could not bind.");
        }
        finally
        {
            held.Stop();

            if (!broker.HasExited)
            {
                broker.Kill(entireProcessTree: true);
                broker.WaitForExit(10_000);
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; the store is in a temporary directory either way.
            }
        }
    }

    [Fact]
    public void The_broker_knows_the_web_ui_by_a_command_name_and_nothing_else()
    {
        // FR-015, read off the source because it is a property of what this tool references rather
        // than of what it does. The graph walk in Parley.Client.Tests says the assembly is not
        // reachable; this says the code does not name it either — a `using Parley.Web` that
        // compiled would mean the reference had come back.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("const string WebUiTool = \"parley-web-ui\";", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Parley.Web.ConsoleHost", program, StringComparison.Ordinal);
        Assert.DoesNotContain("using Parley.Web;", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_child_is_not_waited_on_and_not_restarted()
    {
        // A web UI that exits is a page that is gone, and a broker that ended with it would trade a
        // deployment for a web page. Restarting it would be worse: a port held by something else
        // makes that a loop, and the loop would run for as long as the broker does.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");
        var start = program.IndexOf("async Task<int> HostingWebUi", StringComparison.Ordinal);

        Assert.True(start > 0, "the web UI is no longer started from Program.cs");

        var block = program[start..program.IndexOf("// Passed apart", start, StringComparison.Ordinal)];

        // Comments stripped first. A prose check searching for "while" found the word inside a
        // sentence about shutdown and reported a restart loop that is not there -- which is a probe
        // measuring its own wording rather than the code.
        var code = string.Join(
            Environment.NewLine,
            block.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("child.WaitForExitAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("while (", code, StringComparison.Ordinal);
        Assert.DoesNotContain("for (", code, StringComparison.Ordinal);

        // And it is ended with the broker, so nothing is orphaned: a web UI outliving the broker
        // serves a deployment that has stopped, on the port the next one would want.
        Assert.Contains("child.Kill(entireProcessTree: true)", block, StringComparison.Ordinal);
    }
}
