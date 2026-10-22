using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That storage holds the person seat's rules, and that the two broker-provided participants have no machine
/// (SPEC.md §4.1.1, §9.2, §12.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct SQL for the refusals, for the reason <see cref="FacilitatorStore"/> gives.</b> These are about what
/// storage refuses; a repository that checked first would make a missing rule invisible here.
/// </para>
/// <para>
/// <b>Each refusal must fail against a schema without it</b>: every one of them would be admitted, and
/// the person and the facilitator each given a machine named after the project.
/// </para>
/// </remarks>
public sealed class PersonSeatStoreTests : IDisposable
{
    private static readonly ProjectName Alpha = ProjectName.Create("alpha");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Execute(string sql) => _connection.Execute(sql);

    private long Count(string sql) => _connection.ExecuteScalar<long>(sql);

    private async Task<AgentName> SeatedAsync()
    {
        var agents = new AgentRepository(_connection);

        await agents.EnsureFacilitatorAsync(Alpha, cancellationToken: Ct);

        return await agents.EnsurePersonAsync(Alpha, cancellationToken: Ct);
    }

    [Fact]
    public async Task Ensuring_the_person_twice_writes_one_row()
    {
        var first = await SeatedAsync();
        var second = await new AgentRepository(_connection).EnsurePersonAsync(Alpha, cancellationToken: Ct);

        Assert.Equal("human-user@alpha", first.Value);
        Assert.Equal(first, second);
        Assert.Equal(1L, Count("SELECT count(*) FROM agents WHERE role = 'human-user';"));
    }

    [Fact]
    public async Task Neither_broker_provided_participant_has_a_machine_or_declares_one()
    {
        await SeatedAsync();

        Assert.Equal(2L, Count(
            "SELECT count(*) FROM agents WHERE machine IS NULL " +
            "AND name IN ('human-user@alpha', 'parley-facilitator@alpha');"));

        // No fake machine: a node named for the project that never connects.
        Assert.Equal(0L, Count("SELECT count(*) FROM nodes WHERE machine = 'alpha';"));
    }

    [Fact]
    public async Task A_surface_attaching_as_the_person_declares_no_machine_either()
    {
        // Registration writes the node row itself before the trigger runs, so teaching only the
        // trigger left this route open: a surface's attach goes through RegisterIfAbsentAsync, and the broker's own
        // upsert through UpsertAsync.
        var agents = new AgentRepository(_connection);
        var person = new AgentRecord(
            PersonSeat.On(Alpha), PersonSeat.Adapter, AgentTier.Pull, null, null, null, false, AgentStatus.Active);

        await agents.RegisterIfAbsentAsync(person, Ct);
        await agents.UpsertAsync(person, Ct);

        Assert.Equal(0L, Count("SELECT count(*) FROM nodes WHERE machine = 'alpha';"));
        Assert.Equal(1L, Count("SELECT count(*) FROM agents WHERE name = 'human-user@alpha' AND machine IS NULL;"));
    }

    [Fact]
    public void An_agent_still_declares_its_machine()
    {
        Execute(
            "INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@server', 'claude-code', 'full', 'active');");

        Assert.Equal(1L, Count("SELECT count(*) FROM agents WHERE name = 'orders@server' AND machine = 'server';"));
        Assert.Equal(1L, Count("SELECT count(*) FROM nodes WHERE machine = 'server';"));
    }

    [Theory]
    [InlineData("parley-facilitator@Alpha!")]
    [InlineData("parley-facilitator@a.b")]
    [InlineData("human-user@9lives")]
    [InlineData("human-user@")]
    public void A_second_half_that_is_not_a_project_name_is_refused(string name)
    {
        // The machine CHECK passes on NULL, so without its own check the part after '@' would be checked by nothing.
        var adapter = name.StartsWith("human-user", StringComparison.Ordinal) ? "parley-console" : "parley-facilitator";

        Assert.Throws<SqliteException>(() => Execute(
            $"INSERT INTO agents (name, adapter, tier, status) VALUES ('{name}', '{adapter}', 'pull', 'active');"));
    }

    [Fact]
    public async Task Neither_participant_can_carry_an_ownership_line_by_any_route()
    {
        var person = await SeatedAsync();
        var agents = new AgentRepository(_connection);

        // The route a claimant would take: every attach refreshes `owns` from its launch.
        await Assert.ThrowsAsync<SqliteException>(() => agents.RefreshOwnershipAsync(person, "carts and totals", Ct));
        Assert.Throws<SqliteException>(() => Execute(
            "UPDATE agents SET owns = 'x' WHERE name = 'parley-facilitator@alpha';"));

        // And the ordinary refresh of a real seat, which carries no line, still goes through.
        Assert.Equal(1, await agents.RefreshOwnershipAsync(person, null, Ct));
    }

    [Fact]
    public async Task The_person_cannot_be_retired()
    {
        await SeatedAsync();

        Assert.Throws<SqliteException>(() => Execute(
            "UPDATE agents SET status = 'retired' WHERE name = 'human-user@alpha';"));
    }

    [Fact]
    public async Task The_person_cannot_be_purged()
    {
        await SeatedAsync();

        Assert.Throws<SqliteException>(() => Execute("DELETE FROM agents WHERE name = 'human-user@alpha';"));
    }

    [Fact]
    public async Task A_second_person_is_refused_however_it_is_written()
    {
        await SeatedAsync();

        Assert.Throws<SqliteException>(() => Execute(
            "INSERT INTO agents (name, adapter, tier, status) VALUES ('human-user@beta', 'parley-console', 'pull', 'active');"));
    }

    [Fact]
    public async Task The_seats_adapter_under_any_other_role_is_refused_now_the_reservation_has_landed()
    {
        // An agent attaching under the seat's adapter would be treated as the person (principle 2.10), so the
        // reservation holds.
        await SeatedAsync();

        var refused = Assert.Throws<SqliteException>(() =>
            Execute("INSERT INTO agents (name, adapter, tier, status) VALUES ('arvind@alpha', 'parley-console', 'pull', 'active');"));

        Assert.Contains("person seat alone", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_persons_role_under_any_other_adapter_is_refused()
    {
        var refused = Assert.Throws<SqliteException>(() =>
            Execute("INSERT INTO agents (name, adapter, tier, status) VALUES ('human-user@alpha', 'claude-code', 'pull', 'active');"));

        Assert.Contains("person seat alone", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_persons_adapter_cannot_be_changed_afterwards()
    {
        // The third probe: the row exists before any attach, so an update is the way a claimant would try.
        await SeatedAsync();

        var refused = Assert.Throws<SqliteException>(() =>
            Execute("UPDATE agents SET adapter = 'claude-code' WHERE role = 'human-user';"));

        Assert.Contains("person seat alone", refused.Message, StringComparison.Ordinal);
    }
}
