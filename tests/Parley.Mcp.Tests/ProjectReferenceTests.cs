using System.Globalization;
using Parley.Core;
using Parley.Configuration;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A launch configuration that names a project instead of an address.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint is a port number and ports move. A committed <c>.mcp.json</c> naming a project
/// survives that; one naming <c>127.0.0.1:51707</c> has to be edited in every repository at once,
/// and the repository that gets missed connects to whatever now answers there. That is the whole
/// case for this, and it is worth stating because the feature reads as indirection for its own
/// sake otherwise.
/// </para>
/// <para>
/// <b>What must not change is who the agent is.</b> SPEC.md §4.1 makes membership of a project
/// exactly which endpoint an agent reaches — the protocol has no concept of a project and must not
/// acquire one. So a project supplies an address and nothing else, and the assertions that
/// <c>PARLEY_AGENT</c> and <c>PARLEY_OWNS</c> come through untouched are not padding: identity
/// fixed by the launch environment is the guarantee the entire trust model rests on, and this is
/// the first thing in a long time to be added to that environment.
/// </para>
/// <para>
/// Real manifest files rather than an injected reader, because the shape being tested is a file
/// somebody hand-edits. Every case sets <c>PARLEY_CONFIG</c> through the injected variable reader,
/// so nothing here can read the manifest of the machine running the suite — and nothing mutates
/// the process environment, which the test host runs classes in parallel against.
/// </para>
/// </remarks>
public sealed class ProjectReferenceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-mcp-projects-" + Guid.NewGuid().ToString("N"));

    public ProjectReferenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static (string, string)[] Minimal =>
        [(ServerOptions.AgentVariable, "orders@test")];

    /// <summary>Writes one project's configuration verbatim, and returns the root.</summary>
    /// <param name="name">The project, which is also its folder's name.</param>
    /// <param name="body">The file's contents, verbatim.</param>
    /// <returns>The root, which is what <c>PARLEY_CONFIG</c> names.</returns>
    /// <remarks>
    /// Verbatim rather than through the writer, because what these probe is a file the writer would
    /// refuse — a half-finished hand edit, or one written by a version this does not know.
    /// </remarks>
    private string Manifest(string name, string body)
    {
        var folder = ProjectFolder.For(_directory, ProjectName.Create(name));

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ProjectFolder.ConfigurationFileName), body);

        return _directory;
    }

    /// <summary>Writes a manifest describing one project at an address.</summary>
    /// <param name="name">The project's name.</param>
    /// <param name="endpoint">Where its broker listens.</param>
    /// <returns>The path.</returns>
    private string ManifestFor(string name, string endpoint) =>
        Manifest(name, string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              {
                "endpoint": "{{endpoint}}"
              }
              """));

    [Fact]
    public void An_endpoint_and_no_project_behaves_exactly_as_it_did_before()
    {
        // The parity case. Every deployment configured before any of this existed passes through
        // here, and it must not so much as look for a manifest.
        var options = ServerOptions.Read(
            Env([.. Minimal, (ServerOptions.BrokerVariable, "http://broker.example:51707")]));

        Assert.Equal("http://broker.example:51707", options.Broker);
        Assert.Null(options.BrokerNotice);
    }

    [Fact]
    public void A_project_resolves_to_the_endpoint_the_manifest_gives_it()
    {
        var manifest = ManifestFor("orders", "http://127.0.0.1:51801");

        var options = ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, manifest),
        ]));

        Assert.Equal("http://127.0.0.1:51801", options.Broker);
        Assert.Null(options.BrokerNotice);
    }

    [Fact]
    public void An_endpoint_and_a_project_that_agree_pass_without_comment()
    {
        var manifest = ManifestFor("orders", "http://127.0.0.1:51801");

        var options = ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.BrokerVariable, "http://127.0.0.1:51801"),
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, manifest),
        ]));

        Assert.Equal("http://127.0.0.1:51801", options.Broker);
    }

    [Fact]
    public void An_endpoint_wins_over_a_project_and_the_override_is_reported()
    {
        // The precedence is the same one used everywhere else, and the reporting is the part that
        // matters. Both set and disagreeing is a half-finished edit, and the silent half of that
        // is how an agent joins the wrong deployment with no line anywhere that looks wrong.
        var manifest = ManifestFor("orders", "http://127.0.0.1:51801");

        var options = ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.BrokerVariable, "http://127.0.0.1:51999"),
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, manifest),
        ]));

        Assert.Equal("http://127.0.0.1:51999", options.Broker);

        Assert.Contains("orders", options.BrokerNotice, StringComparison.Ordinal);
        Assert.Contains("51999", options.BrokerNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_project_refuses_and_never_falls_back()
    {
        // The case worth writing rather than reasoning about. Falling back to loopback here
        // connects the agent to whatever is listening — plausibly a working broker, belonging to
        // another project, with a roster this agent has no business in. Nothing on either side of
        // a message would look wrong.
        var manifest = ManifestFor("orders", "http://127.0.0.1:51801");

        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.ProjectVariable, "billing"),
            (ProjectManifest.PathVariable, manifest),
        ])));

        Assert.Contains("billing", ex.Message, StringComparison.Ordinal);
        Assert.Contains(manifest, ex.Message, StringComparison.Ordinal);

        // Names what the machine does have, because the usual cause is a typo and the usual
        // remedy is visible in that list.
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);

        // And says nothing that could be mistaken for having connected somewhere.
        Assert.DoesNotContain(Parley.Core.BrokerEndpoint.Default, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_named_on_a_machine_with_no_manifest_refuses()
    {
        // Absent is not an error for a launch that names no project — that machine is an ordinary
        // one. It is an error for a launch that does, because the name cannot mean anything.
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, Path.Combine(_directory, "absent.json")),
        ])));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("`parley`",ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_refuses_rather_than_reading_as_empty()
    {
        // The third time this distinction has had to be made in this codebase, and the first two
        // were data loss: an unparseable node catalogue read as "no agents" and overwritten, and
        // a wrong-typed one cast away and replaced. Here it would be a wrong connection instead —
        // the manifest read as describing nothing, the project unknown, and every candidate
        // remedy pointing at the wrong file.
        var manifest = Manifest("orders", "{ \"endpoint\": ");

        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, manifest),
        ])));

        Assert.Contains(manifest, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("describes no projects", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_of_the_wrong_shape_refuses_too()
    {
        var manifest = Manifest("orders", "[\"not\", \"an\", \"object\"]");

        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, manifest),
        ])));

        Assert.Contains("does not hold an object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_a_project_does_not_write_to_the_manifest()
    {
        // The adapter reads this file and must never do anything else to it. It is the only file
        // naming every store on the machine, `parley project add` is an operator command, and an
        // MCP server is launched by a client at times nobody chose — so an adapter that could
        // write here would be an adapter able to reconfigure the deployment while a person was
        // editing it.
        //
        // Asserted behaviourally rather than by reflecting for a method, because the linker keeps
        // ProjectManifest.Write in this binary whether or not anything calls it: what is worth
        // pinning is that the launch path leaves the bytes and the timestamp alone.
        var root = ManifestFor("orders", "http://127.0.0.1:51801");
        var configuration = ProjectFolder.ConfigurationFor(root, ProjectName.Create("orders"));

        var before = File.ReadAllBytes(configuration);
        var written = File.GetLastWriteTimeUtc(configuration);

        ServerOptions.Read(Env(
        [
            .. Minimal,
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, root),
        ]));

        Assert.Equal(before, File.ReadAllBytes(configuration));
        Assert.Equal(written, File.GetLastWriteTimeUtc(configuration));

        // And nothing beside it either — the write path leaves a sibling behind while it works, and the
        // claim would leave a port record. **Both halves matter more now than they did**: an adapter that
        // could take the port hold would be able to stall every `project add` on the machine, from a
        // process a client launches at times nobody chose.
        Assert.Equal(
            [ProjectFolder.ConfigurationFileName],
            Directory.GetFiles(ProjectFolder.For(root, ProjectName.Create("orders")))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));

        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void Naming_a_project_changes_nothing_about_who_the_agent_is()
    {
        // FR-024, and the reason it is a requirement rather than an observation. Identity comes
        // from the launch environment and no operation may edit it; a project supplies an address.
        // If a project could ever supply a name or an ownership line, SPEC.md §13's guarantee that
        // a sender cannot forge the agent it claims to be would move from the launch configuration
        // into a file the agent's own repository does not hold.
        var manifest = ManifestFor("orders", "http://127.0.0.1:51801");

        var options = ServerOptions.Read(Env(
        [
            (ServerOptions.AgentVariable, "billing"),
            (ServerOptions.OwnsVariable, "invoicing and dunning"),
            (ServerOptions.WorkspaceVariable, "C:/work/billing"),
            (ServerOptions.ProjectVariable, "orders"),
            (ProjectManifest.PathVariable, manifest),
        ]));

        // The agent is billing, in a project called orders, and neither fact touches the other.
        // Names collide freely across projects for the same reason — SPEC.md §4.1.
        Assert.Equal("billing@test", options.Agent.Value);
        Assert.Equal("invoicing and dunning", options.Owns);
        Assert.Equal("C:/work/billing", options.Workspace);
        Assert.Equal("http://127.0.0.1:51801", options.Broker);
    }
}
