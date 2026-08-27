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
            Assert.Equal("brokerunreachable", reply["refused"]!.GetValue<string>());

            var why = reply["why"]!.GetValue<string>();

            // Three things a model needs and one it must not conclude: that nothing was sent,
            // that this is not its fault, that somebody should be told, and — by omission — no
            // suggestion that rephrasing would help.
            Assert.Contains("nothing was sent", why, StringComparison.Ordinal);
            Assert.Contains("not something you did", why, StringComparison.Ordinal);
            Assert.Contains("principal", why, StringComparison.Ordinal);
        }

        await link.DisposeAsync();
    }
}
