using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That the surface writes the manifest and the store through the code that owns them, and never
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.3. The manifest has exactly one writer, and this surface is a
/// second program running in the same process as the first — a screen that assembled a
/// <c>ProjectSettings</c> and wrote it would have its own idea of the rules, and would sidestep the
/// lock that stops two concurrent writers discarding one another's work. That was a real defect
/// once, not a hypothetical one.
/// </para>
/// <para>
/// <b>And the store is not this surface's at all.</b> §4 gives it to the broker process; every fact
/// about a broker here arrives over the transport. A SQLite type reachable from a screen would be
/// that rule broken silently, so it is asserted the way <c>Parley.Web</c>'s equivalent is: by
/// looking, rather than by trusting a review.
/// </para>
/// </remarks>
public sealed class OneWriterTests
{
    private static IEnumerable<FileInfo> Sources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return new DirectoryInfo(Path.Combine(directory!.FullName, "src", "Parley.Cli", "Top"))
            .EnumerateFiles("*.cs");
    }

    [Fact]
    public void No_screen_writes_the_manifest_itself()
    {
        // ProjectManifest.Write is the writer. Everything here goes through ProjectCommand, which
        // is the one caller that takes the lock.
        var offenders = Sources()
            .Where(file => File.ReadAllText(file.FullName)
                .Contains("ProjectManifest.Write", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The manifest has one writer and this is not it. It appears in: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void No_screen_opens_a_database()
    {
        // FR-006. Every fact about a broker arrives over the transport; the two files read from
        // disk are the broker's log and the project's catalogue, and neither is a store.
        foreach (var forbidden in new[] { "SqliteConnection", "ParleyDatabase", "Parley.Store" })
        {
            var offenders = Sources()
                .Where(file => File.ReadAllText(file.FullName)
                    .Contains(forbidden, StringComparison.Ordinal))
                .Select(file => file.Name)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"{forbidden} has no business on a client surface. It appears in: "
                + string.Join(", ", offenders));
        }
    }

    [Fact]
    public void Both_writers_of_a_repository_go_through_the_one_that_enrols()
    {
        // FR-008. Adding and withdrawing write a repository's launch configuration and a project's
        // catalogue, and so does `parley enroll apply`. Two copies of those writes would agree
        // today and differ in six months.
        var offenders = Sources()
            .Where(file => File.ReadAllText(file.FullName) is var text
                && text.Contains(".mcp.json", StringComparison.Ordinal)
                && !file.Name.Equals("Adding.cs", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The launch configuration is written through Enrolment. Its name appears in: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity, and the failure this repository has hit before: a path that stopped
        // resolving would make every case above pass by finding no files at all.
        var sources = Sources().ToList();

        Assert.NotEmpty(sources);
        Assert.Contains(sources, file => file.Name == "Shell.cs");

        // And the scan can find something that is genuinely there.
        Assert.Contains(
            sources,
            file => File.ReadAllText(file.FullName)
                .Contains("ProjectCommand", StringComparison.Ordinal));
    }
}
