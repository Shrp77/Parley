using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What a refused start, stop or restart says of an agent a node that is gone left running (SPEC.md §11.1, §11.7).
/// </summary>
/// <remarks>
/// Review, quality lens (0.62.9): such a process reads as open outside Parley too, so the refusal blamed somebody's editor,
/// the sentence the spec calls the defect, and named no way out. A process with no token is an editor, and is still said so.
/// </remarks>
[Integration]
public sealed class AStartOrStopOfALeftProcessNamesKillTests
{
    private const string Agent = "billing@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<RpcException>> RefusalsAsync(LoopbackBroker broker)
    {
        var operators = broker.ConnectAsOperator();

        var start = await Assert.ThrowsAsync<RpcException>(
            async () => await operators.StartAsync(new Wire.StartRequest { Agent = Agent }, cancellationToken: Ct));
        var stop = await Assert.ThrowsAsync<RpcException>(
            async () => await operators.StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));
        var restart = await Assert.ThrowsAsync<RpcException>(
            async () => await operators.RestartAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct));

        return [start, stop, restart];
    }

    [Fact]
    public async Task A_process_a_gone_node_left_is_not_blamed_on_an_editor_and_names_kill()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var left = await AttachedClient.AttachAsync(
            broker, Agent, cancellationToken: Ct, launchToken: "from-a-node-that-is-gone", processId: 4242);

        foreach (var refused in await RefusalsAsync(broker))
        {
            Assert.Contains("left by a node that is gone", refused.Status.Detail, StringComparison.Ordinal);
            Assert.Contains("kill", refused.Status.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("editor", refused.Status.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_process_with_no_token_is_still_said_to_be_somebodys_editor()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var editor = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, processId: 4242);

        foreach (var refused in await RefusalsAsync(broker))
        {
            Assert.Contains("started it in an editor", refused.Status.Detail, StringComparison.Ordinal);
        }
    }
}
