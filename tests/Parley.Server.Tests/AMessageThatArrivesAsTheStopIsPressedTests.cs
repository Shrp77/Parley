using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a message whose wake crosses a person's Stop is not left unread with nothing coming (SPEC.md §10.2 <c>stop</c>:
/// "the next work for it starts it again").
/// </summary>
/// <remarks>
/// <para>
/// A stop forgets the turns asked before it. It once did so after its node had been asked, so the note of a turn that
/// reached the node behind the stop could be forgotten with the rest: the turn went to a process the stop ended, nothing
/// held it as lost, and the sender had been told the agent was running (review, lens 1 on 0.64.9: 11 lost in 30,000 over
/// the wire, and most of the crossings when the two are released together). The forgetting now comes before the asking.
/// </para>
/// <para>
/// <b>Many agents released on one barrier, because it is a race.</b> About a third of the pairs land as a stop and then a
/// turn. Every one of those must be followed by a start once the process has gone, and a test that saw none of them has
/// tested nothing, so it says so. It is a race, so a pass narrows the odds of the fault returning unseen and does not
/// close them.
/// </para>
/// </remarks>
[Integration]
public sealed class AMessageThatArrivesAsTheStopIsPressedTests
{
    private const string Here = "m1";
    private const int Agents = 40;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_turn_that_reached_the_node_behind_the_stop_is_followed_by_a_start()
    {
        var roles = Enumerable.Range(0, Agents).Select(i => $"worker{i}").ToArray();
        var names = roles.Select(role => $"{role}@{Here}").ToArray();

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, roles, Ct, reportsTurns: true);
        await using var docs = await AttachedClient.AttachAsync(broker, $"docs@{Here}", cancellationToken: Ct);

        var sessions = new List<AttachedClient>();
        var read = new Read { Upto = node.Commands.Count };

        try
        {
            foreach (var (name, at) in names.Select((name, at) => (name, at)))
            {
                _ = await node.Nodes.ReportLaunchAsync(
                    new Wire.LaunchReport { Machine = Here, Agent = name, Token = "t-" + name }, cancellationToken: Ct);

                sessions.Add(await AttachedClient.AttachAsync(broker, name, cancellationToken: Ct, launchToken: "t-" + name));

                // Mail that stays uncollected, and its wake's claim let go, so the wake in the race below is asked afresh.
                var sent = await docs.Client.RequestAsync(
                    Requests.Open($"docs@{Here}", name, "waiting", "c" + at.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    cancellationToken: Ct);

                Assert.True(Assert.Single(sent.Outcomes).Ok);
                broker.Runtime.Collected(AgentName.Create(name));
            }

            // The turn each of those messages asked for.
            var before = await DrainAsync(node, expected: Agents, read);

            Assert.All(names, name => Assert.Equal(["Turn"], before.GetValueOrDefault(name, [])));

            // The stop and the wake, released together for each agent. On threads of their own: this many blocked on a barrier
            // would wait for the pool to grow, a thread a second, and the pairs would no longer leave together.
            using var barrier = new Barrier(2 * Agents);

            // Each wake leaves a little later than the one before, from none to a few milliseconds, so that whatever the
            // machine's speed some cross their stop part-way through it and some arrive behind it.
            var racing = names.SelectMany((name, at) => new[]
            {
                new Thread(() =>
                {
                    barrier.SignalAndWait(Patience);
                    _ = broker.Runtime.RequestStop(AgentName.Create(name), restart: false, "raced");
                }),
                new Thread(() =>
                {
                    barrier.SignalAndWait(Patience);

                    var held = System.Diagnostics.Stopwatch.StartNew();

                    while (held.Elapsed < TimeSpan.FromMicroseconds(at * 100))
                    {
                        Thread.SpinWait(20);
                    }

                    _ = broker.Runtime.RequestAttentionForMail(AgentName.Create(name), "raced");
                }),
            }).ToList();

            racing.ForEach(thread => thread.Start());
            racing.ForEach(thread => Assert.True(thread.Join(Patience), "a racing thread did not finish"));

            var raced = await DrainAsync(node, expected: 2 * Agents, read);

            // The processes go, together: forty closed one after another is most of a minute.
            await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));

            sessions.Clear();

            foreach (var name in names)
            {
                await broker.WaitUntilDetachedAsync(name, Patience);
            }

            var behind = names.Where(name => raced.GetValueOrDefault(name, []) is ["Stop", "Turn"]).ToList();

            var after = await DrainAsync(node, expected: behind.Count, read);

            Assert.True(behind.Count > 0, $"no wake reached the node behind its stop in {Agents} pairs, so nothing was tested");

            Assert.All(behind, name => Assert.Equal(["Start"], after.GetValueOrDefault(name, [])));

            // And nobody is started twice. A turn that reached the node ahead of the stop may be followed by a start too: its
            // note may have been written after the stop's clearing, and its mail is still uncollected, so the broker cannot
            // know the turn was taken.
            Assert.All(names, name => Assert.True(after.GetValueOrDefault(name, []).Count <= 1, $"{name} was started more than once"));
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>Reads what has reached the node since the last reading, once as many as expected have come and none follows.</summary>
    private static async Task<Dictionary<string, List<string>>> DrainAsync(JoinedNodeClient node, int expected, Read read)
    {
        var deadline = DateTime.UtcNow + Patience;

        // Until they are all in, then once more after a pause that finds nothing new.
        while (node.Commands.Count - read.Upto < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        int count;

        do
        {
            count = node.Commands.Count;
            await Task.Delay(500, Ct);
        }
        while (node.Commands.Count != count);

        var commands = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var command in node.Commands.Skip(read.Upto))
        {
            var (agent, kind) = command.CommandCase switch
            {
                Wire.NodeCommand.CommandOneofCase.Start => (command.Start.Agent, "Start"),
                Wire.NodeCommand.CommandOneofCase.Turn => (command.Turn.Agent, "Turn"),
                Wire.NodeCommand.CommandOneofCase.Stop => (command.Stop.Agent, "Stop"),
                _ => (string.Empty, command.CommandCase.ToString()),
            };

            if (!commands.TryGetValue(agent, out var list))
            {
                commands[agent] = list = [];
            }

            list.Add(kind);
        }

        read.Upto = count;

        return commands;
    }

    /// <summary>How far into the node's commands a test has read.</summary>
    private sealed class Read
    {
        /// <summary>Gets or sets how many commands have been read.</summary>
        public int Upto { get; set; }
    }
}
