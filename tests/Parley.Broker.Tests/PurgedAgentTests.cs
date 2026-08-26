using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What the rest of the deployment sees after an operator purges an agent, per SPEC.md §14.
/// </summary>
/// <remarks>
/// The store tests fix what purge deletes. This fixes the part an operator actually feels:
/// nothing stops a purge landing while other servers are live and still holding the name, and a
/// question aimed at the gap must not surface as a fault in somebody else's session.
/// </remarks>
public sealed class PurgedAgentTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it does I read it from you; if not I snapshot my own copy and we can diverge.";

    private static readonly string Question =
        $$"""{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"{{Consequences}}"}""";

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public PurgedAgentTests()
    {
        _agents = new AgentRepository(_connection);
        _broker = new Broker(_connection, BrokerOptions.Default);

        foreach (var name in new[] { Orders, Billing })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_question_aimed_at_a_purged_agent_is_refused_the_ordinary_way()
    {
        // Nothing stops an operator purging an agent whose peers are mid-session and still hold
        // its name, so this is the case that decides whether purge is safe to offer at all. The
        // answer has to be the refusal every other absence produces — not a foreign key error
        // surfacing as a crash in somebody else's tool call, which is what deleting a row out
        // from under a live deployment could plausibly have done.
        await _broker.StartAsync(Orders, T0, Ct);
        await _broker.StartAsync(Billing, T0, Ct);

        Assert.Equal(
            PurgeVerdict.Gone,
            (await _agents.PurgeAsync(Billing, cancellationToken: Ct)).Verdict);

        var asked = await _broker.AskAsync(
            Orders, Billing, "some-model-9", Question, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.False(asked.Succeeded);
        Assert.Equal(OpenRejection.AnswererUnreachable, asked.Rejection);
        Assert.Null(asked.Exchange);
    }
}
