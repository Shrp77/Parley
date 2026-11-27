using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>That a node's report of background work carries each task by the runner's id, its kind and its label
/// (feature 043, SPEC.md §11.6).</summary>
/// <remarks>
/// The broker shows the person each task by the label it is sent and tells the kinds apart by the kind it is sent; a report
/// that carried the id where the label goes, or every task as one kind, would still count right and name every task wrong.
/// The call is answered here, on the node's side, so nothing listens.
/// </remarks>
[Unit]
public sealed class TheNodeReportsEachTaskByNameTests
{
    [Fact]
    public async Task Each_task_is_reported_with_its_id_kind_and_label_beside_the_count_and_number()
    {
        var said = new KeepsWhatWasSent();

        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel.Intercept(said)), "m1", TextWriter.Null);

        var work = new BackgroundSnapshot(
            3,
            [
                new Parley.Core.BackgroundTask("af7da04eab7667f03", Parley.Core.TaskKind.Agent, "Count to three"),
                new Parley.Core.BackgroundTask("b8n705a3e", Parley.Core.TaskKind.Command, "Wait twenty-five seconds"),
            ])
        {
            Sequence = 41,
        };

        await reporter.BackgroundAsync(Parley.Core.AgentName.Create("billing@m1"), work);

        var report = Assert.Single(said.Reports);

        Assert.Equal("m1", report.Machine);
        Assert.Equal("billing@m1", report.Agent);
        Assert.Equal(3, report.Background.Tasks);
        Assert.Equal(41, report.Background.Sequence);
        Assert.Collection(
            report.Background.Running,
            first =>
            {
                Assert.Equal("af7da04eab7667f03", first.Id);
                Assert.Equal(Wire.TaskKind.Agent, first.Kind);
                Assert.Equal("Count to three", first.Label);
            },
            second =>
            {
                Assert.Equal("b8n705a3e", second.Id);
                Assert.Equal(Wire.TaskKind.Command, second.Kind);
                Assert.Equal("Wait twenty-five seconds", second.Label);
            });
    }

    /// <summary>Answers each turn report at once, keeping what it said.</summary>
    private sealed class KeepsWhatWasSent : Interceptor
    {
        public List<Wire.TurnReport> Reports { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            if (request is Wire.TurnReport report)
            {
                Reports.Add(report);

                return new AsyncUnaryCall<TResponse>(
                    Task.FromResult((TResponse)(object)new Wire.Empty()),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            }

            return continuation(request, context);
        }
    }
}
