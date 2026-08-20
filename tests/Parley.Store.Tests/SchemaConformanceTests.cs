using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Compares the schema the migration builds against the schema SPEC.md documents.
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md says a disagreement between code and spec is a defect in one of them, and which
/// one is a decision rather than an assumption. That rule is only useful if disagreements are
/// noticed, and until now they were noticed by reading two files side by side — which is how
/// the implementation came to be five pull requests behind the document without anything
/// saying so.
/// </para>
/// <para>
/// This compares structure, not text. The migration carries comments, ordering and performance
/// indexes the specification has no reason to describe, and demanding textual identity would
/// make the check fail for reasons nobody should act on. What must agree is the shape: the same
/// tables, the same columns with the same types and nullability, and the same triggers.
/// </para>
/// <para>
/// Indexes are deliberately one-directional. The migration may add any index it likes, because
/// an index is a performance decision and §9.2 documents invariants; it may not omit one the
/// document names, because those carry constraints.
/// </para>
/// </remarks>
public sealed class SchemaConformanceTests : IDisposable
{
    private readonly SqliteConnection _fromSpec = new("Data Source=:memory:");
    private readonly SqliteConnection _fromMigration;

    public SchemaConformanceTests()
    {
        _fromSpec.Open();
        foreach (var fragment in SpecSchema.Fragments)
        {
            using var command = _fromSpec.CreateCommand();
            command.CommandText = fragment;
            command.ExecuteNonQuery();
        }

        _fromMigration = ParleyDatabase.OpenInMemory();
    }

    public void Dispose()
    {
        _fromSpec.Dispose();
        _fromMigration.Dispose();
    }

    [Fact]
    public void The_same_tables_exist()
    {
        Assert.Equal(Names(_fromSpec, "table"), Names(_fromMigration, "table"));
    }

    [Fact]
    public void The_same_triggers_exist()
    {
        Assert.Equal(Names(_fromSpec, "trigger"), Names(_fromMigration, "trigger"));
    }

    [Fact]
    public void Every_index_the_document_names_exists()
    {
        var documented = Names(_fromSpec, "index");
        var built = Names(_fromMigration, "index");

        var missing = documented.Except(built, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            $"SPEC.md §9.2 names indexes the migration does not create: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_enumerated_value_agrees()
    {
        // Columns and types were not enough. `retired` reached the document in five places and
        // the schema in none, and this suite passed throughout, because a CHECK constraint is
        // not part of a column's shape. Enum drift is both the likeliest divergence and the
        // most damaging: a status the document defines and the schema refuses fails at runtime,
        // on the one write that needed it.
        //
        // Literals inside CHECK constraints rather than the constraints themselves, because the
        // migration is free to lay its SQL out differently and a check that fires on formatting
        // is one people learn to ignore.
        Assert.Equal(CheckLiterals(_fromSpec), CheckLiterals(_fromMigration));
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("agent_credentials")]
    [InlineData("exchanges")]
    [InlineData("conversations")]
    [InlineData("conversation_participants")]
    [InlineData("messages")]
    public void Every_column_agrees(string table)
    {
        Assert.Equal(Columns(_fromSpec, table), Columns(_fromMigration, table));
    }

    /// <summary>Every quoted literal appearing inside a CHECK constraint, per object.</summary>
    private static IReadOnlyList<string> CheckLiterals(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';";

        using var reader = command.ExecuteReader();
        var literals = new List<string>();

        while (reader.Read())
        {
            var name = reader.GetString(0);
            var sql = reader.GetString(1);

            foreach (Match check in Regex.Matches(sql, @"CHECK\s*\((?:[^()]|\([^()]*\))*\)"))
            {
                foreach (Match literal in Regex.Matches(check.Value, @"'([^']*)'"))
                {
                    literals.Add($"{name}:{literal.Groups[1].Value}");
                }
            }
        }

        literals.Sort(StringComparer.Ordinal);
        return literals;
    }

    private static IReadOnlyList<string> Names(SqliteConnection connection, string type)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_schema WHERE type = $type AND sql IS NOT NULL " +
            "AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);

        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Column name, declared type and nullability, as a comparable string.</summary>
    /// <remarks>
    /// Default values are excluded. The migration may reasonably spell a default differently
    /// from the document without the two disagreeing about anything, and a check that fires on
    /// that is a check people learn to ignore.
    /// </remarks>
    private static IReadOnlyList<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var type = reader.GetString(2);
            var notNull = reader.GetBoolean(3) ? " NOT NULL" : string.Empty;
            columns.Add($"{name} {type}{notNull}");
        }

        columns.Sort(StringComparer.Ordinal);
        return columns;
    }
}

/// <summary>
/// The schema fragments written in SPEC.md §9.2.
/// </summary>
/// <remarks>
/// Duplicated from Parley.Spec.Tests rather than shared. That project deliberately references
/// neither Core nor Store, and a shared helper would have to live somewhere both can see —
/// which means a project this one references, undoing the separation for the sake of thirty
/// lines. The extraction is covered by tests over there.
/// </remarks>
internal static class SpecSchema
{
    public static IReadOnlyList<string> Fragments { get; } = Extract();

    private static IReadOnlyList<string> Extract()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPEC.md")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("SPEC.md not found above the test assembly.");
        }

        var text = File.ReadAllText(Path.Combine(dir.FullName, "SPEC.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var fragments = Regex.Matches(
                text,
                @"^CREATE TRIGGER\b.*?^END;$|^CREATE (?:TABLE|UNIQUE INDEX|INDEX)\b.*?;$",
                RegexOptions.Singleline | RegexOptions.Multiline)
            .Select(m => m.Value)
            .ToList();

        if (fragments.Count < 10)
        {
            throw new InvalidOperationException(
                $"only {fragments.Count} fragments extracted from SPEC.md; extraction is broken.");
        }

        return fragments;
    }
}
