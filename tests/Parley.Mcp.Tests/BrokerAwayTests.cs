using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent is told when the broker is not there.
/// </summary>
/// <remarks>
/// <para>
/// The failure the architecture introduced. The broker is a separate process: it can be stopped
/// for an upgrade, restarted by a service manager, or sitting on a machine that has gone away —
/// and while it is, every operation an agent attempts fails at the socket rather than at a rule.
/// </para>
/// <para>
/// SPEC.md §7.3 refuses to hand a model a refusal it cannot act on, on the grounds that
/// instructing a language model to remember something for later produces a token-burning loop or
/// silent abandonment. "An error occurred invoking 'parley_ask'" is exactly that refusal.
/// </para>
/// </remarks>
public sealed class BrokerAwayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it is mine I will fix it here; if not I will raise it with the operator.";

    [Fact]
    public async Task Every_tool_says_so_rather_than_failing_opaquely()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var fixture = new ServerFixture(T0);

        var (link, tools) = await fixture.LaunchAsync("orders@test", ct);

        // Attached, working, and then the broker goes away underneath it — which is what an
        // upgrade looks like from an agent's side.
        await fixture.StopBrokerAsync();

        foreach (var (name, call) in new (string, Func<Task<System.Text.Json.Nodes.JsonNode>>)[]
        {
            ("parley_agents", () => tools.AgentsAsync(ct)),
            ("parley_inbox", () => tools.InboxAsync(cancellationToken: ct)),
            ("parley_ask", () => tools.AskAsync(
                to: "billing@test",
                intent: "who owns tax",
                subQuestions: [new SubQuestion("q1", "Gross or net?")],
                consequences: Consequences,
                cancellationToken: ct)),
        })
        {
            var reply = await call();

            Assert.False(reply["ok"]!.GetValue<bool>(), name);

            var refused = reply["refused"]!.GetValue<string>();
            var why = reply["why"]!.GetValue<string>();

            // Both codes are correct here, and which one arrives is a race this test must not
            // pretend to control. A broker torn down while a call is draining releases the
            // attachment before the call finishes, so the call is *answered* -- with a rule
            // rather than a socket error. That is `brokerrefused`. A broker already gone refuses
            // at the socket, and that is `brokerunreachable`.
            //
            // Asserting the reachable-case wording here is what made this fail on loaded runners
            // and pass on every developer's machine. What both owe the agent is the same, so
            // that is what is asserted.
            Assert.Contains(refused, (string[])["brokerunreachable", "brokerrefused"], StringComparer.Ordinal);

            // The one thing a model must be able to conclude: the work did not happen.
            Assert.Contains("othing was sent", why, StringComparison.Ordinal);

            if (refused == "brokerunreachable")
            {
                // Nothing to act on but the deployment, so say so plainly and send them to the
                // person who can start it.
                Assert.Contains("not something you did", why, StringComparison.Ordinal);
                Assert.Contains("principal", why, StringComparison.Ordinal);
            }
        }

        await link.DisposeAsync();
    }

    [Fact]
    public async Task A_rule_the_broker_states_is_passed_on_rather_than_overruled()
    {
        // The defect this file was failing over, made deterministic. Start the call, then take
        // the broker down under it: the attach stream dies first, so the call is answered with
        // "not attached. Open an Attach stream before calling anything else". Fifteen runs out of
        // fifteen, where the same sequence with the shutdown first gave nought out of fifteen --
        // which is why a fast machine never sees this and CI does.
        //
        // That answer is a rule, and the remedy in it is the agent's to apply. It used to be
        // relabelled `brokerunreachable` and have "Tell your principal rather than retrying"
        // appended -- a refusal that named the fix and then forbade it. SPEC.md §7.3: a refusal
        // returns work, never a retry instruction.
        //
        // "Made deterministic" was overstated, and the failure it caused was itself silent
        // about the cause: 2 runs in 43 on a loaded Linux box, a NullReferenceException at
        // the first assertion. The call had won the race outright -- the broker listed the
        // agents and only then went down -- and a listing is a success, which carries no `ok`
        // at all. That outcome says nothing about the refusal this test exists for, so it is
        // retried on a fresh broker rather than asserted against, and the test fails only when
        // the race cannot be reached at all.
        var ct = TestContext.Current.CancellationToken;

        const int Attempts = 10;

        JsonNode reply;

        for (var attempt = 1; ; attempt++)
        {
            await using var fixture = new ServerFixture(T0);

            var (link, tools) = await fixture.LaunchAsync("orders@test", ct);

            var call = tools.AgentsAsync(ct);

            await fixture.StopBrokerAsync();

            reply = await call;

            await link.DisposeAsync();

            if (reply["ok"] is not null)
            {
                break;
            }

            // A success and not some third shape: this test must not quietly start accepting
            // whatever a future refusal looks like.
            Assert.NotNull(reply["agents"]);

            Assert.True(
                attempt < Attempts,
                $"the broker answered before it went down on all {Attempts} attempts, " +
                "so the refusal under test was never produced");
        }

        Assert.False(reply["ok"]!.GetValue<bool>());

        var why = reply["why"]!.GetValue<string>();

        Assert.Contains("othing was sent", why, StringComparison.Ordinal);

        // Never contentless, whichever way the race fell. The first attempt at this fix keyed
        // only on the status and handed back "Nothing was sent." and nothing else, because a call
        // cut off mid-shutdown carries no detail -- a refusal with nothing in it to act on, which
        // is the §7.3 failure the whole method exists to prevent.
        Assert.True(
            why.Length > "Nothing was sent. ".Length + 20,
            $"the refusal carries nothing to act on: '{why}'");

        if (reply["refused"]!.GetValue<string>() == "brokerrefused")
        {
            // A rule the broker stated. It survives intact, and nothing is appended that argues
            // with it -- the old text named the remedy and then said not to retry.
            Assert.Contains("Open an Attach stream", why, StringComparison.Ordinal);
            Assert.DoesNotContain("rather than retrying", why, StringComparison.Ordinal);
        }
        else
        {
            // The broker went away under the call. Nothing to act on but the deployment.
            Assert.Equal("brokerunreachable", reply["refused"]!.GetValue<string>());
            Assert.Contains("not something you did", why, StringComparison.Ordinal);
        }
    }
}
