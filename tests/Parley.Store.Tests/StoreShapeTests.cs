using Dapper;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a store which is not the shape this build expects is noticed, which its schema number cannot do.
/// </summary>
/// <remarks>
/// <b>A store with a missing table must not read as fine while every number says it is.</b> A store's number records
/// the migrations it was given, not what it holds, so a store changed by hand reads as current. Each of these fails
/// against comparing the numbers.
/// </remarks>
public sealed class StoreShapeTests
{
    [Fact]
    public void A_store_made_by_this_build_has_no_problems()
    {
        using var connection = ParleyDatabase.OpenInMemory();

        Assert.Empty(StoreShape.Problems(connection));
    }

    [Fact]
    public void A_missing_table_is_named()
    {
        // Must fail against checking the schema number: the number is still current.
        using var connection = ParleyDatabase.OpenInMemory();

        connection.Execute("DROP TABLE roadmap_notices;");

        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetUserVersion(connection));
        Assert.Contains("missing table roadmap_notices", StoreShape.Problems(connection));
    }

    [Fact]
    public void A_missing_index_is_named_as_well_as_a_missing_table()
    {
        using var connection = ParleyDatabase.OpenInMemory();

        connection.Execute("DROP INDEX ix_roadmap_notices_collection;");

        Assert.Contains("missing index ix_roadmap_notices_collection", StoreShape.Problems(connection));
    }

    [Fact]
    public void A_definition_that_differs_is_named()
    {
        // The case the missing-object check cannot see: the object is there, and it is the old one.
        using var connection = ParleyDatabase.OpenInMemory();

        connection.Execute("DROP INDEX ix_roadmap_notices_collection;");
        connection.Execute("CREATE INDEX ix_roadmap_notices_collection ON roadmap_notices(agent);");

        Assert.Contains(
            "index ix_roadmap_notices_collection is defined differently from this build's",
            StoreShape.Problems(connection));
    }

    [Fact]
    public void An_object_this_build_does_not_have_is_left_alone()
    {
        // A leftover table nobody reads is harmless, and listing it would teach a person to ignore the list.
        using var connection = ParleyDatabase.OpenInMemory();

        connection.Execute("CREATE TABLE left_over (id INTEGER PRIMARY KEY);");

        Assert.Empty(StoreShape.Problems(connection));
    }
}
