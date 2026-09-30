using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The reads a console's health page makes, over a real socket, against a fresh store.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these was once implemented, unit-tested against a repository, and bound to the wire — and the
/// page that asks for them failed on a deployment two minutes old, because nothing had ever asked for them
/// through the transport. A fresh store is the case: no refusals, no clarifications, no results, nothing
/// flagged, nobody waiting. Empty is what a broker answers with most of its life and the state least likely to
/// have been tried.
/// </para>
/// </remarks>
public sealed class HealthReadTests
{
    [Fact]
    public async Task Every_health_read_answers_on_a_deployment_that_has_done_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var operators = broker.ConnectAsOperator();
        var failures = new List<string>();

        await Attempt("flags", async () => await operators.DescribeFlagsAsync(new Wire.Empty(), cancellationToken: token));
        await Attempt("refusals", async () => await operators.DescribeRefusalsAsync(new Wire.Empty(), cancellationToken: token));
        await Attempt("clarifications", async () => await operators.DescribeClarificationsAsync(new Wire.Empty(), cancellationToken: token));
        await Attempt("results", async () => await operators.DescribeResultsAsync(new Wire.Empty(), cancellationToken: token));
        await Attempt("waiting", async () => await operators.DescribeWaitingAsync(new Wire.Empty(), cancellationToken: token));

        // Reported together rather than one assertion each, so a run says which are broken instead of stopping at
        // the first.
        Assert.True(failures.Count == 0, "a health read the console makes does not answer on an empty deployment: " + string.Join("; ", failures));

        async Task Attempt(string named, Func<Task> read)
        {
            try
            {
                await read();
            }
            catch (RpcException failure)
            {
                failures.Add($"{named} -> {failure.StatusCode} {failure.Status.Detail}".Trim());
            }
        }
    }

    [Fact]
    public async Task The_registry_and_the_conversation_list_answer_on_an_empty_store_too()
    {
        // The two reads a console makes before anything else.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var operators = broker.ConnectAsOperator();

        // Empty of agents: the only rows an empty store holds are the two the broker writes itself (SPEC.md §12.3) —
        // the facilitator, which the adapter marks as Parley, and since specs/023 the person seat.
        var rows = (await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: token)).Rows;

        Assert.Equal(
            [Parley.Core.PersonSeat.Adapter, Parley.Core.Facilitation.Adapter],
            rows.Select(r => r.Adapter).Order(StringComparer.Ordinal));
        Assert.Empty((await operators.ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: token)).Conversations);
    }
}
