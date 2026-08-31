using System.Collections.Concurrent;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// What a node does with a turn it has been asked to give.
/// </summary>
/// <remarks>
/// A turn is asked for when the broker believes a session is attached, which is precisely when a
/// start is not asked for. That makes the node the last thing standing between a question and
/// silence, and the three cases it has to tell apart are easy to collapse into two: an agent that
/// is not ours, an agent that is ours and alive, and an agent that is ours and dead. The third
/// looks like the first from one line up, and treating them the same drops the question exactly
/// the way the missing turn command dropped the last one.
/// </remarks>
public sealed class NodeTurnTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("parley-node-turn").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_turn_for_an_agent_that_is_not_ours_is_silent()
    {
        // The common case, and the reason silence is right: an agent attached through somebody's
        // editor window is working perfectly well, driven by the person in front of it. There is
        // nothing a node can do to make one take a turn, and saying so on every question would
        // print a line per message about an agent that is not broken.
        var residents = new ConcurrentDictionary<string, ResidentAgent>(StringComparer.Ordinal);
        var reported = new StringWriter();

        await NodeCommand.TurnAsync(
            new Wire.GiveTurn { Agent = "docs@alpha", Because = "orders asked it a question" },
            residents,
            reported,
            Ct);

        Assert.Empty(reported.ToString());
    }

    [Fact]
    public async Task A_turn_for_a_resident_whose_process_has_died_is_cleared_and_named()
    {
        // The window this guards: the process is gone and the broker has not yet seen the stream
        // close, so it asks for a turn rather than a start. Before this, the node checked that the
        // resident was alive and said nothing when it was not — which reads as correct and drops
        // the question, which is the whole defect in miniature.
        var residents = new ConcurrentDictionary<string, ResidentAgent>(StringComparer.Ordinal);
        var reported = new StringWriter();

        var agent = AgentName.Create("docs@alpha");
        var dead = ResidentAgent.Start(
            agent, InvocationCommand.Parse("dotnet --info"), _workspace, TimeProvider.System);

        residents[agent.Value] = dead;

        // It is launched with the protocol's own arguments appended, which `dotnet --info` will
        // not accept, so it exits at once. All this test needs of it is that it has stopped.
        for (var i = 0; i < 100 && dead.IsAlive; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(dead.IsAlive, "the stand-in process was still running");

        await NodeCommand.TurnAsync(
            new Wire.GiveTurn { Agent = agent.Value, Because = "orders asked it a question" },
            residents,
            reported,
            Ct);

        // Cleared, so the next question arrives as a start and finds nothing stale in the way.
        Assert.Empty(residents);

        // And named, because a broker that believes in a session the machine no longer has is
        // worth one line to whoever is reading the node's output.
        Assert.Contains("docs@alpha", reported.ToString(), StringComparison.Ordinal);

        await dead.DisposeAsync();
    }

    [Fact]
    public async Task A_turn_for_an_unusable_name_is_ignored_rather_than_throwing()
    {
        // The wire carries a string. A node that threw on one would take a machine off the air
        // over a message it could simply decline to act on.
        var residents = new ConcurrentDictionary<string, ResidentAgent>(StringComparer.Ordinal);
        var reported = new StringWriter();

        await NodeCommand.TurnAsync(
            new Wire.GiveTurn { Agent = "Not A Valid Name", Because = "whatever" },
            residents,
            reported,
            Ct);

        Assert.Empty(reported.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A stand-in process may still be releasing a handle. The directory is a temporary
            // one and its survival tells nobody anything.
        }
    }
}
