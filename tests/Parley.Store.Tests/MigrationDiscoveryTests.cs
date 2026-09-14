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
public sealed class MigrationDiscoveryTests
{
    /// <summary>How many migrations this build is expected to carry.</summary>
    /// <remarks>
    /// Bumped deliberately when one is added, which is the point: the number is the assertion,
    /// and a migration that appears without anybody deciding it should is what this catches.
    /// </remarks>
    private const int Expected = 8;

    [Fact]
    public void Every_migration_in_the_folder_is_embedded_and_reachable()
    {
        Assert.Equal(Expected, SchemaMigrator.LatestVersion);
    }
}
