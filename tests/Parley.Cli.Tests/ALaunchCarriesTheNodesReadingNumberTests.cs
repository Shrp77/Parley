using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>That a node's launch report carries its reading number at the launch (SPEC.md §11.6).</summary>
/// <remarks>
/// The broker holds a launch as nothing running at that number, so a reading of the last process that the node made before
/// the launch, and that lands after it, is older and changes nothing (lens 3 run 2 on #115). Without the number the launch is
/// held at whatever the broker last heard, and a later-numbered reading of the old process shows its work again. The call is
/// answered here, on the node's side, so nothing listens.
/// </remarks>
[Unit]
public sealed class ALaunchCarriesTheNodesReadingNumberTests
{
    [Fact]
    public async Task A_launch_is_numbered_after_every_reading_made_before_it()
    {
        var before = BackgroundSnapshot.NextSequence();
        var said = new SaysWhatWasSent();

        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel.Intercept(said)), "m1", TextWriter.Null);

        _ = await reporter.LaunchAsync(Parley.Core.AgentName.Create("billing@m1"));

        var launch = Assert.Single(said.Launches);
        Assert.True(launch.Sequence > before, $"the launch was numbered {launch.Sequence}, not after {before}");
        Assert.True(BackgroundSnapshot.NextSequence() > launch.Sequence, "a reading made after the launch is not numbered after it");
    }

    [Fact]
    public async Task A_process_told_to_another_project_is_numbered_too()
    {
        // A checkout in two projects: the second hears of the launch from this report, and to it the process is new.
        var before = BackgroundSnapshot.NextSequence();
        var said = new SaysWhatWasSent { HoldUpFirst = true };

        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel.Intercept(said)), "m1", TextWriter.Null);

        await reporter.ReportRunningAsync(
            Parley.Core.AgentIdentity.Mint(), "billing", "t-1", "/w", () => true);

        // Numbered once, before the first try: the retry carries the same number.
        Assert.Equal(2, said.Launches.Count);
        Assert.True(said.Launches[0].Sequence > before, $"the report was numbered {said.Launches[0].Sequence}, not after {before}");
        Assert.Equal(said.Launches[0].Sequence, said.Launches[1].Sequence);
    }

    [Fact]
    public async Task Another_project_is_told_the_launchs_own_number_and_every_try_carries_it()
    {
        // The process runs and numbers its readings while the other projects are told of it, one after another and each
        // tried again while its broker is held up: told any later number, a project that heard a reading first would take
        // the launch as newer and clear the work it shows.
        var said = new SaysWhatWasSent { HoldUpFirst = true };

        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel.Intercept(said)), "m1", TextWriter.Null);

        await reporter.ReportRunningAsync(Parley.Core.AgentIdentity.Mint(), "billing", "t-1", "/w", () => true, sequence: 7);

        Assert.Equal([7L, 7L], said.Launches.Select(l => l.Sequence));
    }

    [Fact]
    public async Task A_launch_tried_again_carries_the_number_taken_before_the_process_started()
    {
        var said = new SaysWhatWasSent { HoldUpFirst = true };

        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel.Intercept(said)), "m1", TextWriter.Null);

        _ = await reporter.LaunchAsync(Parley.Core.AgentName.Create("billing@m1"), sequence: 9);

        Assert.Equal([9L, 9L], said.Launches.Select(l => l.Sequence));
    }

    /// <summary>Answers each launch report at once, keeping what it said; the first held past its deadline when asked.</summary>
    private sealed class SaysWhatWasSent : Interceptor
    {
        public List<Wire.LaunchReport> Launches { get; } = [];

        /// <summary>Whether the first report is answered as held up past its deadline, so the reporter tries again.</summary>
        public bool HoldUpFirst { get; init; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            if (request is Wire.LaunchReport launch)
            {
                Launches.Add(launch);

                if (HoldUpFirst && Launches.Count == 1)
                {
                    var held = new RpcException(new Status(StatusCode.DeadlineExceeded, "held"));

                    return new AsyncUnaryCall<TResponse>(
                        Task.FromException<TResponse>(held),
                        Task.FromResult(new Metadata()),
                        () => held.Status,
                        () => [],
                        () => { });
                }

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
