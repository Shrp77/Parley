using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// That the catalogue is read from the file it is given, and from no other.
/// </summary>
/// <remarks>
/// <see cref="NodeCatalogue.ReadFrom"/> replaced a reader that consulted
/// <see cref="NodeCatalogue.PathVariable"/> itself. One setting resolved in two places disagrees
/// eventually, and this one disagreed exactly where it could not be seen: under a supervisor,
/// which starts a process with no shell environment, the node fell back to a working directory
/// nobody chose. It joined, reported that it could start nothing, and every agent on the machine
/// stayed startable-and-never-started.
/// </remarks>
public sealed class NodeCatalogueTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-catalogue-" + Guid.NewGuid().ToString("N"));

    public NodeCatalogueTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NodeCatalogue.PathVariable, null);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Write(string name, string role)
    {
        var path = Path.Combine(_root, name);

        File.WriteAllText(
            path,
            $$"""
            { "agents": { "{{role}}": { "command": "claude", "workspace": "/srv/{{role}}" } } }
            """);

        return path;
    }

    [Fact]
    public void The_named_file_is_the_one_read()
    {
        var catalogue = NodeCatalogue.ReadFrom(Write("named.json", "orders"));

        Assert.Contains("orders", catalogue.Roles, StringComparer.Ordinal);
    }

    [Fact]
    public void The_environment_does_not_override_the_file_it_was_handed()
    {
        // The whole fix, asserted directly. Both files exist and hold different agents, so a
        // reader that consulted the variable would pass every other test in this class and fail
        // only this one -- which is the shape the defect had.
        var named = Write("named.json", "orders");
        _ = Write("other.json", "billing");

        Environment.SetEnvironmentVariable(
            NodeCatalogue.PathVariable, Path.Combine(_root, "other.json"));

        var catalogue = NodeCatalogue.ReadFrom(named);

        Assert.Contains("orders", catalogue.Roles, StringComparer.Ordinal);
        Assert.DoesNotContain("billing", catalogue.Roles, StringComparer.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_there_is_a_machine_that_starts_nothing()
    {
        // Not an exception, deliberately: the failure is "nothing is started automatically",
        // which somebody notices, rather than "the supervisor will not run", which takes a
        // deployment down over a path typed wrong. What makes that survivable is the node naming
        // the file when it reports what it can start.
        var catalogue = NodeCatalogue.ReadFrom(Path.Combine(_root, "absent.json"));

        Assert.Empty(catalogue.Roles);
    }

    [Fact]
    public void A_blank_path_is_refused_rather_than_resolved_to_something()
    {
        // The one case where guessing would put the old defect back: a caller with nothing to
        // pass must be made to fix that, not quietly handed the working directory.
        _ = Assert.Throws<ArgumentException>(() => NodeCatalogue.ReadFrom("  "));
    }
}
