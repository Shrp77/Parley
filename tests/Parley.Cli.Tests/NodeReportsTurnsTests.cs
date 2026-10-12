using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Parley.Headless;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node's turns reach the broker, over a real socket, and that reporting them never costs the turn (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// <b>The defect</b>: an agent's command was refused for want of approval, its turn ended without a word, and
/// the room said <i>working</i> for nearly three hours. The node had measured the turn and told nobody. These drive the
/// node's own reporter against a loopback broker and read the result back through the operator's registry.
/// </remarks>
public sealed class NodeReportsTurnsTests
{
    private const string Machine = "m1";
    private static readonly AgentName Billing = AgentName.Create("billing@m1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TurnOutcome Outcome(bool completed = true, int denials = 0) =>
        new(completed, false, "end_turn", TimeSpan.FromSeconds(42), 0.01, 3, denials, false, false);

    private static async Task<(LoopbackOperator Broker, Wire.Nodes.NodesClient Nodes, IAsyncDisposable Joined)> JoinedAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Billing, "mcp", AgentTier.Headless, null, null, null, false, AgentStatus.Active), Ct);

        var broker = LoopbackOperator.Start(connection);
        var nodes = new Wire.Nodes.NodesClient(GrpcChannel.ForAddress(broker.Address));
        var join = nodes.Join(
            new Wire.JoinRequest { Machine = Machine, ReportsTurns = true, StartableRoles = { "billing" } }, cancellationToken: Ct);

        Assert.True(await join.ResponseStream.MoveNext(Ct), "the node was not acknowledged");

        return (broker, nodes, new Disposing(join));
    }

    private static async Task<TurnStanding> StandingAsync(LoopbackOperator broker) =>
        (await new BrokerClient(broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == Billing).Turns!;

    [Fact]
    public async Task A_turn_reads_as_running_while_it_runs_and_then_as_ended_with_what_it_was_refused()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        var (broker, nodes, joined) = await JoinedAsync(connection);

        await using (broker)
        await using (joined)
        {
            TurnStanding? during = null;

            var outcome = await new TurnReporter(nodes, Machine, TextWriter.Null).RunAsync(Billing, async began =>
            {
                await began();

                during = await StandingAsync(broker);

                return Outcome(denials: 2);
            });

            Assert.Equal(2, outcome.PermissionDenials);

            Assert.True(during!.Reported);
            Assert.NotNull(during.RunningSince);

            var after = await StandingAsync(broker);

            Assert.Null(after.RunningSince);
            Assert.NotNull(after.LastEnded);
            Assert.Equal(2, after.LastPermissionDenials);
            Assert.True(after.LastClean);
        }
    }

    [Fact]
    public async Task A_turn_that_throws_still_has_its_end_reported()
    {
        // Otherwise the broker holds a turn that will never end, and the agent reads as working for ever.
        using var connection = ParleyDatabase.OpenInMemory();
        var (broker, nodes, joined) = await JoinedAsync(connection);

        await using (broker)
        await using (joined)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TurnReporter(nodes, Machine, TextWriter.Null).RunAsync(
                    Billing, async began =>
                    {
                        await began();

                        throw new InvalidOperationException("the runner fell over");
                    }));

            var after = await StandingAsync(broker);

            Assert.Null(after.RunningSince);
            Assert.NotNull(after.LastEnded);
            Assert.False(after.LastClean);
        }
    }

    [Fact]
    public async Task A_turn_is_reported_from_when_it_begins_not_from_when_it_was_asked_for()
    {
        // The review gate's finding: a turn queued behind another was reported as starting when it was asked for, so its
        // end — start plus the time it ran — landed before the work it took, and a stop on a refused permission was never
        // flagged. The wait here stands in for the agent's turn lock.
        using var connection = ParleyDatabase.OpenInMemory();
        var (broker, nodes, joined) = await JoinedAsync(connection);

        await using (broker)
        await using (joined)
        {
            var asked = DateTimeOffset.UtcNow;
            TurnStanding? during = null;

            _ = await new TurnReporter(nodes, Machine, TextWriter.Null).RunAsync(Billing, async began =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

                during = await StandingAsync(broker);

                await began();

                return Outcome();
            });

            Assert.Null(during!.RunningSince);

            var after = await StandingAsync(broker);

            Assert.True(
                after.LastEnded >= asked.AddMilliseconds(900),
                $"the end was put at {after.LastEnded:O}, though the turn began a second after {asked:O}");
        }
    }

    [Fact]
    public async Task A_turn_that_never_began_is_not_reported()
    {
        // Cancelled while it queued: nothing ran, so there is no turn to record.
        using var connection = ParleyDatabase.OpenInMemory();
        var (broker, nodes, joined) = await JoinedAsync(connection);

        await using (broker)
        await using (joined)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new TurnReporter(nodes, Machine, TextWriter.Null).RunAsync(
                    Billing, _ => throw new OperationCanceledException()));

            var after = await StandingAsync(broker);

            Assert.Null(after.RunningSince);
            Assert.Null(after.LastEnded);
        }
    }

    [Fact]
    public async Task A_broker_that_cannot_be_reached_never_fails_the_turn()
    {
        var nowhere = new Wire.Nodes.NodesClient(GrpcChannel.ForAddress("http://127.0.0.1:1"));
        var said = new StringWriter();

        var outcome = await new TurnReporter(nowhere, Machine, said).RunAsync(Billing, async began =>
        {
            await began();

            return Outcome();
        });

        Assert.True(outcome.Succeeded);
        Assert.Contains("could not report its turn", said.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Ends a node's join stream when disposed.</summary>
    private sealed class Disposing(IDisposable join) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            join.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
