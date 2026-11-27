using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That a registry's background work is held to the broker's rules again as it is read off the wire
/// (<c>BrokerClient.RegistryAsync</c>, SPEC.md §11.6): the count to its bound, a task's id to the runner's shape, its kind
/// read, and its name and description neutralised.
/// </summary>
/// <remarks>
/// Every surface draws what this returns, and a broker that is not this build is not trusted to have done it. No test fed
/// the client a row the broker would never send, so dropping any one of these checks passed: every stand-in broker in the
/// other tests sends only rows the broker already held.
/// </remarks>
[Unit]
public sealed class TheWiresTasksAreHeldAgainTests
{
    private const string BadName = "Check\u0007VAT‮rules⁦now";
    private const string BadDescription = "Reads\u001B[2Jthe‏ledger\u0085twice";

    [Fact]
    public async Task A_count_past_the_bound_is_held_to_it()
    {
        var turns = (await Read()).Turns!;

        Assert.Equal(10_000, turns.BackgroundTasks);
    }

    [Fact]
    public async Task A_task_id_the_runner_could_not_have_given_is_emptied()
    {
        var tasks = (await Read()).Turns!.Named;

        Assert.Equal(string.Empty, tasks[0].Id);
        Assert.Equal("t-2_ok", tasks[1].Id);
    }

    [Fact]
    public async Task A_command_reads_as_a_command()
    {
        var tasks = (await Read()).Turns!.Named;

        Assert.Equal(TaskKind.Agent, tasks[0].Kind);
        Assert.Equal(TaskKind.Command, tasks[1].Kind);
    }

    [Fact]
    public async Task A_name_and_description_are_neutralised()
    {
        var task = (await Read()).Turns!.Named[0];

        Assert.Equal(BackgroundTaskText.Name(BadName).Text, task.Name);
        Assert.Equal(BackgroundTaskText.Description(BadDescription).Text, task.Description);
        Assert.NotEqual(BadName, task.Name);
        Assert.NotEqual(BadDescription, task.Description);
        Assert.DoesNotContain(task.Name + task.Description, c => char.IsControl(c) || c is '‮' or '⁦' or '‏');
    }

    private static async Task<AgentRecord> Read()
    {
        var row = new Wire.RegistryRow
        {
            Name = "orders@pc",
            Adapter = "claude-code",
            Tier = Wire.AgentTier.Headless,
            Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running,
            TurnsReported = true,
            LastTurnClean = true,
            BackgroundTasks = 2_000_000,
        };
        row.RunningTasks.Add(new Wire.BackgroundTaskRow
        {
            Id = "t1; rm -rf",
            Kind = Wire.TaskKind.Agent,
            Name = BadName,
            Description = BadDescription,
        });
        row.RunningTasks.Add(new Wire.BackgroundTaskRow { Id = "t-2_ok", Kind = Wire.TaskKind.Command, Name = "Build" });

        var reply = new Wire.RegistryReply();
        reply.Rows.Add(row);
        var broker = new BrokerClient(new Wire.Operators.OperatorsClient(new Answering(reply)));

        return Assert.Single(await broker.RegistryAsync(TestContext.Current.CancellationToken));
    }

    private sealed class Answering(object reply) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            new(
                Task.FromResult((TResponse)reply),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }
}
