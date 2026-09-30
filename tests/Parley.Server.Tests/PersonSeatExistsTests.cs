using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a broker provides the person seat and the facilitator in its project before anything attaches, once, and
/// names neither for a machine (SPEC.md §4.1.1, §12.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Over a store on disk, and across a restart</b>, because "written if absent" is a claim about the second start,
/// not the first. Read back through the operator's registry rather than the store, since this project may not open a
/// connection: a row nobody can read through the protocol is not evidence the protocol has one.
/// </para>
/// <para>
/// <b>Watched to fail first</b>: before 023 the registry held no person at all, and the facilitator was
/// <c>parley-facilitator@&lt;this machine&gt;</c> with a machine the broker had recorded for it.
/// </para>
/// </remarks>
public sealed class PersonSeatExistsTests : IDisposable
{
    private static readonly ProjectName Alpha = ProjectName.Create("alpha");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-person-" + Guid.NewGuid().ToString("N"));

    public PersonSeatExistsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Store => Path.Combine(_directory, "parley.db");

    [Fact]
    public async Task A_fresh_broker_holds_one_person_and_one_facilitator_named_for_its_project()
    {
        var token = TestContext.Current.CancellationToken;

        var first = await NamesAsync(token);
        var second = await NamesAsync(token);

        Assert.Equal(["human-user@alpha", "parley-facilitator@alpha"], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task No_machine_is_recorded_under_the_projects_name()
    {
        var token = TestContext.Current.CancellationToken;

        await using var server = await ParleyServer.StartAsync(
            BrokerRuntime.Open(Alpha, Store, Parley.Broker.BrokerOptions.Default, TimeProvider.System),
            "http://127.0.0.1:0",
            token);

        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var nodes = new Wire.Nodes.NodesClient(channel);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await nodes.DescribeAsync(new Wire.DescribeNodeRequest { Machine = "alpha" }, cancellationToken: token));

        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
    }

    /// <summary>Starts a broker over the store, reads the two broker-provided names, and stops it.</summary>
    private async Task<string[]> NamesAsync(CancellationToken token)
    {
        await using var server = await ParleyServer.StartAsync(
            BrokerRuntime.Open(Alpha, Store, Parley.Broker.BrokerOptions.Default, TimeProvider.System),
            "http://127.0.0.1:0",
            token);

        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var registry = await new Wire.Operators.OperatorsClient(channel)
            .DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token);

        return [.. registry.Rows
            .Select(r => r.Name)
            .Where(n => PersonSeat.Is(AgentName.Create(n)) || Facilitation.Is(AgentName.Create(n)))
            .Order(StringComparer.Ordinal)];
    }
}
