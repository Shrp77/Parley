using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That no operator operation acts on the person's seat, and each says why in the same sentence (SPEC.md §10.2,
/// specs/023 T048), with the person row present, as it always is.
/// </summary>
/// <remarks>
/// Before, the five answered five different ways: a raw storage constraint for retire and purge, a node asked to start
/// somebody no node can, an instruction "undeliverable", and a reinstate that silently changed nothing.
/// </remarks>
public sealed class PersonNameIsReservedTests
{
    private const string Person = "human-user@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Operations => ["start", "retire", "reinstate", "purge", "instruct"];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task No_operator_operation_acts_on_the_person(string operation)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var operators = broker.ConnectAsOperator();

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            switch (operation)
            {
                case "start":
                    _ = await operators.StartAsync(new Wire.StartRequest { Agent = Person }, cancellationToken: Ct);
                    break;
                case "retire":
                    _ = await operators.RetireAsync(new Wire.AgentRequest { Agent = Person }, cancellationToken: Ct);
                    break;
                case "reinstate":
                    _ = await operators.ReinstateAsync(new Wire.AgentRequest { Agent = Person }, cancellationToken: Ct);
                    break;
                case "purge":
                    _ = await operators.PurgeAsync(new Wire.AgentRequest { Agent = Person }, cancellationToken: Ct);
                    break;
                default:
                    _ = await operators.InstructAsync(
                        new Wire.InstructRequest { Agent = Person, Text = "do something" }, cancellationToken: Ct);
                    break;
            }
        });

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("person's seat", refused.Status.Detail, StringComparison.Ordinal);

        // And the person is still there, still active.
        var row = (await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
            .Single(r => r.Name == Person);

        Assert.Equal(Wire.AgentStatus.Active, row.Status);
    }
}
