using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That no operator operation on an agent can be pointed at the facilitator (SPEC.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>A person could reach every one of these from a list that showed Parley as a row.</b> The surfaces offer nothing
/// on its row, but a surface is not the only client, and a refusal that came back as a storage exception would name a
/// constraint rather than say what Parley is.
/// </para>
/// <para>
/// <b>Asserted over the wire, and afterwards against the registry</b>: the facilitator is still there, still active,
/// with no accepted line. Each case was watched to pass through with the guard removed.
/// </para>
/// </remarks>
public sealed class ParleyCannotBeWithdrawnTests
{
    public static TheoryData<string> Operations => ["retire", "reinstate", "purge", "start", "record"];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_operation_on_an_agent_refuses_the_facilitator(string operation)
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var operators = broker.ConnectAsOperator();
        var parley = broker.Runtime.Facilitator.Value;

        Task call = operation switch
        {
            "retire" => operators.RetireAsync(new Wire.AgentRequest { Agent = parley }, cancellationToken: token).ResponseAsync,
            "reinstate" => operators.ReinstateAsync(new Wire.AgentRequest { Agent = parley }, cancellationToken: token).ResponseAsync,
            "purge" => operators.PurgeAsync(new Wire.AgentRequest { Agent = parley }, cancellationToken: token).ResponseAsync,
            "start" => operators.StartAsync(new Wire.StartRequest { Agent = parley }, cancellationToken: token).ResponseAsync,
            "record" => operators.RecordOwnershipAsync(
                new Wire.RecordOwnershipRequest
                {
                    Agent = parley,
                    Statement = "everything",
                    Source = Wire.OwnershipSource.Composed
                },
                cancellationToken: token).ResponseAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        var refused = await Assert.ThrowsAsync<RpcException>(() => call);

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("Parley", refused.Status.Detail, StringComparison.Ordinal);

        var row = Assert.Single(
            (await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows,
            r => r.Name == parley);

        Assert.Equal(Wire.AgentStatus.Active, row.Status);
        Assert.Empty(row.OwnsAccepted);
    }
}
