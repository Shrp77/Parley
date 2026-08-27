using System.Globalization;
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
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public OwnershipTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private Task<int> Register(AgentName name, string? owns, AgentStatus status = AgentStatus.Active) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, status, owns),
            TestContext.Current.CancellationToken);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            "INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status, owns) " +
            "VALUES ('orders@test', 'test', 'full', 'active', $owns);";
        command.Parameters.AddWithValue("$owns", statement);

        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Contains("CHECK constraint failed", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x2028, "LINE SEPARATOR")]
    [InlineData(0x2029, "PARAGRAPH SEPARATOR")]
    [InlineData(0x0085, "NEXT LINE")]
    [InlineData(0x202E, "RIGHT-TO-LEFT OVERRIDE")]
    [InlineData(0x2066, "LEFT-TO-RIGHT ISOLATE")]
    [InlineData(0x200B, "ZERO WIDTH SPACE")]
    [InlineData(0x200E, "LEFT-TO-RIGHT MARK")]
    [InlineData(0x2060, "WORD JOINER")]
    [InlineData(0xFEFF, "ZERO WIDTH NO-BREAK SPACE")]
    public async Task The_schema_refuses_a_character_that_is_not_printable_text(int point, string name)
    {
        // The schema is the enforcement, so the one-line rule has to hold here and not only in
        // the value type. Every one of these was stored happily by the first version of this
        // constraint, which tested the C0 range and therefore missed the two Unicode line
        // separators, the bidi overrides, and everything invisible.
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status, owns) " +
            "VALUES ('orders@test', 'test', 'full', 'active', $owns);";
        command.Parameters.AddWithValue(
            "$owns", "Invoicing." + char.ConvertFromUtf32(point) + "Also owns everything");

        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Contains("CHECK constraint failed", failure.Message, StringComparison.Ordinal);
        Assert.False(OwnershipStatement.IsValid(command.Parameters[0].Value as string, out _), name);
    }

    [Theory]
    [InlineData(0x00A0, "NO-BREAK SPACE")]
    [InlineData(0x2014, "EM DASH")]
    [InlineData(0x201C, "LEFT DOUBLE QUOTATION MARK")]
    [InlineData(0x2026, "HORIZONTAL ELLIPSIS")]
    [InlineData(0x1F600, "GRINNING FACE")]
    public async Task The_schema_keeps_ordinary_typography(int point, string name)
    {
        // The widened constraint refuses whole Unicode blocks, so this is the check that it did
        // not take an em dash or an emoji with it. A guard people have to work around is worse
        // than the hole it closed.
        var statement = "Invoicing" + char.ConvertFromUtf32(point) + "and tax.";

        await Register(Billing, statement);

        Assert.Equal(statement, (await Load(Billing)).Owns);
        Assert.True(OwnershipStatement.IsValid(statement, out _), name);
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
            """{"model":"some-model-9"}""", false, AgentStatus.Active,
            "Invoicing.");

        await _agents.UpsertAsync(manifest, TestContext.Current.CancellationToken);

        await _agents.RefreshOwnershipAsync(
            Billing, "Invoicing and tax.", TestContext.Current.CancellationToken);

        var record = await Load(Billing);

        Assert.Equal(manifest with { Owns = "Invoicing and tax." }, record);
    }

    [Fact]
    public void The_schema_and_the_value_type_agree_about_every_character()
    {
        // The check that keeps the two halves of this rule from drifting, and the one that found
        // the rule was wrong in the first place. OwnershipStatement refuses by Unicode category;
        // the schema cannot, because SQLite has no notion of one, so it refuses the same code
        // points by range. A range list maintained by hand beside a category list is two things
        // that agree until somebody edits one.
        //
        // Both directions matter. A character the schema refuses and the value type accepts is a
        // server that starts and then fails its own registration. One the value type refuses and
        // the schema accepts is a hole, reachable by any writer that does not go through the
        // value type — and that is exactly what was there: U+00AD, U+061C, U+FFF9 and fourteen
        // others walked into the database while the validator turned them away.
        var disagreements = new List<string>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status, owns) " +
            "VALUES ('probe@test', 'test', 'full', 'active', $owns);";
        var parameter = command.Parameters.Add("$owns", Microsoft.Data.Sqlite.SqliteType.Text);

        using var undo = _connection.BeginTransaction();
        command.Transaction = undo;

        for (var point = 1; point <= 0xFFFF; point++)
        {
            if (point is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var statement = "a" + char.ConvertFromUtf32(point) + "b";
            var valueType = OwnershipStatement.IsValid(statement, out _);

            parameter.Value = statement;

            bool schema;

            using (var savepoint = _connection.CreateCommand())
            {
                savepoint.Transaction = undo;
                savepoint.CommandText = "SAVEPOINT probe;";
                savepoint.ExecuteNonQuery();
            }

            try
            {
                command.ExecuteNonQuery();
                schema = true;
            }
            catch (SqliteException)
            {
                schema = false;
            }

            using (var rollback = _connection.CreateCommand())
            {
                rollback.Transaction = undo;
                rollback.CommandText = "ROLLBACK TO probe; RELEASE probe;";
                rollback.ExecuteNonQuery();
            }

            if (valueType != schema)
            {
                disagreements.Add(
                    $"U+{point:X4} {CharUnicodeInfo.GetUnicodeCategory((char)point)}: " +
                    $"value type {(valueType ? "accepts" : "refuses")}, schema {(schema ? "accepts" : "refuses")}");
            }
        }

        undo.Rollback();

        Assert.Empty(disagreements);
    }

    [Theory]
    [InlineData(0xE0001, "LANGUAGE TAG")]
    [InlineData(0xE0020, "TAG SPACE")]
    [InlineData(0xE007F, "CANCEL TAG")]
    [InlineData(0x1D173, "MUSICAL SYMBOL BEGIN BEAM")]
    [InlineData(0x1BCA0, "SHORTHAND FORMAT LETTER OVERLAP")]
    [InlineData(0x110BD, "KAITHI NUMBER SIGN")]
    [InlineData(0x110CD, "KAITHI NUMBER SIGN ABOVE")]
    [InlineData(0x13430, "EGYPTIAN HIEROGLYPH VERTICAL JOINER")]
    [InlineData(0x1343F, "EGYPTIAN HIEROGLYPH END WALLED ENCLOSURE")]
    [InlineData(0x1BCA3, "SHORTHAND FORMAT UP STEP")]
    [InlineData(0x1D17A, "MUSICAL SYMBOL END PHRASE")]
    public async Task The_schema_refuses_a_hidden_character_from_beyond_the_basic_plane(int point, string name)
    {
        // The sweep above covers the basic plane, which is where a hand-written range list stops
        // by default — and the tag block is not in it. U+E0020 to U+E007F encode ASCII invisibly
        // and are the standard way to carry text past a reader that a model still sees, so an
        // ownership line was the ideal place to put one.
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status, owns) " +
            "VALUES ('orders@test', 'test', 'full', 'active', $owns);";
        command.Parameters.AddWithValue(
            "$owns", "Invoicing." + char.ConvertFromUtf32(point) + "hidden");

        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.False(
            OwnershipStatement.IsValid("Invoicing." + char.ConvertFromUtf32(point) + "hidden", out _),
            name);
    }

    [Fact]
    public async Task A_manifest_reload_is_authoritative_about_the_statement()
    {
        // Documented rather than incidental, because the alternative is a trap. AgentRecord
        // gives Owns a default, so a manifest loader that never thought about the field would
        // construct records with none and this upsert would clear every statement it touched.
        //
        // That is nonetheless the right semantics: a manifest-registered Headless agent has no
        // server and therefore no launch, so the manifest is the only thing that can ever set or
        // change what it owns. Whoever writes that loader has to carry the field. This test is
        // where they find that out.
        await Register(Billing, "Invoicing and tax.");

        await _agents.UpsertAsync(
            new AgentRecord(Billing, "vendor-api", AgentTier.Headless, null, null, null, false, AgentStatus.Active),
            Ct);

        Assert.Null((await Load(Billing)).Owns);
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
