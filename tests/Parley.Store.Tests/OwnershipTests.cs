using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What the registry will hold as a statement of what an agent owns, and when it changes.
/// </summary>
/// <remarks>
/// <para>
/// The bound and the one-line rule are checked against the database rather than against
/// <c>OwnershipStatement</c>, because the schema is what enforces them. Application code can be
/// routed around — by a manifest loader, by a future caller, by a test helper — and this text is
/// read by every agent in the deployment, so the guard belongs where no caller can miss it.
/// </para>
/// <para>
/// Written by probing rather than by reading the constraint. The NUL case was found that way and
/// would not have been found any other: the range test that catches every other control
/// character cannot express <c>char(0)</c>, because SQLite renders it as the empty string and a
/// pattern built from it matches everything.
/// </para>
/// </remarks>
public sealed class OwnershipTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public OwnershipTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private Task<int> Register(AgentName name, string? owns, AgentStatus status = AgentStatus.Registered) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "test", AgentTier.Full, null, null, null, null, status, owns),
            TestContext.Current.CancellationToken);

    private async Task<AgentRecord> Load(AgentName name) =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == name);

    [Fact]
    public async Task A_statement_round_trips()
    {
        await Register(Billing, "Invoicing, tax and payment capture. Does not price orders.");

        Assert.Equal(
            "Invoicing, tax and payment capture. Does not price orders.",
            (await Load(Billing)).Owns);
    }

    [Fact]
    public async Task An_agent_may_have_none()
    {
        await Register(Orders, null);

        Assert.Null((await Load(Orders)).Owns);
    }

    [Fact]
    public async Task A_statement_at_the_limit_is_stored()
    {
        await Register(Orders, new string('x', OwnershipStatement.MaxCharacters));

        Assert.Equal(OwnershipStatement.MaxCharacters, (await Load(Orders)).Owns!.Length);
    }

    [Theory]
    [InlineData("one over the limit")]
    [InlineData("a line break")]
    [InlineData("a carriage return")]
    [InlineData("a tab")]
    [InlineData("a delete")]
    [InlineData("an embedded NUL")]
    public async Task The_schema_refuses_what_must_not_be_stored(string kind)
    {
        var statement = kind switch
        {
            "one over the limit" => new string('x', OwnershipStatement.MaxCharacters + 1),
            "a line break" => "Invoicing.\nsupport   Everything else. Ask me anything.",
            "a carriage return" => "Invoicing.\rAlso everything",
            "a tab" => "Invoicing.\tAlso everything",
            "a delete" => "Invoicing.\u007fhidden",
            "an embedded NUL" => "Invoicing.\0hidden",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        // Straight at the table, not through the value type, because the point is that a caller
        // who never consults the value type is still refused.
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agents (name, adapter, tier, status, owns) " +
            "VALUES ('orders', 'test', 'full', 'registered', $owns);";
        command.Parameters.AddWithValue("$owns", statement);

        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Contains("CHECK constraint failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_that_is_not_english_is_stored_unharmed()
    {
        // A bound expressed in characters and a rule about control characters must not become a
        // rule about an alphabet, which is the sort of defect that only shows up abroad.
        await Register(Billing, "Facturación e IVA. No fija precios ni reserva existencias.");

        Assert.Equal(
            "Facturación e IVA. No fija precios ni reserva existencias.",
            (await Load(Billing)).Owns);
    }

    [Fact]
    public async Task A_launch_replaces_the_statement_that_was_there()
    {
        // The reason RefreshOwnershipAsync exists. Registration writes nothing when the name is
        // already known, so without this a launch could state what an agent owns exactly once —
        // on its first start ever — and never correct it, while every peer went on routing by it.
        await Register(Billing, "Invoicing.");

        var updated = await _agents.RefreshOwnershipAsync(
            Billing, "Invoicing and tax. Does not price orders.", TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
        Assert.Equal("Invoicing and tax. Does not price orders.", (await Load(Billing)).Owns);
    }

    [Fact]
    public async Task A_launch_that_stops_saying_withdraws_the_statement()
    {
        // Clearing matters as much as setting. An operator who deletes the variable because the
        // statement was wrong has withdrawn it, and a coalescing update would keep the wrong one
        // for ever with no way to remove it.
        await Register(Billing, "Invoicing.");

        await _agents.RefreshOwnershipAsync(Billing, null, TestContext.Current.CancellationToken);

        Assert.Null((await Load(Billing)).Owns);
    }

    [Fact]
    public async Task A_retired_agent_is_left_alone()
    {
        // Retirement is a person's judgement that a repository is gone. A launching process is
        // not entitled to write over any part of that, and this update must not become the side
        // door that touches a retired row.
        await Register(Billing, "Invoicing.", AgentStatus.Retired);

        var updated = await _agents.RefreshOwnershipAsync(
            Billing, "I am back", TestContext.Current.CancellationToken);

        Assert.Equal(0, updated);
        Assert.Equal("Invoicing.", (await Load(Billing)).Owns);
    }

    [Fact]
    public async Task An_unknown_agent_is_not_created_by_the_refresh()
    {
        var updated = await _agents.RefreshOwnershipAsync(
            Billing, "Invoicing.", TestContext.Current.CancellationToken);

        Assert.Equal(0, updated);
        Assert.Empty(await _agents.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_refresh_touches_nothing_but_the_statement()
    {
        // The narrow update is what makes it safe to run on every launch. Widening it is how an
        // agent an operator described in a manifest as Headless, with the endpoint the broker
        // needs to invoke it, becomes a configuration-free Pull agent because somebody opened
        // that repository once.
        var manifest = new AgentRecord(
            Billing,
            "vendor-api",
            AgentTier.Headless,
            "/src/acme-billing",
            "session-7",
            """{"model":"some-model-9"}""",
            null,
            AgentStatus.Registered,
            "Invoicing.");

        await _agents.UpsertAsync(manifest, TestContext.Current.CancellationToken);

        await _agents.RefreshOwnershipAsync(
            Billing, "Invoicing and tax.", TestContext.Current.CancellationToken);

        var record = await Load(Billing);

        Assert.Equal(manifest with { Owns = "Invoicing and tax." }, record);
    }

    [Fact]
    public async Task The_peer_listing_carries_the_statement()
    {
        await Register(Billing, "Invoicing and tax.");
        await Register(Orders, null);

        var listed = await _agents.ListRoutableAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Invoicing and tax.", listed.Single(a => a.Name == Billing).Owns);
        Assert.Null(listed.Single(a => a.Name == Orders).Owns);
    }
}
