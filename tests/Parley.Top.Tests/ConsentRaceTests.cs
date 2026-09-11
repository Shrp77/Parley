using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the screen's promise about a catalogue cannot outlive the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding now reads the deployment twice, and this is the gap between the reads.</b> The
/// confirmation says which files it is about to write, which means asking — before anything is
/// written — whether another project already starts this repository. Somebody then reads that
/// description, and a catalogue is a file: another project can take the consent while they are
/// reading it.
/// </para>
/// <para>
/// The dangerous direction is this one. Nothing held the workspace when the description was
/// written, so the description promised a catalogue line and the write asks for an unattended
/// start — which, if it went through, would be the second one FR-013 exists to prevent, and
/// SPEC.md's own reason for it is that a node starts a client with the repository as its working
/// directory. What makes that safe is that the refusal lives in <c>Enrolment.AddAsync</c> rather
/// than on the screen, so no surface can route around it by having asked earlier.
/// </para>
/// <para>
/// The other direction costs nothing: a consent that has gone away by the time of the write leaves
/// an attended entry where an unattended one would have been allowed, which is a missed
/// opportunity rather than a wrong file.
/// </para>
/// </remarks>
public sealed class ConsentRaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-consent-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not go is not a failure of the thing tested.
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string CatalogueFor(string name) => Path.Combine(_root, name + "-node.json");

    private ProjectSettings Project(string name, int port) =>
        new(ProjectName.Create(name), Path.Combine(_root, name + ".db"),
            $"http://127.0.0.1:{port}", CatalogueFor(name), null);

    [Fact]
    public async Task A_consent_taken_while_the_confirmation_is_read_still_refuses_the_second_start()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        var token = TestContext.Current.CancellationToken;
        var manifest = Path.Combine(_root, "projects.json");
        var deployment = new[] { Project("alpha", 1), Project("beta", 2) };

        ProjectManifest.Write(manifest, deployment);

        await File.WriteAllTextAsync(CatalogueFor("alpha"), """{ "agents": {} }""", token);
        await File.WriteAllTextAsync(CatalogueFor("beta"), """{ "agents": {} }""", token);

        // Nothing holds the workspace, so the description promises a catalogue line.
        Assert.Null(Enrolment.StartedElsewhere(deployment, Workspace, CatalogueFor("beta")));

        // Alpha takes it while somebody is reading that description. The command matters: a
        // catalogue entry with no way to launch it is not a role anything can start, and a probe
        // written without one reported this whole check as absent when it is not.
        await File.WriteAllTextAsync(
            CatalogueFor("alpha"),
            $$"""
              { "agents": { "orders": { "command": "claude", "workspace": {{System.Text.Json.JsonSerializer.Serialize(Workspace)}} } } }
              """,
            token);

        Assert.Equal("alpha", Enrolment.StartedElsewhere(deployment, Workspace, CatalogueFor("beta"))?.Project);

        // And the write goes ahead asking for exactly what the description promised.
        var entry = new EnrollmentEntry(Workspace, "orders", null, Unattended: true) { Enroll = true };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Enrolment.AddAsync(
                entry, "beta", CatalogueFor("beta"), Path.Combine(Workspace, "CLAUDE.md"),
                token, deployment, manifest));

        // Nothing at all, which is the promise the whole command rests on: a half-enrolment reads
        // as a working agent.
        Assert.DoesNotContain(
            "orders",
            await File.ReadAllTextAsync(CatalogueFor("beta"), token),
            StringComparison.Ordinal);

        Assert.False(
            File.Exists(Path.Combine(Workspace, ".mcp.json")),
            "a refused enrolment wrote the launch configuration");
    }
}
