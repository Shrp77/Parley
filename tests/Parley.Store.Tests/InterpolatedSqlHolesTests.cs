using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>Holds "Parameterised SQL" where CA2100 cannot: a string handed to Dapper.</summary>
/// <remarks>
/// CA2100 reads <c>IDbCommand.CommandText</c> and nothing else, so <c>connection.Execute($"... {word}")</c> builds and
/// passes. Every hole in an interpolated SQL string in this assembly is a name from the list below, each a constant or a
/// column or table name the schema itself supplied; a hole that is anything else fails here.
/// </remarks>
[Integration]
public sealed class InterpolatedSqlHolesTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "columns", "sets", "wheres", "table", "table.Key", "reference.TableName", "reference.ColumnName",
        "KeyColumn", "NoticeKey", "SelectColumns", "TallyColumns", "version", "Columns", "where", "group", "PendingPredicate",
        "SilentVoters", "ConversationRepository.SilentVoters", "OutstandingPolls", "Facilitation.Adapter",
    };

    private static readonly Regex Interpolated = new(
        "\\$\"\"\"[\\s\\S]*?\"\"\"|\\$@?\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly Regex Sql = new(
        "\\b(SELECT|INSERT|UPDATE|DELETE|PRAGMA|FROM|WHERE|CREATE|DROP|ALTER)\\b", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex Hole = new("(?<!\\{)\\{(?<expr>[^{}:,]+)(?:[:,][^{}]*)?\\}", RegexOptions.None, TimeSpan.FromSeconds(1));

    [Fact]
    public void Every_hole_in_an_interpolated_statement_is_a_name_the_schema_or_a_constant_supplies()
    {
        var loose = new List<string>();
        var statements = 0;

        foreach (var file in Directory.EnumerateFiles(RepositoryPath("src/Parley.Store"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match literal in Interpolated.Matches(File.ReadAllText(file)))
            {
                if (!Sql.IsMatch(literal.Value))
                {
                    continue;
                }

                statements++;

                loose.AddRange(Hole.Matches(literal.Value)
                    .Select(hole => hole.Groups["expr"].Value.Trim())
                    .Where(expr => !Allowed.Contains(expr))
                    .Select(expr => $"{Path.GetFileName(file)}: {{{expr}}}"));
            }
        }

        Assert.True(statements > 5, "the scan found no interpolated statements");
        Assert.True(loose.Count == 0, "SQL built from a value: " + string.Join(", ", loose));
    }

    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
