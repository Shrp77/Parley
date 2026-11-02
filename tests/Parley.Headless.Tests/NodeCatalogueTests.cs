using Parley.Configuration;
using Parley.Core;
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
[Integration]
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

    /// <summary>What a catalogue offers a project that accepts every claim.</summary>
    /// <param name="catalogue">The catalogue.</param>
    /// <returns>The names, which is what a node announces.</returns>
    /// <remarks>
    /// These tests asked for the catalogue's roles, and a catalogue has none: it is keyed by identity and
    /// says nothing about what any project calls a checkout. The name comes back out of the repository, so
    /// asking this way is also what exercises the lookup a node actually performs.
    /// </remarks>
    private static IReadOnlyList<string> Names(NodeCatalogue catalogue) =>
        catalogue.Announceable(project: null).Names;

    /// <summary>Writes a catalogue naming one checkout, and the checkout that answers to a name.</summary>
    /// <param name="name">The catalogue file's name, inside this test's own directory.</param>
    /// <param name="role">What the repository declares it is called.</param>
    /// <returns>The catalogue file's path.</returns>
    private string Write(string name, string role)
    {
        var path = Path.Combine(_root, name);
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "ws-" + role);

        Directory.CreateDirectory(workspace);

        File.WriteAllText(
            Path.Combine(workspace, LaunchConfiguration.FileName),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{role}}" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """);

        File.WriteAllText(
            path,
            $$"""
            {
              "agents": {
                "{{identity}}": {
                  "command": "claude",
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                }
              }
            }
            """);

        return path;
    }

    [Fact]
    public void The_named_file_is_the_one_read()
    {
        var catalogue = NodeCatalogue.ReadFrom(Write("named.json", "orders"));

        Assert.Contains("orders", Names(catalogue), StringComparer.Ordinal);
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

        Assert.Contains("orders", Names(catalogue), StringComparer.Ordinal);
        Assert.DoesNotContain("billing", Names(catalogue), StringComparer.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_there_is_a_machine_that_starts_nothing()
    {
        // Not an exception, deliberately: the failure is "nothing is started automatically",
        // which somebody notices, rather than "the supervisor will not run", which takes a
        // deployment down over a path typed wrong. What makes that survivable is the node naming
        // the file when it reports what it can start.
        var catalogue = NodeCatalogue.ReadFrom(Path.Combine(_root, "absent.json"));

        Assert.Empty(catalogue.Identities);
        Assert.Empty(Names(catalogue));
    }

    [Fact]
    public void A_blank_path_is_refused_rather_than_resolved_to_something()
    {
        // The one case where guessing would put the old defect back: a caller with nothing to
        // pass must be made to fix that, not quietly handed the working directory.
        _ = Assert.Throws<ArgumentException>(() => NodeCatalogue.ReadFrom("  "));
    }

    [Fact]
    public void A_key_that_is_not_an_identity_is_reported_rather_than_swallowed()
    {
        // **The silence this feature exists to remove, in its newest form.** A catalogue written by a
        // version that keyed by role reads as a machine that can start nothing -- and a machine that can
        // start nothing joins successfully to say so, in a log nobody reads.
        var path = Path.Combine(_root, "old.json");

        File.WriteAllText(
            path,
            """
            { "agents": { "orders": { "command": "claude", "workspace": "/srv/orders" } } }
            """);

        var catalogue = NodeCatalogue.ReadFrom(path);

        Assert.Empty(catalogue.Identities);
        Assert.Equal(["orders"], catalogue.Unreadable);
    }
}
