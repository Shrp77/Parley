using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That an open question from the facilitator does not decide where an agent's own work goes (SPEC.md principle 2.10,
/// research R13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by <c>/speckit-analyze</c> rather than by use, which is the only reason it did not ship.</b> The broker
/// assigns a Request to the conversation its sender holds a request in. An agent that had asked Parley something back
/// holds its question indefinitely, so without this every Request it sent would have gone into its ownership
/// conversation — the facilitator steering work, which principle 2.11 forbids. Watched to fail without the filter.
/// </para>
/// </remarks>
public sealed class ParleyChoosesNoConversationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public ParleyChoosesNoConversationTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-09-28T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active');
            """);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_question_asked_back_does_not_pull_the_agents_own_work_into_it()
    {
        var parley = await new AgentRepository(_connection).EnsureFacilitatorAsync("test", cancellationToken: Ct);
        var agents = new Broker(_connection, BrokerOptions.Default);
        agents.ServeFrom(T0);

        var question = await new Facilitator(_connection, BrokerOptions.Default).AskAsync(parley, Orders, T0, Ct);

        Assert.True((await agents.ClarifyAsync(
            question.Exchange!.Value, Orders, "some-model-9", Json("text", "Which repository?"), T0, Ct)).Succeeded);

        // orders holds no request but Parley's, so a Request naming nothing starts a conversation of its own — exactly
        // as it would with no question open.
        var sent = await agents.RequestAsync(Orders, Billing, "some-model-9", Json("ask", "Is tax yours?"), T0, null, Ct);

        Assert.True(sent.Succeeded);

        var landed = _connection.ExecuteScalar<string>(
            "SELECT conversation_id FROM exchanges WHERE id = @Id;", new { Id = sent.Exchange!.Id });

        Assert.NotEqual(question.Conversation, landed);
        Assert.DoesNotContain(Facilitation.ConversationPrefix, landed, StringComparison.Ordinal);
    }

    private static string Json(string field, string text) => new JsonObject { [field] = text }.ToJsonString();
}
