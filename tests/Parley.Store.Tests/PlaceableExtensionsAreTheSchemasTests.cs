using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>That the extensions a placed file may have are the same list in the code and in the schema (SPEC.md §9.3).</summary>
/// <remarks>
/// <see cref="SharedFileName.AllowedExtensions"/> says the schema holds the same list, and nothing else ties the two.
/// Core cannot see the store and its tests may not reference it, so the tie is held here, where both are in reach.
/// </remarks>
[Integration]
public sealed class PlaceableExtensionsAreTheSchemasTests : IDisposable
{
    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void The_codes_list_of_extensions_is_the_schemas()
    {
        // Without this an extension dropped from the code, or added to it, left the store taking a different set of names
        // from the one the broker checks, and the code's comment still said they matched (review, lens 3 round 41).
        var table = _connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'shared_files';");

        Assert.NotNull(table);

        var schema = Regex.Matches(table!, @"lower\(name\) GLOB '\*(?<extension>\.[a-z0-9]+)'", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Select(match => match.Groups["extension"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(schema);
        Assert.Equal(schema, SharedFileName.AllowedExtensions.Order(StringComparer.Ordinal));
    }
}
