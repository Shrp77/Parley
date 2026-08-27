using System.Diagnostics;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// What stopping a broker costs when agents are attached to it.
/// </summary>
/// <remarks>
/// <para>
/// Found by measuring rather than reading, and it was not subtle once measured: a broker with one
/// agent attached took thirty seconds to stop, every time.
/// </para>
/// <para>
/// The cause is the design working as intended in two places that had not been put together. An
/// <c>Attach</c> handler holds its stream open by blocking on a read that completes only when its
/// channel does — that is what makes a connection evidence (SPEC.md §4.0) — and a graceful
/// shutdown waits for every handler to return before it finishes. Neither is wrong; together they
/// wait the host's full shutdown timeout.
/// </para>
/// <para>
/// What a person would have seen is Ctrl+C appearing to hang. What a service manager would have
/// seen is a process that missed its stop deadline and had to be killed — which is the worse
/// half, because a killed broker is one that did not get to finish whatever it was writing.
/// </para>
/// </remarks>
public sealed class ShutdownTests
{
    [Fact]
    public async Task A_broker_with_agents_attached_stops_promptly()
    {
        var token = TestContext.Current.CancellationToken;

        var broker = await LoopbackBroker.StartAsync(token);

        await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);
        await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        var stopping = Stopwatch.StartNew();

        await broker.DisposeAsync();

        stopping.Stop();

        // Generous, deliberately. The number being defended is not "fast" — it is "does not wait
        // for a timeout", and the two are three orders of magnitude apart. A threshold near the
        // real cost would fail on a loaded CI agent and teach somebody to raise it.
        Assert.True(
            stopping.Elapsed < TimeSpan.FromSeconds(5),
            $"stopping took {stopping.Elapsed}, which means it waited for the shutdown timeout " +
            "rather than closing its held streams");
    }
}
