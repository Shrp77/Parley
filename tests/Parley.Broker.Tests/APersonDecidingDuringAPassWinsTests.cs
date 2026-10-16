using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a person deciding an answer while Parley's pass decides it wins, and the pass leaves their decision alone.
/// </summary>
/// <remarks>
/// A pass reads answers back in one gated unit and accepts each in a
/// later one; settling by agent then overwrote a line a person had recorded in between with the agent's words, marked
/// automatic — or, if they had asked again, settled the new question with no answer and failed at the constraint.
/// Interleaved by calling the real operations in the order the race produces.
/// </remarks>
public sealed class APersonDecidingDuringAPassWinsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentName _parley;
    private readonly Facilitator _facilitator;
    private readonly Broker _agents;

    public APersonDecidingDuringAPassWinsTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-09-28T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'claude-code', 'full', 'active');
            """);
        _parley = new AgentRepository(_connection).EnsureFacilitatorAsync(ProjectName.Create("test")).GetAwaiter().GetResult();
        _facilitator = new Facilitator(_connection, BrokerOptions.Default);
        _agents = new Broker(_connection, BrokerOptions.Default);
        _agents.ServeFrom(T0);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<FacilitatorAnswer> AnsweredAndReadBack()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        var payload = new JsonObject
        {
            ["outcomes"] = new JsonArray(new JsonObject
            {
                ["id"] = PayloadShape.WholeRequest, ["outcome"] = "answered", ["text"] = "Orders, and tax."
            })
        }.ToJsonString();
        Assert.True((await _agents.ResultAsync(exchange, Orders, "m", payload, T0, Ct)).Succeeded);

        return Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));
    }

    [Fact]
    public async Task A_persons_line_recorded_while_the_pass_decides_is_not_overwritten_by_the_automatic_one()
    {
        // The serve pass: FacilitateAsync reads the answer back (one gated unit) ...
        var answer = await AnsweredAndReadBack();

        // ... a person, reading the discussion, edits and accepts it (OperatorsService.RecordOwnership) ...
        var record = await new OperatorConsole(_connection).RecordOwnershipAsync(
            Orders, new OwnershipAcceptance("Orders. Not tax.", OwnershipSource.Edited, 16, answer.Conversation), T0.AddSeconds(1), Ct);
        Assert.True(record.Recorded);
        Assert.True(await _facilitator.SettledByPersonAsync(_parley, Orders, T0.AddSeconds(1), cancellationToken: Ct));

        // ... and the pass's next gated unit accepts the agent's words automatically.
        var auto = await _facilitator.AcceptAutomaticallyAsync(_parley, answer, T0.AddSeconds(2), Ct);

        var line = _connection.QuerySingle<(string Statement, string Source)>(
            "SELECT statement, source FROM agent_ownership_accepted WHERE agent = 'orders@test';");
        TestContext.Current.SendDiagnosticMessage($"auto returned {auto}; recorded line '{line.Statement}' source {line.Source}");
        Assert.Equal("Orders. Not tax.", line.Statement);
        Assert.Equal("edited", line.Source);
    }

    [Fact]
    public async Task Asked_again_after_a_persons_accept_the_stale_automatic_accept_does_not_settle_the_new_question()
    {
        var answer = await AnsweredAndReadBack();

        _ = await new OperatorConsole(_connection).RecordOwnershipAsync(
            Orders, new OwnershipAcceptance("Orders. Not tax.", OwnershipSource.Edited, 16, answer.Conversation), T0.AddSeconds(1), Ct);
        _ = await _facilitator.SettledByPersonAsync(_parley, Orders, T0.AddSeconds(1), cancellationToken: Ct);
        Assert.True((await _facilitator.AskAgainAsync(_parley, Orders, T0.AddSeconds(2), Ct)).Done);

        Exception? thrown = null;
        bool auto = false;
        try
        {
            auto = await _facilitator.AcceptAutomaticallyAsync(_parley, answer, T0.AddSeconds(3), Ct);
        }
        catch (Exception e)
        {
            thrown = e;
        }

        var states = string.Join(",", _connection.Query<string>("SELECT state FROM ownership_questions ORDER BY id;"));
        var line = _connection.QuerySingle<(string Statement, string Source)>(
            "SELECT statement, source FROM agent_ownership_accepted WHERE agent = 'orders@test';");
        TestContext.Current.SendDiagnosticMessage(
            $"auto={auto} thrown={thrown?.GetType().Name}: {thrown?.Message}; states={states}; line='{line.Statement}' {line.Source}");

        Assert.Null(thrown);
        Assert.Equal("accepted,asked", states);
        Assert.Equal("edited", line.Source);
    }
}
