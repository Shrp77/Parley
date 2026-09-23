using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a start in flight when the node stops does not leave a process nothing will ever stop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review gate, lens 1.</b> The gate's third finding made looking for a resident and creating one a
/// single act, under a process-wide semaphore, because two callers could otherwise both launch and the
/// loser was overwritten in the table — a process invisible to <c>StopEveryoneAsync</c>. This asks the
/// same question about the other side: <c>StopEveryoneAsync</c> takes no part in that semaphore, and
/// snapshots the table with <c>residents.Keys.ToArray()</c>.
/// </para>
/// <para>
/// <b>So the window is between a launch and the assignment that records it.</b> A start that has spawned
/// its process but not yet written it into the table is absent from a snapshot taken in that moment; the
/// stop then finishes, the start records its resident, and the node loop returns. Nothing calls
/// <c>StopEveryoneAsync</c> again, so what is left in the table is a live agent process with nothing left
/// that would stop it — which is precisely the harm the third finding names.
/// </para>
/// <para>
/// <b>The symptom is observable, which is what makes this a probe rather than an argument.</b> After both
/// calls have returned, a stop that ran to completion should leave nothing alive behind it. Anything in
/// the table that is still running got there after the sweep passed.
/// </para>
/// </remarks>
public sealed class StartRacingStopTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-racestop-" + Path.GetRandomFileName());

    public StartRacingStopTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const int Attempts = 40;

    [Fact]
    public async Task A_start_in_flight_when_the_node_stops_is_not_left_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(_root, "checkout");

        _ = Directory.CreateDirectory(workspace);

        var orphaned = 0;
        var started = 0;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var identity = AgentIdentity.Mint();
            var cataloguePath = Path.Combine(_root, $"node-{attempt}.json");

            // **The checkout declares what each project calls it, and without this nothing starts.** 018
            // made a name resolve to an identity by reading it back out of the repository, so a workspace
            // with no launch configuration resolves to nothing and `StartAsync` returns having done
            // nothing at all — which a first version of this probe reported as forty clean attempts.
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".mcp.json"),
                $$"""
                {
                  "mcpServers": {
                    "parley-alpha": {
                      "command": "parley-mcp",
                      "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" }
                    }
                  },
                  "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
                }
                """,
                ct);

            await File.WriteAllTextAsync(
                cataloguePath,
                $$"""
                {
                  "agents": {
                    "{{identity}}": {
                      "command": {{System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command)}},
                      "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                    }
                  }
                }
                """,
                ct);

            var catalogue = NodeCatalogue.ReadFrom(cataloguePath);
            var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

            // The node is stopping while a start it already accepted is still launching.
            var said = new StringWriter();
            var report = TextWriter.Synchronized(said);

            var starting = Task.Run(
                async () => await NodeCommand.StartAsync(
                    new Wire.StartAgent
                    {
                        Agent = "orders@" + NodeCatalogue.Machine,
                        Because = "a peer asked"
                    },
                    catalogue, "alpha", residents, report, ct),
                ct);

            var stopping = Task.Run(
                async () => await NodeCommand.StopEveryoneAsync(residents, TextWriter.Null),
                ct);

            await Task.WhenAll(starting, stopping);

            // **Anything still alive here is an orphan.** The loop that owns this table has returned, so
            // nothing will call the sweep again — in production that process outlives the node.
            foreach (var left in residents.Values)
            {
                if (left.IsAlive)
                {
                    orphaned++;
                }

                await left.DisposeAsync();
            }

            // **Counted from what the command announced, not from the loop running.** A first version of
            // this probe counted iterations, which a `StartAsync` that launched nothing at all satisfies
            // perfectly — and forty attempts in half a second is exactly what that looks like. It was
            // resolving no name, because the workspace had no launch configuration for one to be read
            // out of. Three probes in this gate have now passed while exercising nothing.
            //
            // **Either outcome counts as the scenario having happened**, and that is the point rather
            // than a loosening: once a node is stopping, a start that is refused is the behaviour under
            // test, so requiring a launch would fail the probe precisely when the guard works.
            var announced = said.ToString();

            if (announced.Contains(": started (", StringComparison.Ordinal)
                || announced.Contains("this node is stopping", StringComparison.Ordinal))
            {
                started++;
            }
        }

        // Guarded against passing by vacuity: a run where `StartAsync` never reached a decision at all —
        // an unresolvable name, say — would satisfy "nothing was orphaned" while measuring nothing.
        Assert.True(
            started > 0,
            "no attempt either launched or was refused, so StartAsync never reached a decision "
            + "and this probe measured nothing at all");

        Assert.True(
            orphaned == 0,
            $"{orphaned} of {started} launched starts left a live agent behind a completed stop, "
            + "which nothing would ever stop");
    }
}
