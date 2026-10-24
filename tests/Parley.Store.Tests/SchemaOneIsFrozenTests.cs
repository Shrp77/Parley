using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That schema 1 is the schema the live stores hold, and is never edited again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live stores are kept across upgrades.</b> A new build is installed over a deployment that holds work in flight,
/// and the only way to change the schema under it is a migration that upgrades the store in place. An edit to
/// <c>001_initial.sql</c> would change what a fresh store holds and leave every live store as it was, under the same
/// version number: exactly the drift that made one live store an older "version 1" than another.
/// </para>
/// <para>
/// <b>Pinned on what a database holds, not on the file.</b> SQLite keeps the text of every table, index and trigger,
/// comments inside them included, and <see cref="StoreShape"/> compares that text. So any change to it is a change in
/// shape, and is caught. Comments before the first statement never reach a database, so the file's header can still
/// be corrected without moving this.
/// </para>
/// </remarks>
public sealed partial class SchemaOneIsFrozenTests
{
    /// <summary>The SHA-256 of what migration 001 creates, as <see cref="Shape"/> lists it.</summary>
    /// <remarks>
    /// Never updated. Both live stores were hashed the same way on 2026-10-24 and gave this value, so it is what they
    /// hold and not only what this file says. A change to the schema is a new migration, 002 onwards.
    /// </remarks>
    private const string Pinned = "836ac822147bd15f2302cb1cd6da9008fc810e26edd727738a1cba20633efd7f";

    [Fact]
    public void Migration_001_creates_exactly_what_the_live_stores_hold()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = MigrationOne();
            command.ExecuteNonQuery();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Shape(connection))));

        Assert.True(
            string.Equals(Pinned, hash, StringComparison.Ordinal),
            $"001_initial.sql now creates a different schema ({hash}). It is schema 1 as the live stores hold it and is "
            + "never edited: put the change in a new migration, 002 onwards, that upgrades a store in place.");
    }

    [Fact]
    public void A_change_inside_a_definition_moves_the_shape()
    {
        // The witness is worth nothing if it cannot see an edit. Must fail against hashing only names, or only columns.
        var original = MigrationOne();
        var edited = original.Replace("CREATE TABLE nodes (", "CREATE TABLE nodes ( -- reworded", StringComparison.Ordinal);

        Assert.NotEqual(original, edited);
        Assert.NotEqual(ShapeOf(original), ShapeOf(edited));
    }

    /// <summary>Every object a database holds, one line each, in a fixed order, with whitespace collapsed.</summary>
    /// <remarks>Collapsed the way <see cref="StoreShape"/> does, so a checkout's line endings are not a difference.</remarks>
    private static string Shape(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite!_%' ESCAPE '!' AND sql IS NOT NULL";

        var lines = new List<string>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            lines.Add(reader.GetString(0) + " " + reader.GetString(1) + " " + Whitespace().Replace(reader.GetString(2), " ").Trim());
        }

        lines.Sort(StringComparer.Ordinal);

        return string.Join('\n', lines);
    }

    private static string ShapeOf(string sql)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        return Shape(connection);
    }

    /// <summary>Migration 001 as the build ships it: the embedded resource, not the file in the checkout.</summary>
    private static string MigrationOne()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".001_initial.sql", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
