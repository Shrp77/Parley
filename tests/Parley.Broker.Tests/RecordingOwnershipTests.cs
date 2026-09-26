using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Recording what a person agreed an agent owns (SPEC.md §10.2 <c>record_ownership</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The guarantee under test is the one §17 question 10 is about</b>: no line reaches the registry
/// that a person did not accept, and this operation does not put one there either. The registry is
/// filled from the launch environment when an agent attaches (§12.3), so what these check is that
/// recording a decision changes <c>owns</c> not at all — which is what makes <i>pending</i> a
/// comparison of two facts rather than a flag somebody has to clear.
/// </para>
/// <para>
/// Each test says what it must fail against, because a probe that cannot fail is the defect this
/// repository has recorded more often than any other.
/// </para>
/// </remarks>
public sealed class RecordingOwnershipTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    /// <summary>Registers an agent, optionally carrying a line already.</summary>
    /// <param name="owns">What its launch says it owns, or null for none.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>Nothing; the agent is in the registry afterwards.</returns>
    private async Task RegisterAsync(string? owns, CancellationToken cancellationToken)
    {
        // The machine and the conversation are both real rows because both are real foreign keys,
        // and the second one is load-bearing rather than incidental: FR-011 says an accepted line
        // must be traceable to the discussion that produced it, and an identifier pointing at no
        // conversation is not traceable. A fixture that stubbed it would be testing a weaker rule
        // than the schema holds.
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc)
              VALUES ('test', 0, '2026-09-26T00:00:00.0000000Z')
              ON CONFLICT DO NOTHING;
            INSERT INTO conversations (id, state, opened_utc)
              VALUES (@Discussion, 'OPEN', '2026-09-26T00:00:00.0000000Z')
              ON CONFLICT DO NOTHING;
            """,
            new { Discussion });

        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(
                Orders, "test", AgentTier.Full,
                Workspace: null, SessionRef: null, AdapterConfig: null,
                Connected: false, AgentStatus.Active, Owns: owns),
            cancellationToken);
    }

    /// <summary>The conversation an ownership ask opens, as `owns-&lt;role&gt;-&lt;timestamp&gt;`.</summary>
    private const string Discussion = "owns-orders-20260926-120000";

    private OperatorConsole Console() => new(_connection);

    private OwnershipAcceptance Proposed(string statement = "Invoicing and tax. Not stock.") =>
        new(statement, OwnershipSource.Proposed, statement.Length, Discussion);

    [Fact]
    public async Task What_peers_read_is_not_touched()
    {
        // THE WHOLE POINT. A person accepting a line records a decision; it does not edit the
        // registry. Must fail against an implementation that helpfully writes `owns` at the same
        // time -- which is the obvious thing to write and is the route around Principle III that
        // §17 question 10 exists to keep shut.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync("Invoicing only.", ct);

        var record = await Console().RecordOwnershipAsync(Orders, Proposed(), T0, ct);

        Assert.True(record.Recorded);
        Assert.Equal(
            "Invoicing only.",
            _connection.QuerySingle<string?>("SELECT owns FROM agents WHERE name = 'orders@test';"));
    }

    [Fact]
    public async Task An_accepted_line_is_pending_until_the_agent_restarts()
    {
        // The two halves differ, so the surface says so. Must fail against `Pending` being hardcoded
        // either way: the next test is the other side of it.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync("Invoicing only.", ct);

        var record = await Console().RecordOwnershipAsync(Orders, Proposed(), T0, ct);

        Assert.True(record.Pending);
        Assert.Equal("Invoicing only.", record.LiveStatement);
    }

    [Fact]
    public async Task A_line_that_already_matches_is_not_pending()
    {
        // The state after the agent restarted and registered the accepted line. Nothing cleared a
        // flag to get here -- the comparison simply stopped differing, which is why there is no flag.
        var ct = TestContext.Current.CancellationToken;
        var agreed = "Invoicing and tax. Not stock.";
        await RegisterAsync(agreed, ct);

        var record = await Console().RecordOwnershipAsync(Orders, Proposed(agreed), T0, ct);

        Assert.True(record.Recorded);
        Assert.False(record.Pending);
    }

    [Fact]
    public async Task An_unregistered_agent_is_refused_and_nothing_is_written()
    {
        // §14 counts an unregistered name unroutable, so accepting a line for one would record a
        // decision about an agent nothing can address. Must fail against letting the INSERT run and
        // reporting SQLite's foreign-key message, which names a constraint rather than the problem.
        var ct = TestContext.Current.CancellationToken;

        var record = await Console().RecordOwnershipAsync(Orders, Proposed(), T0, ct);

        Assert.False(record.Recorded);
        Assert.Contains("nothing has registered", record.Problem!, StringComparison.Ordinal);
        Assert.Equal(0, _connection.QuerySingle<int>("SELECT COUNT(*) FROM agent_ownership_accepted;"));
    }

    [Fact]
    public async Task A_statement_that_could_break_its_roster_row_is_refused_in_words_a_person_reads()
    {
        // The character rule did not move when the length bound came off at 0.33.0. Must fail against
        // the refusal being SQLite's: a person reading "CHECK constraint failed" cannot act on it.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var broken = "Invoicing." + (char)0x2028 + "Everything else.";

        var record = await Console().RecordOwnershipAsync(
            Orders, new OwnershipAcceptance(broken, OwnershipSource.Proposed, broken.Length, Discussion), T0, ct);

        Assert.False(record.Recorded);
        Assert.DoesNotContain("CHECK constraint", record.Problem!, StringComparison.Ordinal);
        Assert.Equal(0, _connection.QuerySingle<int>("SELECT COUNT(*) FROM agent_ownership_accepted;"));
    }

    [Fact]
    public async Task A_long_statement_is_recorded_because_the_bound_came_off()
    {
        // The enabling half of 020: an agent asked what it owns answers in two halves and names a
        // peer, which a hundred characters frequently will not hold.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var record = await Console().RecordOwnershipAsync(Orders, Proposed(new string('x', 1_200)), T0, ct);

        Assert.True(record.Recorded);
        Assert.Equal(
            1_200,
            _connection.QuerySingle<int>(
                "SELECT length(statement) FROM agent_ownership_accepted WHERE agent = 'orders@test';"));
    }

    [Theory]
    [InlineData(OwnershipSource.Proposed, "proposed")]
    [InlineData(OwnershipSource.Edited, "edited")]
    [InlineData(OwnershipSource.Composed, "composed")]
    public async Task How_the_person_arrived_at_it_is_recorded(OwnershipSource source, string stored)
    {
        // Must fail against every acceptance being written as one source. What separates them is
        // whose characters they are, which is what stops a person's line being read later as an
        // agent's own words.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var acceptance = source == OwnershipSource.Composed
            ? new OwnershipAcceptance("Invoicing and tax.", source, null, null)
            : new OwnershipAcceptance("Invoicing and tax.", source, 40, Discussion);

        Assert.True((await Console().RecordOwnershipAsync(Orders, acceptance, T0, ct)).Recorded);

        Assert.Equal(
            stored,
            _connection.QuerySingle<string>(
                "SELECT source FROM agent_ownership_accepted WHERE agent = 'orders@test';"));
    }

    [Fact]
    public async Task A_composed_line_records_no_length_an_agent_never_wrote()
    {
        // 0.29.0's second review finding, from the other side: a retry wrote into size_refusals a
        // length no agent composed, and size_refusals is what §17 question 16 reads its evidence out
        // of. The length here is the evidence the removed bound has to be chosen against, so a
        // person's characters must never be counted into it.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var composed = new OwnershipAcceptance("Invoicing and tax.", OwnershipSource.Composed, null, null);

        Assert.True((await Console().RecordOwnershipAsync(Orders, composed, T0, ct)).Recorded);

        Assert.Null(
            _connection.QuerySingle<int?>(
                "SELECT proposed_length FROM agent_ownership_accepted WHERE agent = 'orders@test';"));
    }

    [Fact]
    public async Task A_composed_line_carrying_a_proposal_length_is_refused()
    {
        // The shape rule, which storage also holds. Must fail against the guard only living in the
        // schema: the reply has to say what is wrong in words, and the CHECK cannot.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var wrong = new OwnershipAcceptance("Invoicing.", OwnershipSource.Composed, 10, null);

        var record = await Console().RecordOwnershipAsync(Orders, wrong, T0, ct);

        Assert.False(record.Recorded);
        Assert.Contains("no proposal", record.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposed_line_with_no_conversation_is_refused()
    {
        // FR-011: an accepted line must be traceable to the discussion that produced it, and a
        // proposal was discussed somewhere by definition.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var wrong = new OwnershipAcceptance("Invoicing.", OwnershipSource.Proposed, 10, null);

        var record = await Console().RecordOwnershipAsync(Orders, wrong, T0, ct);

        Assert.False(record.Recorded);
        Assert.Contains("conversation", record.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepting_twice_leaves_one_record_and_it_is_the_later_one()
    {
        // An operator may argue a line to a second conclusion. Two rows would make "what was agreed"
        // a question with two answers, and the surface would have to pick.
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(null, ct);

        var console = Console();
        Assert.True((await console.RecordOwnershipAsync(Orders, Proposed("First."), T0, ct)).Recorded);
        Assert.True((await console.RecordOwnershipAsync(Orders, Proposed("Second."), T0.AddMinutes(5), ct)).Recorded);

        Assert.Equal(1, _connection.QuerySingle<int>("SELECT COUNT(*) FROM agent_ownership_accepted;"));
        Assert.Equal(
            "Second.",
            _connection.QuerySingle<string>(
                "SELECT statement FROM agent_ownership_accepted WHERE agent = 'orders@test';"));
    }
}
