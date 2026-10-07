using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// Executes the schema written in SPEC.md §9.2 against a real SQLite engine.
/// </summary>
/// <remarks>
/// <para>
/// The document's schema is not an illustration; §9.3 argues that invariants belong in
/// storage precisely because application code can route around them, and every one of those
/// arguments is worth exactly as much as the DDL that carries it. A fragment that no longer
/// parses is a claim the project cannot cash, and reading SQL is not a reliable way to notice.
/// </para>
/// <para>
/// Every constraint in this document was executed by hand before being committed. That is the
/// same work, done once, and then never done again on the next edit. This is that check made
/// permanent.
/// </para>
/// </remarks>
public sealed class SpecSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SpecSchemaTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void The_document_contains_a_schema()
    {
        // Guards the extraction rather than the schema. A regex that silently matched nothing
        // would make every other test in this class pass by vacuity, which is the failure mode
        // of a checker nobody checks.
        Assert.True(SpecDocument.SchemaFragments.Count >= 10,
            $"only {SpecDocument.SchemaFragments.Count} schema fragments extracted from SPEC.md; " +
            "the extraction is probably broken rather than the document");
    }

    [Fact]
    public void Every_schema_fragment_executes_in_the_order_written()
    {
        // Order is part of the claim. A trigger cannot be created before its table exists, so
        // a document that reads sensibly but defines them in the wrong order describes a
        // database nobody can build.
        foreach (var (kind, name, sql) in SpecDocument.SchemaObjects)
        {
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Assert.Fail(
                    $"SPEC.md §9.2 defines {kind} {name}, which SQLite refuses: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Every_created_object_has_a_distinct_name()
    {
        var duplicates = SpecDocument.SchemaObjects
            .GroupBy(o => o.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"SPEC.md defines the same object twice: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Foreign_keys_resolve_once_the_whole_schema_is_applied()
    {
        // Individually valid fragments can still reference a table nobody defines: SQLite
        // accepts a forward reference at CREATE TABLE and only complains on the first write,
        // by which time the diagnosis is far from the cause.
        ApplyWholeSchema();

        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();

        var problems = new List<string>();
        while (reader.Read())
        {
            problems.Add(reader.GetString(0));
        }

        Assert.True(problems.Count == 0,
            $"unresolved foreign keys after applying the whole schema: {string.Join(", ", problems)}");
    }

    [Fact]
    public void The_schema_declares_the_objects_it_claims_to()
    {
        ApplyWholeSchema();

        var declared = SpecDocument.SchemaObjects.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' AND sql IS NOT NULL;";
        using var reader = command.ExecuteReader();

        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            actual.Add(reader.GetString(0));
        }

        Assert.Equal(declared.OrderBy(n => n, StringComparer.Ordinal),
                     actual.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("roadmaps")]
    [InlineData("roadmap_items")]
    public void A_roadmap_stores_no_status_because_where_an_item_stands_is_derived(string table)
    {
        // SPEC.md 5.7: state is derived from the item's conversation when read and written nowhere. A
        // column for it would be a second route that could disagree with the first, and every surface
        // would then have to choose which to believe.
        ApplyWholeSchema();

        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.NotEmpty(columns);
        Assert.DoesNotContain(columns, c => c.Contains("status", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("state", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("done", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("waiting", "'2026-10-06T10:00:00.0000000Z'", false)]
    [InlineData("confirmed", "NULL", false)]
    [InlineData("waiting", "NULL", true)]
    [InlineData("declined", "'2026-10-06T10:00:00.0000000Z'", true)]
    public void A_proposal_is_decided_exactly_when_it_has_a_decision_time(
        string state, string decided, bool accepted)
    {
        // The pairing is written with IS rather than =, because a NULL decided_utc makes = yield NULL and
        // a CHECK admits NULL: confirmed with no decision time would otherwise be stored.
        ApplyWholeSchema();

        // The proposer is not registered here, so without this every row would be refused by the foreign
        // key and the rejecting cases would pass for a reason that is not the rule under test.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO roadmap_proposals (proposer, title, size, state, created_utc, decided_utc) " +
            $"VALUES ('orders@test', 'A roadmap', 9, '{state}', '2026-10-06T09:00:00.0000000Z', {decided});";

        if (accepted)
        {
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        else
        {
            var refused = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.Contains("CHECK", refused.Message, StringComparison.Ordinal);
        }
    }

    private void ApplyWholeSchema()
    {
        foreach (var (_, _, sql) in SpecDocument.SchemaObjects)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
