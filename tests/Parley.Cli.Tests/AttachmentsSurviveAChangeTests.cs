using Grpc.Net.Client;
using Parley.Core;
using Wire = Parley.Transport;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That adding a project does not disconnect an agent attached to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half that matters, and the half a restart would silently break.</b> Two
/// things are required: a project named while Parley runs is reachable within ten seconds, *and* no agent
/// attached to any other project loses its connection.
/// </para>
/// <para>
/// It matters because the cheap implementation passes the first and fails the second. Re-reading the
/// root and restarting the process would serve the new project within seconds — and SPEC.md §12 says
/// what that costs: the node stops, tells every agent it started to quit, and the process is what
/// held everything each had learned. §4.0 makes an open connection the evidence an agent is
/// reachable, so a dropped attachment is an agent that has gone as far as the deployment knows.
/// </para>
/// <para>
/// <b>Asserted on a live attach stream rather than on a log line.</b> What must survive is the
/// connection, so the test holds one open across the change and reads from it afterwards — a probe
/// that checked for the absence of a "stopping" message would pass against a broker that dropped
/// the stream without saying so.
/// </para>
/// </remarks>
public sealed class AttachmentsSurviveAChangeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-attached-" + Path.GetRandomFileName());

    public AttachmentsSurviveAChangeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ProjectSettings Project(string name)
    {
        var project = ProjectName.Create(name);

        return new ProjectSettings(
            project,
            Path.Combine(_directory, name, "parley.db"),
            $"http://127.0.0.1:{Loopback.FreePort()}",
            null);
    }

    [Fact]
    public async Task An_agent_attached_to_one_project_survives_another_being_added()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        var manifest = new List<ProjectSettings> { orders };
        var changed = false;

        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            output,
            error,
            reread: () =>
            {
                if (!changed)
                {
                    return null;
                }

                changed = false;

                return manifest;
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await Loopback.AnswersAsync(orders.Endpoint, ct), "the first project never came up");

            // An ordinary agent attachment against the first project, held open for the rest of the
            // test. §12.3 makes registering the adapter's own act on attach, which is what this is.
            using var channel = GrpcChannel.ForAddress(orders.Endpoint);

            var agents = new Wire.Agents.AgentsClient(channel);

            using var attached = agents.Attach(
                new Wire.AttachRequest
                {
                    Agent = "orders@test",
                    Adapter = "test",
                    Tier = Wire.AgentTier.Pull,
                    ProtocolVersion = ProtocolVersion.Current
                },
                cancellationToken: ct);

            Assert.True(
                await attached.ResponseStream.MoveNext(ct),
                "the attach stream ended before it said anything");

            // Now name the second project, which is what the terminal's form does.
            manifest.Add(billing);
            changed = true;

            Assert.True(
                await Loopback.AnswersAsync(billing.Endpoint, ct),
                "the project named while serving never came up");

            // **The assertion this test exists for.** The stream is still open and the first
            // project's broker still answers it. A broker that had restarted, or a node that had
            // swept the shared table, would have ended this.
            var waiting = await agents.WaitingAsync(
                new Wire.WaitingRequest { Agent = "orders@test" }, cancellationToken: ct);

            Assert.NotNull(waiting);

            // **And the stream itself, which is the only assertion a restart could not satisfy.**
            // A broker torn down and rebuilt would rebind the same port and answer `Waiting` again,
            // so the two calls above would pass against exactly the failure this test is named for.
            // What cannot survive a restart is this stream: if it had ended, `MoveNext` returns
            // false at once. Still pending after half a second is the connection still being held.
            // **Two outcomes are both healthy and one is not**: the stream carries messages, so a read
            // completing is ordinary. What distinguishes them is the *value* — `MoveNext` answers
            // false only when the stream has ended, which is the disconnection.
            var next = attached.ResponseStream.MoveNext(ct);

            _ = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(500), ct));

            if (next.IsCompleted)
            {
                Assert.True(
                    await next,
                    "the attach stream ended when another project was added, so the agent was disconnected");
            }

            // Still pending is the other healthy answer: nothing to deliver, connection held.
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);

        // Nothing said it was stopping anybody: the change added a broker and touched no other.
        Assert.DoesNotContain("orders: stopping", output.ToString(), StringComparison.Ordinal);
    }
}
