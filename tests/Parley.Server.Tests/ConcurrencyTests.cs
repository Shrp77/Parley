using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Many clients on the wire at once, against the one connection they share.
/// </summary>
/// <remarks>
/// <para>
/// <b>The architecture's central claim is a concurrency claim, and nothing was testing it.</b>
/// <c>BrokerRuntime</c> holds a single <c>SqliteConnection</c> and states plainly that it is not
/// thread-safe, so every operation queues behind one gate. Every other test in this project
/// drives one client at a time, which is exactly the shape that cannot tell a held gate from an
/// absent one: a missed acquisition is invisible until two callers are inside it together.
/// </para>
/// <para>
/// So these drive real gRPC clients concurrently and assert on what the store holds afterwards.
/// A dropped write, a duplicated row, or a connection used from two threads shows up as a
/// count that is not the count asked for -- or as an exception the gate exists to prevent, which
/// is why nothing here catches one.
/// </para>
/// <para>
/// They are not load tests and the numbers are deliberately small. The property is
/// "simultaneous work is serialised correctly", which does not get truer at a thousand callers;
/// what a large number would add is runtime on three CI platforms and a flake when one of them
/// is slow.
/// </para>
/// </remarks>
public sealed class ConcurrencyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string Consequences =
        "If the answer is yes I will record it and stop asking; if no I will raise it with the " +
        "operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    [Fact]
    public async Task Every_agent_asking_at_the_same_moment_is_admitted_exactly_once()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var infra = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/repo-infra", token);

        // Attached one at a time rather than concurrently: this test is about concurrent asks,
        // and an attach storm is the test below. Mixing them would leave a failure ambiguous.
        var askers = new List<AttachedClient>();

        for (var i = 0; i < 8; i++)
        {
            askers.Add(await AttachedClient.AttachAsync(
                broker, $"docs{i}@alpha", $"C:/repo-docs-{i}", token));
        }

        try
        {
            // Released together. Starting the calls in a loop and awaiting each would serialise
            // them at the client and test nothing -- the gate would never see two callers.
            var release = new TaskCompletionSource();

            var asks = askers.Select(asker => Task.Run(
                async () =>
                {
                    await release.Task.ConfigureAwait(false);

                    return await asker.Client.AskAsync(
                        new Wire.AskRequest
                        {
                            From = asker.Name,
                            To = infra.Name,
                            ResponderModel = "some-model-9",
                            PayloadJson = Question($"who owns the retry policy, asked by {asker.Name}")
                        },
                        cancellationToken: token);
                },
                token)).ToList();

            release.SetResult();

            var outcomes = await Task.WhenAll(asks);

            Assert.All(outcomes, outcome => Assert.True(outcome.Ok));

            // Distinct, because an exchange identifier collision under concurrency is the defect
            // this shape of test exists to catch and would otherwise surface much later as two
            // conversations sharing a history.
            Assert.Equal(
                outcomes.Length,
                outcomes.Select(outcome => outcome.ExchangeId).Distinct().Count());

            // The store's account of it, read once every ask has returned. Eight questions were
            // asked, so eight are waiting: fewer means a write was lost inside the gate, more
            // means one was applied twice.
            var inbox = await infra.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token);

            Assert.Equal(askers.Count, inbox.Messages.Count);

            Assert.Equal(
                askers.Select(asker => asker.Name).OrderBy(name => name, StringComparer.Ordinal),
                inbox.Messages.Select(message => message.Message.From).OrderBy(name => name, StringComparer.Ordinal));
        }
        finally
        {
            foreach (var asker in askers)
            {
                await asker.DisposeAsync();
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

                return await AttachedClient.AttachAsync(
                    broker, $"docs{i}@alpha", $"C:/repo-docs-{i}", token);
            },
            token)).ToList();

        release.SetResult();

        var attached = await Task.WhenAll(attaches);

        try
        {
            // Registration happens inside the attach, under the gate, and it is the write most
            // exposed to a race: it reads whether the name is held and then inserts. Eight names
            // registering together must produce eight agents and not seven with one silently
            // folded into another.
            var roster = await attached[0].Client.ListAgentsAsync(
                new Wire.ListAgentsRequest { AskingAs = attached[0].Name }, cancellationToken: token);

            foreach (var client in attached)
            {
                Assert.Contains(
                    roster.Agents,
                    agent => string.Equals(agent.Name, client.Name, StringComparison.Ordinal));
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
    public async Task Two_sessions_of_one_agent_asking_together_do_not_collide()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var infra = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/repo-infra", token);

        // SPEC.md §14: two windows open on one repository are two sessions for one agent, and
        // `connected` counts them. Both hold their own stream and both may call at once, so the
        // runtime picking one broker per operation has to be safe against the other picking the
        // same one mid-flight.
        await using var first = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        await using var second = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        var release = new TaskCompletionSource();

        var asks = new[] { first, second }.Select(session => Task.Run(
            async () =>
            {
                await release.Task.ConfigureAwait(false);

                return await session.Client.AskAsync(
                    new Wire.AskRequest
                    {
                        From = session.Name,
                        To = infra.Name,
                        ResponderModel = "some-model-9",

                        // Distinct per session, deliberately. Identical text is refused by the
                        // distinctness rule of SPEC.md §7.1, which admitted exactly one of the
                        // two and left this asserting that one identifier is distinct from
                        // itself -- a test that passed without ever exercising two callers.
                        PayloadJson = Question($"who owns the retry policy, per {session.GetHashCode()}")
                    },
                    cancellationToken: token);
            },
            token)).ToList();

        release.SetResult();

        var outcomes = await Task.WhenAll(asks);

        // Whatever the admission rules decide about a second question on one pair, the two
        // sessions must not both be told they succeeded with the same exchange. Asserting on the
        // outcomes rather than on a count keeps this a concurrency test rather than a restatement
        // of §6's admission rules, which their own tests cover.
        var admitted = outcomes.Where(outcome => outcome.Ok).ToList();

        Assert.Equal(2, admitted.Count);

        Assert.Equal(
            admitted.Count,
            admitted.Select(outcome => outcome.ExchangeId).Distinct().Count());
    }
}
