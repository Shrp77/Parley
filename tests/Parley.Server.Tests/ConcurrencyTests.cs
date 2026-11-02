using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Many clients on the wire at once, against the one connection they share.
/// </summary>
/// <remarks>
/// <para>
/// <b>The architecture's central claim is a concurrency claim.</b> <c>BrokerRuntime</c> holds a single
/// <c>SqliteConnection</c> that is not thread-safe, so every operation queues behind one gate. A test driving one
/// client at a time cannot tell a held gate from an absent one.
/// </para>
/// <para>
/// So these drive real gRPC clients concurrently and assert on what the store holds afterwards. A dropped write, a
/// duplicated row, or a connection used from two threads shows up as a count that is not the count asked for.
/// </para>
/// </remarks>
[Integration]
public sealed class ConcurrencyTests
{
    [Fact]
    public async Task Every_agent_sending_at_the_same_moment_is_admitted_exactly_once()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        // Attached one at a time: this test is about concurrent Requests, and an attach storm is the test below.
        var requesters = new List<AttachedClient>();

        for (var i = 0; i < 8; i++)
        {
            requesters.Add(await AttachedClient.AttachAsync(broker, $"docs{i}@alpha", $"C:/repo-docs-{i}", token));
        }

        try
        {
            // Released together. Awaiting each in a loop would serialise them at the client and test nothing.
            var release = new TaskCompletionSource();

            var sends = requesters.Select(requester => Task.Run(
                async () =>
                {
                    await release.Task.ConfigureAwait(false);

                    return Assert.Single((await requester.Client.RequestAsync(
                        Requests.Open(requester.Name, infra.Name, $"who owns the retry policy, asked by {requester.Name}"),
                        cancellationToken: token)).Outcomes);
                },
                token)).ToList();

            release.SetResult();

            var outcomes = await Task.WhenAll(sends);

            Assert.All(outcomes, outcome => Assert.True(outcome.Ok));
            Assert.Equal(outcomes.Length, outcomes.Select(outcome => outcome.ExchangeId).Distinct().Count());

            // Eight Requests were sent, so eight are waiting: fewer means a write was lost inside the gate, more means
            // one was applied twice.
            var inbox = await infra.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token);

            Assert.Equal(requesters.Count, inbox.Messages.Count);
            Assert.Equal(
                requesters.Select(r => r.Name).OrderBy(name => name, StringComparer.Ordinal),
                inbox.Messages.Select(m => m.Message.From).OrderBy(name => name, StringComparer.Ordinal));
        }
        finally
        {
            foreach (var requester in requesters)
            {
                await requester.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Agents_attaching_at_the_same_moment_all_register()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var release = new TaskCompletionSource();

        var attaches = Enumerable.Range(0, 8).Select(i => Task.Run(
            async () =>
            {
                await release.Task.ConfigureAwait(false);
                return await AttachedClient.AttachAsync(broker, $"docs{i}@alpha", $"C:/repo-docs-{i}", token);
            },
            token)).ToList();

        release.SetResult();

        var attached = await Task.WhenAll(attaches);

        try
        {
            // Registration reads whether the name is held and then inserts, under the gate. Eight names registering
            // together must produce eight agents and not seven with one silently folded into another.
            var roster = await attached[0].Client.ListAgentsAsync(new Wire.ListAgentsRequest { AskingAs = attached[0].Name }, cancellationToken: token);

            foreach (var client in attached)
            {
                Assert.Contains(roster.Agents, agent => string.Equals(agent.Name, client.Name, StringComparison.Ordinal));
            }
        }
        finally
        {
            foreach (var client in attached)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Two_sessions_of_one_agent_sending_together_do_not_collide()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        // Two windows open on one repository are two sessions for one agent (§14), and both may call at once.
        await using var first = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var second = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        var release = new TaskCompletionSource();

        var sends = new[] { first, second }.Select((session, index) => Task.Run(
            async () =>
            {
                await release.Task.ConfigureAwait(false);

                // Distinct per session, deliberately: identical text is refused by distinctness, which would admit
                // exactly one and leave this asserting that one identifier is distinct from itself.
                return Assert.Single((await session.Client.RequestAsync(
                    Requests.Open(session.Name, infra.Name, $"who owns the retry policy, session {index}"),
                    cancellationToken: token)).Outcomes);
            },
            token)).ToList();

        release.SetResult();

        var admitted = (await Task.WhenAll(sends)).Where(outcome => outcome.Ok).ToList();

        Assert.Equal(2, admitted.Count);
        Assert.Equal(2, admitted.Select(outcome => outcome.ExchangeId).Distinct().Count());
    }
}
