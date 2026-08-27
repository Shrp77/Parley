using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// A question addressed to a role rather than to one agent, per SPEC.md §5.5.
/// </summary>
/// <remarks>
/// <para>
/// The whole design is that this is not a new kind of exchange. N ordinary ones are opened and
/// everything downstream — admission, clarification, orphaning, ghosting — treats them exactly
/// as it would have treated each on its own. So most of what is pinned here is the restraint:
/// that the group binds nothing, that a refusal for one member is not a refusal for the rest,
/// and that responders cannot tell they were asked as part of a crowd.
/// </para>
/// </remarks>
public sealed class RoleQuestionTests : IDisposable
{
    private const string Model = "some-model-9";

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AgentName Docs = AgentName.Create("docs@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;

    public RoleQuestionTests()
    {
        _broker = new Broker(_connection, BrokerOptions.Default);

        // Three machines running the same role, which is the case the naming scheme exists for:
        // one repository checked out in three places, each able to execute a different part of
        // what they all own.
        foreach (var name in new[] { "docs@test", "infra@alpha", "infra@beta", "infra@gamma" })
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO agents (name, adapter, tier, status) " +
                "VALUES ($name, 'test', 'pull', 'active');";
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }

        _broker.StartAsync(Docs, T0, TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private const string Consequences =
        "If the answer is yes I will record it in BOUNDARIES.md and stop asking; if no I will " +
        "raise it with the operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    private Task<IReadOnlyList<AskOutcome>> AskRole(string role) =>
        _broker.AskRoleAsync(
            Docs, role, Model, Question("who owns the domain"), T0.AddMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_role_opens_one_exchange_per_agent_under_it()
    {
        var outcomes = await AskRole("infra");

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Succeeded));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM exchanges;"));
    }

    [Fact]
    public async Task They_are_bound_by_a_group_and_by_nothing_else()
    {
        await AskRole("infra");

        // One group across all three, so the asker can tell which answers are comparable.
        Assert.Equal(1, Scalar<long>(
            "SELECT count(DISTINCT ask_group) FROM exchanges WHERE ask_group IS NOT NULL;"));

        // And every one of them is an ordinary OPEN exchange. Nothing about the group changes a
        // state, a window, or a cap -- which is what stops it becoming the plural exchange §5.5
        // deliberately does not introduce.
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM exchanges WHERE state = 'OPEN';"));
    }

    [Fact]
    public async Task An_ordinary_question_belongs_to_no_group()
    {
        await _broker.AskAsync(
            Docs, AgentName.Create("infra@alpha"), Model, Question("just the one"),
            T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, Scalar<long>(
            "SELECT count(*) FROM exchanges WHERE ask_group IS NOT NULL;"));
    }

    [Fact]
    public async Task The_asker_is_not_in_its_own_group()
    {
        // A role the asker shares would otherwise have it ask itself, which §7.1's pair identity
        // refuses anyway -- but excluding it here means the asker gets a group of its peers
        // rather than a group with one guaranteed refusal in it.
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO agents (name, adapter, tier, status) " +
                "VALUES ('docs@alpha', 'test', 'pull', 'active');";
            command.ExecuteNonQuery();
        }

        var outcomes = await AskRole("docs");

        Assert.True(Assert.Single(outcomes).Succeeded);
    }

    [Fact]
    public async Task A_role_is_matched_whole_and_never_as_a_prefix()
    {
        // The load-bearing reason `role` is a column at all rather than something derived at
        // query time. `infra` is a prefix of `infrastructure`, so a LIKE 'infra%' would quietly
        // put a question in front of agents that answer to something else entirely -- and the
        // asker would never know, because it asked a role and got answers.
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO agents (name, adapter, tier, status) " +
                "VALUES ('infrastructure@alpha', 'test', 'pull', 'active');";
            command.ExecuteNonQuery();
        }

        var outcomes = await AskRole("infra");

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(0, Scalar<long>(
            "SELECT count(*) FROM exchanges WHERE answerer LIKE 'infrastructure%';"));
    }

    [Fact]
    public async Task A_role_nobody_answers_to_is_refused_rather_than_returning_nothing()
    {
        // "Asked zero agents, all done" reads as success, and asking a role nothing is
        // registered under is almost always a misspelling.
        var outcomes = await AskRole("infrastructure");

        Assert.False(Assert.Single(outcomes).Succeeded);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM exchanges;"));
    }

    [Fact]
    public async Task A_retired_member_is_not_asked()
    {
        // Retirement is the one judgement that gates routing, so an exchange opened with a
        // retired agent could never be answered by anybody.
        await new AgentRepository(_connection).RetireAsync(
            AgentName.Create("infra@beta"), TestContext.Current.CancellationToken);

        var outcomes = await AskRole("infra");

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(0, Scalar<long>(
            "SELECT count(*) FROM exchanges WHERE answerer = 'infra@beta';"));
    }

    [Fact]
    public async Task One_member_refusing_does_not_silence_the_question_to_the_others()
    {
        // The load-bearing one. A member at its pair cap, or already holding this question, is a
        // fact about that pair and says nothing about the rest -- and an all-or-nothing group
        // would let one busy agent silence a question to everybody else.
        await AskRole("infra");

        var again = await AskRole("infra");

        // Every one is a duplicate of a question still open, so every one is refused -- which is
        // the shape being checked: three independent verdicts, not one for the group.
        Assert.Equal(3, again.Count);
        Assert.All(again, o => Assert.False(o.Succeeded));

        // And the originals are untouched.
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM exchanges WHERE state = 'OPEN';"));
    }

    [Fact]
    public async Task A_group_larger_than_the_cap_is_refused_whole()
    {
        // The bound §17 question 12 records as a guess. Refused before anything is opened,
        // because a partially-asked group is the worst outcome: the asker cannot tell which
        // machines were reached and which were dropped for arithmetic reasons.
        //
        // The fixture is built from MaxGroupSize, so raising the cap does not fail this -- which
        // is deliberate and worth saying, because it looks like a vacuous test and is not. What
        // is pinned is that a cap exists and refuses whole; the number itself is a guess §17
        // asks to be replaced with evidence, and pinning a guess in a test makes it harder to
        // replace rather than safer to keep.
        for (var i = 0; i < Broker.MaxGroupSize; i++)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO agents (name, adapter, tier, status) " +
                $"VALUES ('infra@m{i}', 'test', 'pull', 'active');";
            command.ExecuteNonQuery();
        }

        var outcomes = await AskRole("infra");

        Assert.False(Assert.Single(outcomes).Succeeded);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM exchanges;"));
    }

    [Fact]
    public async Task A_member_on_a_machine_that_is_off_is_still_asked()
    {
        // §5.5's central decision, and the one that looks like a bug until the alternative is
        // written down. Nothing about infra@gamma is connected, so it is unreachable in every
        // practical sense -- and it is asked anyway, so its silence arrives as an orphan the
        // asker is told about rather than as an answer nobody noticed was missing.
        var outcomes = await AskRole("infra");

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(1, Scalar<long>(
            "SELECT count(*) FROM exchanges WHERE answerer = 'infra@gamma';"));
    }
}
