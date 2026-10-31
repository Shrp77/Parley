using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a migration added to the folder is a migration the build actually ships.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are embedded resources matched by a wildcard in the project file. A file added to
/// the folder and not picked up — a typo in its name, a build that did not re-run, a resource the
/// wildcard does not reach — produces a database that is silently one version behind the code
/// reading it, on the machine that most needs the new column.
/// </para>
/// <para>
/// <see cref="SchemaMigrator.LatestVersion"/> reads the embedded set rather than a written-down
/// number, precisely so it cannot disagree with what <c>Migrate</c> would apply. This asserts
/// against it, so adding a migration without shipping it fails here rather than at a missing
/// column somewhere else.
/// </para>
/// </remarks>
[Unit]
public sealed class MigrationDiscoveryTests
{
    /// <summary>How many migrations this build is expected to carry.</summary>
    /// <remarks>
    /// <para>
    /// Bumped deliberately when one is added, which is the point: the number is the assertion,
    /// and a migration that appears without anybody deciding it should is what this catches.
    /// </para>
    /// <para>
    /// <b>There are four migrations.</b> <c>001_initial.sql</c> holds the whole of schema 1, which live
    /// stores hold and which is never edited (<c>SchemaOneIsFrozenTests</c>); <c>002</c> rebuilds the
    /// proposals table so an id is never used twice, and judges an item's opening by its exchanges.
    /// <c>003</c> adds file problems and <c>004</c> marks when an agent was first asked what it owns and first refreshed.
    /// **The next change to the schema is migration 005**, and this number moving to 5 is how that is noticed.
    /// </para>
    /// </remarks>
    private const int Expected = 4;

    [Fact]
    public void Every_migration_in_the_folder_is_embedded_and_reachable()
    {
        Assert.Equal(Expected, SchemaMigrator.LatestVersion);
    }
}
