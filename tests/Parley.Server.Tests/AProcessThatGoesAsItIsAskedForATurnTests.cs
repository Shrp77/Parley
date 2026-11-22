using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That work which reaches a stopped agent in the instant its process goes is still started for (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// A wake looks to see whether the agent is attached, asks its node for a turn, and only then notes what it asked. The
/// stopped process can go in between. Its detach then finds nothing noted and does nothing, and the wake has asked a turn
/// of a process that no longer exists: the message was left unread, and the claim the wake held told every later wake for
/// two minutes that a turn was coming (review, lens 1 on 0.64.8: 3 in 32,000 races).
/// </para>
/// <para>
/// <b>Held open with a clock, because it is a race.</b> The wake reads the clock once between asking for the turn and
/// noting it, so a clock that waits there until the process has gone puts the two in the losing order every time.
/// </para>
/// </remarks>
[Integration]
public sealed class AProcessThatGoesAsItIsAskedForATurnTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_work_is_asked_for_again_as_a_start_by_the_wake_that_found_the_process_gone()
    {
        using var clock = new HeldInTheWake();

        await using var broker = await LoopbackBroker.StartAsync(Ct, clock);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = "t-1" }, cancellationToken: Ct);

        var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "t-1");
        var gone = false;

        try
        {
            _ = await broker.ConnectAsOperator().StopAsync(new Wire.StopRequest { Agent = Agent }, cancellationToken: Ct);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Stop, (await node.WaitForCommandAsync(Patience)).CommandCase);

            clock.Arm();

            var sending = docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "as it goes", "c1"), cancellationToken: Ct).ResponseAsync;

            // The wake has asked for its turn and is held before it notes that: the node has the turn, and the wake waits.
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(Patience)).CommandCase);
            Assert.True(clock.Held.Wait(Patience, Ct), "the wake never reached the clock after asking for its turn");

            // The process goes, wholly, while the wake is held: its detach finds nothing noted.
            await agent.DisposeAsync();
            gone = true;
            await broker.WaitUntilDetachedAsync(Agent, Patience);

            clock.LetGo();

            var sent = await sending.WaitAsync(Patience, Ct);

            Assert.Equal(Wire.WakeOutcome.Started, Assert.Single(sent.Outcomes).Wake);

            var command = await node.WaitForCommandAsync(Patience);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
            Assert.Equal(Agent, command.Start.Agent);

            // Once, and the claim it holds is the start's own: a second message is told it is coming and asks nothing more.
            var again = await docs.Client.RequestAsync(Requests.Open("docs@m1", Agent, "and again", "c2"), cancellationToken: Ct);

            Assert.Equal(Wire.WakeOutcome.AlreadyComing, Assert.Single(again.Outcomes).Wake);
            await Assert.ThrowsAsync<TimeoutException>(() => node.WaitForCommandAsync(TimeSpan.FromMilliseconds(400)));
        }
        finally
        {
            clock.LetGo();

            if (!gone)
            {
                await agent.DisposeAsync();
            }
        }
    }

    /// <summary>The system clock, which waits once, when armed, at the read a wake makes after asking a node.</summary>
    private sealed class HeldInTheWake : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _letGo = new(false);
        private int _armed;

        /// <summary>Gets what is set when a wake is being held.</summary>
        public ManualResetEventSlim Held { get; } = new(false);

        /// <summary>Holds the next wake at its clock.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Lets the held wake go on.</summary>
        public void LetGo() => _letGo.Set();

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            // Told apart by who asks: the wake's own method is the only reader of the clock between asking a node and
            // noting what was asked. Looked at only while armed, so no other read pays for it.
            if (Volatile.Read(ref _armed) == 1
                && Environment.StackTrace.Contains("BrokerRuntime.Ask(", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Held.Set();
                _ = _letGo.Wait(TimeSpan.FromSeconds(20));
            }

            return System.GetUtcNow();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _letGo.Dispose();
            Held.Dispose();
        }
    }
}
