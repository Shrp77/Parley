using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Constraints that make a specification claim unbreakable rather than merely documented.
/// Each test corresponds to a rule that application code could otherwise route around.
/// </summary>
public sealed class SchemaIntegrityTests : IDisposable
{
    private const string OrdersBilling = "billing\u001Forders";
    private const string Opened = "2026-01-01T00:00:00.0000000Z";
    private const string Expires = "2026-01-01T00:15:00.0000000Z";
    private const string Closed = "2026-01-01T00:05:00.0000000Z";

    private readonly SqliteConnection _connection;

    public SchemaIntegrityTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Seed("orders", "billing", "inventory");
    }

    public void Dispose() => _connection.Dispose();

    private void Seed(params string[] names)
    {
        foreach (var name in names)
        {
            Execute(
                """
                INSERT INTO agents (name, adapter, tier, status)
                VALUES ($name, 'test', 'full', 'registered');
                """,
                ("$name", name));
        }
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private void InsertExchange(string pairKey, string asker, string answerer, string state = "OPEN") => Execute(
        """
        INSERT INTO exchanges
            (pair_key, asker, answerer, owed_by, state, ttl_ms, opened_utc, expires_utc, closed_utc)
        VALUES
            ($pair, $asker, $answerer, $answerer, $state, 900000, $opened, $expires,
             CASE WHEN $state = 'CLOSED' THEN $closed ELSE NULL END);
        """,
        ("$pair", pairKey), ("$asker", asker), ("$answerer", answerer), ("$state", state),
        ("$opened", Opened), ("$expires", Expires), ("$closed", Closed));



    // ---- one open exchange per pair ----

    [Fact]
    public void A_pair_may_hold_only_one_open_exchange()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        var ex = Assert.Throws<SqliteException>(
            () => InsertExchange(OrdersBilling, "billing", "orders"));

        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Closed_exchanges_do_not_hold_the_pair_slot()
    {
        InsertExchange(OrdersBilling, "orders", "billing", "CLOSED");
        InsertExchange(OrdersBilling, "billing", "orders");
    }

    // ---- pair_key must be derived from its participants ----

    [Fact]
    public void Pair_key_that_does_not_match_its_participants_is_rejected()
    {
        // Without this constraint, ux_open_pair would enforce uniqueness against the wrong
        // string, permitting two open exchanges for the same real pair.
        var ex = Assert.Throws<SqliteException>(
            () => InsertExchange("orders\u001Fbilling", "orders", "billing"));

        Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pair_key_naming_an_unrelated_agent_is_rejected()
    {
        Assert.Throws<SqliteException>(
            () => InsertExchange("billing\u001Finventory", "orders", "billing"));
    }

    [Fact]
    public void A_crafted_key_cannot_smuggle_a_second_open_exchange_past_the_index()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        // The attack the derivation constraint closes: a hand-built key that would hash to a
        // different index entry for the same real pair.
        Assert.Throws<SqliteException>(
            () => InsertExchange("orders\u001Fbilling", "billing", "orders"));
    }

    // ---- exchange field invariants ----

    [Fact]
    public void Owed_by_must_be_a_participant()
    {
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, ttl_ms, opened_utc, expires_utc)
            VALUES ($pair, 'orders', 'billing', 'inventory', 'OPEN', 900000, $opened, $expires);
            """,
            ("$pair", OrdersBilling), ("$opened", Opened), ("$expires", Expires)));
    }

    [Fact]
    public void Ttl_must_be_positive()
    {
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, ttl_ms, opened_utc, expires_utc)
            VALUES ($pair, 'orders', 'billing', 'billing', 'OPEN', 0, $opened, $expires);
            """,
            ("$pair", OrdersBilling), ("$opened", Opened), ("$expires", Expires)));
    }

    [Fact]
    public void An_agent_cannot_question_itself()
    {
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, ttl_ms, opened_utc, expires_utc)
            VALUES ($pair, 'orders', 'orders', 'orders', 'OPEN', 900000, $opened, $expires);
            """,
            ("$pair", "orders\u001Forders"), ("$opened", Opened), ("$expires", Expires)));
    }

    // ---- agent names obey the grammar every other constraint rests on ----

    [Theory]
    [InlineData("shipping")]
    [InlineData("a")]
    [InlineData("web-api-2")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]   // 32 characters, the limit
    public void Names_matching_the_grammar_are_accepted(string name)
    {
        Execute(
            """
            INSERT INTO agents (name, adapter, tier, status)
            VALUES ($name, 'test', 'full', 'registered');
            """,
            ("$name", name));
    }

    [Theory]
    [InlineData("Shipping")]                            // uppercase
    [InlineData("1shipping")]                           // leading digit
    [InlineData("-shipping")]                           // leading hyphen
    [InlineData("ship_ping")]                           // underscore
    [InlineData("ship ping")]                           // space
    [InlineData("")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]   // 33 characters
    public void Names_breaking_the_grammar_are_rejected(string name)
    {
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO agents (name, adapter, tier, status)
            VALUES ($name, 'test', 'full', 'registered');
            """,
            ("$name", name)));
    }

    [Fact]
    public void A_name_containing_the_pair_separator_is_rejected()
    {
        // The attack this closes. pair_key is min(asker, answerer) || char(31) || max(...),
        // so a name holding char(31) makes that derivation ambiguous: ('a', 'b<US>c') and
        // ('a<US>b', 'c') produce one identical key. ux_open_pair would then hold one slot
        // against two unrelated pairs, and the pair_key CHECK could not tell, because the
        // key it recomputes is ambiguous in exactly the same way.
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO agents (name, adapter, tier, status)
            VALUES ($name, 'test', 'full', 'registered');
            """,
            ("$name", "b\u001Fc")));
    }

    // ---- credential_ref holds pointers, never secrets ----

    [Theory]
    [InlineData("env:VENDOR_API_KEY")]
    [InlineData("keychain:parley/billing")]
    [InlineData("file:/etc/parley/billing.token")]
    [InlineData(null)]
    public void Recognised_credential_references_are_accepted(string? reference)
    {
        Execute(
            """
            INSERT INTO agents (name, adapter, tier, status, credential_ref)
            VALUES ('shipping', 'test', 'headless', 'registered', $ref);
            """,
            ("$ref", reference));
    }

    [Theory]
    [InlineData("sk-live-1a2b3c4d5e6f")]          // a pasted API key
    [InlineData("VENDOR_API_KEY")]                // scheme omitted
    [InlineData("env:")]                          // scheme with no target
    [InlineData("")]
    public void Literal_credential_values_are_rejected(string reference)
    {
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO agents (name, adapter, tier, status, credential_ref)
            VALUES ('shipping', 'test', 'headless', 'registered', $ref);
            """,
            ("$ref", reference)));
    }

    // ---- message integrity ----

    [Fact]
    public void Payload_is_capped_at_two_thousand_characters()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, payload_json, created_utc)
            VALUES (1, 'question', 'orders', 'billing', $payload, $created);
            """,
            ("$payload", $$"""{"text":"{{new string('x', 2000)}}"}"""), ("$created", Opened)));
    }

    [Fact]
    public void Payload_must_be_valid_json()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, payload_json, created_utc)
            VALUES (1, 'question', 'orders', 'billing', 'not json at all', $created);
            """,
            ("$created", Opened)));
    }

    [Fact]
    public void Notify_must_not_reference_an_exchange()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        // Notify opens no exchange and claims no pair slot, so an exchange reference on one
        // would be a lie about which rules applied to it.
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, payload_json, created_utc)
            VALUES (1, 'notify', 'orders', 'billing', '{}', $created);
            """,
            ("$created", Opened)));
    }

    [Fact]
    public void Answers_must_name_their_responder()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        // An unattributed 'unknown' from a weak local model reads identically to a considered
        // 'unknown' from a frontier model, and the asker acts on both the same way.
        Assert.Throws<SqliteException>(() => Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, payload_json, created_utc)
            VALUES (1, 'answer', 'billing', 'orders', '{}', $created);
            """,
            ("$created", Opened)));
    }

    [Fact]
    public void Broker_forced_answers_record_the_broker_as_responder()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        Execute(
            """
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json, created_utc)
            VALUES (1, 'answer', 'billing', 'orders', 'parley:broker',
                    '{"resolution":"unknown"}', $created);
            """,
            ("$created", Opened));
    }

    [Fact]
    public void Questions_may_omit_a_responder()
    {
        InsertExchange(OrdersBilling, "orders", "billing");

        Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, payload_json, created_utc)
            VALUES (1, 'question', 'orders', 'billing', '{}', $created);
            """,
            ("$created", Opened));
    }

    [Fact]
    public void Exchange_naming_an_unregistered_agent_is_rejected()
    {
        // 'shipping' was never registered. Asserting on the message matters here: the same
        // row also has CHECK constraints to satisfy, so a bare Throws would still pass if
        // the insert failed for an unrelated reason and the foreign key were never reached.
        var ex = Assert.Throws<SqliteException>(
            () => InsertExchange("billing\u001Fshipping", "shipping", "billing"));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- connections are configured by construction ----

    [Fact]
    public void Foreign_keys_are_enforced_on_factory_connections()
    {
        // Raw SQLite defaults foreign key enforcement to OFF, but Microsoft.Data.Sqlite
        // overrides that and enables it per connection. This test pins the provider's
        // behaviour rather than assuming it: if a future version, a different provider, or a
        // connection string keyword changes the default, this fails and the factory's
        // guarantee gets re-examined instead of quietly becoming the only thing holding
        // referential integrity up.
        using var raw = new SqliteConnection("Data Source=:memory:");
        raw.Open();

        Assert.Equal(1L, ReadForeignKeysPragma(raw));

        using var viaFactory = ParleyDatabase.OpenInMemory();
        Assert.Equal(1L, ReadForeignKeysPragma(viaFactory));
    }

    [Fact]
    public void Factory_connections_use_wal_and_a_busy_timeout()
    {
        // The settings the factory genuinely adds over a bare connection. These are
        // per-connection, so every connection the broker opens must go through the factory
        // or the reaper and the inbox drain lose their concurrent-reader guarantees.
        using var viaFactory = ParleyDatabase.OpenInMemory();

        using var command = viaFactory.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(
            5000L,
            Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Database_and_wal_sidecars_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows inherits directory ACLs; there is no equivalent mode to assert.
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"parley-{Guid.NewGuid():N}.db");
        var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            // Asserted while the connection is still open. SQLite removes the sidecars on a
            // clean close of the last connection, so checking after the using block would
            // pass by finding nothing left to check.
            using (var connection = ParleyDatabase.OpenAndMigrate(path))
            {
                Assert.Equal(ConnectionState.Open, connection.State);

                // The database holds the full text of every question and answer between
                // agents. The write-ahead log holds the most recent of them, so restricting
                // only the main file protects everything except the newest writes.
                Assert.Equal(owner, File.GetUnixFileMode(path));

                foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
                {
                    Assert.True(
                        File.Exists(sidecar),
                        $"expected '{sidecar}' to exist while a WAL connection is open");
                    Assert.Equal(owner, File.GetUnixFileMode(sidecar));
                }
            }
        }
        finally
        {
            foreach (var sidecar in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }
    }

    // ---- rehydration verifies rather than recomputes ----

    [Fact]
    public void Rehydration_rejects_a_pair_key_that_is_not_derived()
    {
        var row = MakeRow("billing\u001Finventory");

        var ex = Assert.Throws<InvalidOperationException>(() => row.ToDomain());
        Assert.Contains("pair key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rehydration_accepts_a_correctly_derived_pair_key()
    {
        var exchange = MakeRow(OrdersBilling).ToDomain();

        Assert.Equal(
            PairKey.For(AgentName.Create("orders"), AgentName.Create("billing")),
            exchange.PairKey);
    }

    private static ExchangeRow MakeRow(string pairKey) => new()
    {
        Id = 7,
        PairKey = pairKey,
        Asker = "orders",
        Answerer = "billing",
        OwedBy = "billing",
        State = "OPEN",
        TtlMs = 900_000,
        OpenedUtc = "2026-01-01T00:00:00.0000000+00:00",
        ExpiresUtc = "2026-01-01T00:15:00.0000000+00:00"
    };

    private static long ReadForeignKeysPragma(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
