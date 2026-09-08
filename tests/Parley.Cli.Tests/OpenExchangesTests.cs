using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `parley open` names what a broker restart would interrupt.
/// </summary>
/// <remarks>
/// <para>
/// SC-003's first half. The install scripts ask this before they stop a broker, so what it reports
/// is what somebody is told before deciding — and reporting nothing is indistinguishable, from
/// their side, from there being nothing.
/// </para>
/// <para>
/// <b>The command exists because neither existing command answers the question.</b>
/// <c>parley waiting</c> counts messages queued for an agent, which is the opposite of the risk:
/// a question already drained into an inbox is marked delivered and no longer queued, so it sits
/// in §4.2's four-hour Working window with nothing running to answer it once the agent is killed.
/// That is exactly the exchange that ends as <c>ghosted</c>, and exactly the one
/// <c>waiting</c> would have said nothing about.
/// </para>
/// </remarks>
public sealed class OpenExchangesTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;

    public OpenExchangesTests() => _broker = LoopbackOperator.Start(_connection);

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task An_open_exchange_is_named_with_who_is_in_it()
    {
        // Registered first, because an exchange between agents the registry does not hold is
        // refused — which is §13's trust direction and not something to work around here.
        var agents = new AgentRepository(_connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken);
        }

        var broker = new Broker.Broker(_connection, BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);
        await broker.StartAsync(Billing, T0, TestContext.Current.CancellationToken);

        await broker.AskAsync(
            Orders,
            Billing,
            "some-model-9",
            // The consequences are forty characters at least, and the schema is what says so —
            // §9.3's argument that a rule of §8 belongs in storage, where no code path can route
            // around it. A shorter one here fails as a CHECK constraint rather than as a review
            // comment, which is the point.
            """
            {"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"a wrong answer ships the wrong total on every invoice"}
            """,
            T0.AddMinutes(1),
            // In a conversation, because that is what this command lists. An exchange opened
            // outside one is not reported, and that is the honest limit of the warning rather
            // than a defect here: §10.2's `conversations()` is what both surfaces read, and an
            // exchange belonging to no conversation is not on any of their screens either.
            conversationId: "pricing",
            cancellationToken: TestContext.Current.CancellationToken);

        var (code, said, error) = ToolProcess.Reading("open", "--broker", _broker.Address);

        Assert.True(code == 0, $"exit {code}: {error}");

        // The identifier and both participants, because the reader is deciding whether to stop a
        // broker and "one conversation" is not enough to decide on.
        Assert.Contains(Orders.Value, said, StringComparison.Ordinal);
        Assert.Contains(Billing.Value, said, StringComparison.Ordinal);

        var lines = said.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
    }

    [Fact]
    public void A_deployment_with_nothing_open_says_nothing_and_succeeds()
    {
        // The distinction the install script depends on: exit zero and no output means "asked, and
        // nothing is open", which is a different thing from a non-zero exit meaning "could not
        // ask". Collapsing the two is what would let a script imply nothing is in flight when it
        // never found out.
        var (code, said, _) = ToolProcess.Reading("open", "--broker", _broker.Address);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, said.Trim());
    }

    [Fact]
    public void A_broker_that_is_not_there_fails_rather_than_reporting_nothing()
    {
        // The other half, and the one that matters more. A command that exited zero and said
        // nothing when it could not reach the broker would make the install script report "nothing
        // in flight" for a deployment it never asked.
        var (code, said, _) = ToolProcess.Reading("open", "--broker", Loopback.NothingListens());

        Assert.NotEqual(0, code);
        Assert.Equal(string.Empty, said.Trim());
    }

    [Fact]
    public async Task It_discloses_what_a_surface_shows_and_nothing_more()
    {
        // **A new command on the operator side is a new disclosure question.** This one is safe
        // because it is the same §10.2 `conversations()` reply both surfaces already read, filtered
        // — but "the same query" is a claim about what it asks, not about what it prints, and what
        // it prints is what a script and a person see.
        //
        // So: identifiers and participants. Not the question, not the intent, not the consequences,
        // and not a single word of any message. The payload here carries a distinctive phrase; if
        // any of it reaches standard output, this fails.
        var agents = new AgentRepository(_connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken);
        }

        var broker = new Broker.Broker(_connection, BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken);
        await broker.StartAsync(Billing, T0, TestContext.Current.CancellationToken);

        await broker.AskAsync(
            Orders,
            Billing,
            "some-model-9",
            """
            {"intent":"HERRING-INTENT","sub_questions":[{"id":"q1","text":"HERRING-QUESTION"}],"consequences":"HERRING-CONSEQUENCE and enough characters to pass the cap"}
            """,
            T0.AddMinutes(1),
            conversationId: "pricing",
            cancellationToken: TestContext.Current.CancellationToken);

        var (code, said, _) = ToolProcess.Reading("open", "--broker", _broker.Address);

        Assert.Equal(0, code);

        foreach (var secret in new[]
                 {
                     "HERRING-INTENT", "HERRING-QUESTION", "HERRING-CONSEQUENCE", "some-model-9",
                 })
        {
            Assert.DoesNotContain(secret, said, StringComparison.Ordinal);
        }

        // And it did report the conversation, so the absence above is a filter rather than an
        // empty answer.
        Assert.Contains("pricing", said, StringComparison.Ordinal);
    }
}
