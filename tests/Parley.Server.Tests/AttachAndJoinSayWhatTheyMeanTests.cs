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

    private static Wire.AttachRequest Attach(string expected) =>
        new()
        {
            Agent = "infra@server",
            Adapter = "mcp",
            Tier = Wire.AgentTier.Pull,
            ProtocolVersion = ProtocolVersion.Current,
            ExpectedProject = expected,
        };

    [Fact]
    public async Task A_broker_says_which_project_it_serves_to_a_machine_it_has_never_heard_of()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var reply = await broker.ConnectAsNode().ServedProjectAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.False(string.IsNullOrEmpty(reply.Project));
    }

    [Fact]
    public async Task An_attach_expecting_another_project_is_refused_naming_both_and_writes_no_row()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var served = (await broker.ConnectAsNode().ServedProjectAsync(new Wire.Empty(), cancellationToken: Ct)).Project;

        using var call = broker.Connect().Attach(Attach("somewhere-else"), cancellationToken: Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(Ct));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("somewhere-else", refused.Status.Detail, StringComparison.Ordinal);
        Assert.Contains(served, refused.Status.Detail, StringComparison.Ordinal);

        var registry = await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.DoesNotContain(registry.Rows, row => row.Name == "infra@server");
    }

    [Fact]
    public async Task An_attach_expecting_this_project_attaches()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var served = (await broker.ConnectAsNode().ServedProjectAsync(new Wire.Empty(), cancellationToken: Ct)).Project;

        using var call = broker.Connect().Attach(Attach(served), cancellationToken: Ct);

        Assert.True(await call.ResponseStream.MoveNext(Ct));
        Assert.Equal(Wire.Event.EventOneofCase.Attached, call.ResponseStream.Current.EventCase);
    }

    [Fact]
    public async Task An_attach_expecting_nothing_is_not_asked()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        using var call = broker.Connect().Attach(Attach(string.Empty), cancellationToken: Ct);

        Assert.True(await call.ResponseStream.MoveNext(Ct));
    }

    [Fact]
    public async Task What_an_attach_expects_is_held_to_one_printable_line()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        using var call = broker.Connect().Attach(Attach("two\nlines"), cancellationToken: Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(Ct));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
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
