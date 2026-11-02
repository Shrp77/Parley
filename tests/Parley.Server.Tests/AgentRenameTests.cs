using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an agent is its identity and a name is only what a project shows for it (SPEC.md §4.1.1).
/// </summary>
/// <remarks>
/// The identity is a label the adapter reports, never authority, so each case is a way a label could be abused or
/// misread: a different repository presenting it, a name taken by somebody else, a session still attached under the
/// old name, the same checkout on another machine.
/// </remarks>
[Integration]
public sealed class AgentRenameTests
{
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";
    private const string Other = "1a2b3c4d-0000-4000-8000-00000000abcd";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string[]> NamesAsync(LoopbackBroker broker) =>
        [.. (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
            .Select(row => row.Name)];

    private static async Task Detached(LoopbackBroker broker, AttachedClient agent)
    {
        await agent.DisposeAsync();
        await broker.WaitUntilDetachedAsync(agent.Name, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_attach_under_a_new_name_with_the_same_identity_renames_the_agent()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var first = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@m1", cancellationToken: Ct);

        _ = await orders.Client.RequestAsync(
            Requests.Open("orders@m1", "billing@m1", "what do you own?"), cancellationToken: Ct);

        await Detached(broker, first);

        await using var renamed = await AttachedClient.AttachAsync(
            broker, "payments@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);

        var names = await NamesAsync(broker);

        Assert.Contains("payments@m1", names);
        Assert.DoesNotContain("billing@m1", names);

        // What was asked of it under the old name is still asked of it.
        var waiting = Assert.Single((await renamed.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = "payments@m1" }, cancellationToken: Ct)).Messages);

        Assert.Contains("what do you own?", waiting.Message.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_identity_from_another_workspace_is_refused()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var first = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);
        await Detached(broker, first);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await AttachedClient.AttachAsync(
            broker, "payments@m1", workspace: "/work/somewhere-else", cancellationToken: Ct, identity: Id));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("billing@m1", refused.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(["billing@m1"], (await NamesAsync(broker)).Where(name => name.Contains("@m1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_name_somebody_else_holds_is_not_taken()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var billing = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);
        var payments = await AttachedClient.AttachAsync(
            broker, "payments@m1", workspace: "/work/payments", cancellationToken: Ct, identity: Other);
        await Detached(broker, billing);
        await Detached(broker, payments);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await AttachedClient.AttachAsync(
            broker, "payments@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);

        var names = await NamesAsync(broker);

        Assert.Contains("billing@m1", names);
        Assert.Contains("payments@m1", names);
    }

    [Fact]
    public async Task A_rename_waits_until_the_old_name_has_detached()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        await using var running = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await AttachedClient.AttachAsync(
            broker, "payments@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("still attached", refused.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("billing@m1", await NamesAsync(broker));
    }

    [Fact]
    public async Task With_no_identity_a_new_name_is_a_new_agent()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var first = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct);
        await Detached(broker, first);

        await using var second = await AttachedClient.AttachAsync(
            broker, "payments@m1", workspace: "/work/billing", cancellationToken: Ct);

        var names = await NamesAsync(broker);

        Assert.Contains("billing@m1", names);
        Assert.Contains("payments@m1", names);
    }

    [Fact]
    public async Task The_same_identity_on_another_machine_is_another_agent()
    {
        // A committed launch configuration carries one identifier to every machine that checks the repository out.
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        await using var here = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);
        await using var there = await AttachedClient.AttachAsync(
            broker, "billing@m2", workspace: "/work/billing", cancellationToken: Ct, identity: Id);

        var names = await NamesAsync(broker);

        Assert.Contains("billing@m1", names);
        Assert.Contains("billing@m2", names);
    }

    [Fact]
    public async Task Something_that_is_not_an_identity_is_refused_whole()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await AttachedClient.AttachAsync(
            broker, "billing@m1", cancellationToken: Ct, identity: "not-an-identity"));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.DoesNotContain("billing@m1", await NamesAsync(broker));
    }

    [Fact]
    public async Task A_joining_node_is_told_the_identity_of_each_agent_that_has_one()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var first = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);
        await Detached(broker, first);

        using var stream = broker.ConnectAsNode().Join(
            new Wire.JoinRequest { Machine = "m1", Facts = new Wire.NodeFacts { Os = "test-os" } }, cancellationToken: Ct);

        Assert.True(await stream.ResponseStream.MoveNext(Ct));

        var joined = stream.ResponseStream.Current.Joined;

        Assert.Equal(Id, joined.Identities["billing@m1"]);
    }

    [Fact]
    public async Task A_start_names_the_checkout_by_its_identity_and_reaches_a_node_that_knows_it_only_so()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        // The node announces no role at all: the repository's launch configuration has been renamed since, so only the
        // identifier still says which checkout this is.
        await using var node = await JoinedNodeClient.JoinAsync(
            broker, "m1", [], Ct, reportsTurns: true, identities: [Id]);

        var first = await AttachedClient.AttachAsync(
            broker, "billing@m1", workspace: "/work/billing", cancellationToken: Ct, identity: Id);
        await Detached(broker, first);

        _ = await broker.ConnectAsOperator().StartAsync(new Wire.StartRequest { Agent = "billing@m1" }, cancellationToken: Ct);

        var command = await node.WaitForCommandAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("billing@m1", command.Start.Agent);
        Assert.Equal(Id, command.Start.Identity);
    }
}
