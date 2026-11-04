using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>What a machine says it expects of a broker, and what it is told when that is wrong (SPEC.md §4.0, §4.1.2).</summary>
[Integration]
public sealed class AttachAndJoinSayWhatTheyMeanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.AttachRequest Attach() =>
        new()
        {
            Agent = "infra@server",
            Adapter = "mcp",
            Tier = Wire.AgentTier.Pull,
            ProtocolVersion = ProtocolVersion.Current,
        };

    [Fact]
    public async Task An_identity_arriving_for_a_row_that_had_none_attaches_and_is_kept()
    {
        // A file gaining a machine section makes an agent present an identity where it sent none before.
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";

        using (var first = broker.Connect().Attach(Attach(), cancellationToken: Ct))
        {
            Assert.True(await first.ResponseStream.MoveNext(Ct));
        }

        await broker.WaitUntilDetachedAsync("infra@server", TimeSpan.FromSeconds(10));

        var again = Attach();
        again.Identity = Id;
        again.Workspace = "/srv/infra";

        using var second = broker.Connect().Attach(again, cancellationToken: Ct);

        Assert.True(await second.ResponseStream.MoveNext(Ct));
        Assert.Equal(Wire.Event.EventOneofCase.Attached, second.ResponseStream.Current.EventCase);

        var row = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct))
            .Rows.Single(r => r.Name == "infra@server");

        Assert.Equal("/srv/infra", row.Workspace);
    }

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
