using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>A node is held to the protocol version as an adapter is (SPEC.md §4.0), because two machines are not one build.</summary>
[Integration]
public sealed class ANodeSpeaksTheSameProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("0.1")]
    [InlineData("")]
    public async Task A_node_on_another_protocol_is_refused_naming_it(string version)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        using var call = broker.ConnectAsNode().Join(
            new Wire.JoinRequest { Machine = "server", ProtocolVersion = version },
            cancellationToken: Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains(ProtocolVersion.Current, refused.Status.Detail, StringComparison.Ordinal);

        if (version.Length > 0)
        {
            Assert.Contains(version, refused.Status.Detail, StringComparison.Ordinal);
        }
    }
}
