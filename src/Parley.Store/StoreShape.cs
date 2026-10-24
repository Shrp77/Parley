using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// Whether a store has the shape this build expects, which its schema number cannot say.
/// </summary>
/// <remarks>
/// <para>
/// <b>The number is the wrong witness.</b> It records which migrations a store was given, not what it holds. A store
/// changed by hand, or made by a build whose migration 001 differed from this one's, reads as current and may lack
/// what this build relies on. Migration 001 is now fixed (<c>SchemaOneIsFrozenTests</c>), so the second cannot recur. What is compared here is the
/// thing itself: every table, index and trigger this build's schema creates, against what the file holds.
/// </para>
/// <para>
/// <b>Reported and never repaired.</b> A store with a table missing needs a person to decide how to bring it to shape, and a broker that quietly altered a database it was asked to serve would be making that decision.
/// Whitespace is ignored when comparing a definition, because SQLite keeps the text it was given and a checkout's line
/// endings are not a difference in shape. Objects the file has and this build does not are not reported:
/// they are harmless, and reporting them would only teach a person to ignore the list.
/// </para>
/// </remarks>
public static partial class StoreShape
{
    /// <summary>What is wrong with a store, in words a person can act on.</summary>
    /// <param name="connection">An open connection to the store.</param>
    /// <returns>One line per difference, or nothing when the store has the shape this build expects.</returns>
    public static IReadOnlyList<string> Problems(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var expected = ParleyDatabase.OpenInMemory();

        var wanted = Objects(expected);
        var held = Objects(connection);

        var problems = new List<string>();

        foreach (var (key, definition) in wanted.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            if (!held.TryGetValue(key, out var found))
            {
                problems.Add($"missing {key.Kind} {key.Name}");
            }
            else if (!string.Equals(found, definition, StringComparison.Ordinal))
            {
                problems.Add($"{key.Kind} {key.Name} is defined differently from this build's");
            }
        }

        return problems;
    }

    private static Dictionary<(string Kind, string Name), string> Objects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite!_%' ESCAPE '!' AND sql IS NOT NULL";

        var found = new Dictionary<(string, string), string>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            found[(reader.GetString(0), reader.GetString(1))] = Whitespace().Replace(reader.GetString(2), " ").Trim();
        }

        return found;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
