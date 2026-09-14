using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That refusing a second unattended start names the way past it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner met this from the terminal, where it was a dead end, and asked for a repository to
/// answer in two projects.</b> It never was a dead end here: <c>enroll apply</c> reads
/// <c>"unattended": false</c> off the manifest row and writes the launch entry without a catalogue
/// line, which is exactly what the second project should get. What made it look like one is that
/// the refusal stated the rule and said nothing about the flag that satisfies it.
/// </para>
/// <para>
/// <b>The advice is at the call site rather than in <c>Catalogues.Refusal</c>, which is shared.</b>
/// <c>Enrolment.AddAsync</c> raises the same refusal for the terminal, and there this advice would
/// be wrong — that surface has no manifest row to edit and makes the choice for the person. The
/// rule belongs in one place and the remedy belongs where it can be acted on.
/// </para>
/// </remarks>
public sealed class SecondProjectAdviceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-advice-").FullName;

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

    [Fact]
    public async Task Enroll_apply_refusing_a_second_unattended_start_names_the_flag()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        var token = TestContext.Current.CancellationToken;
        var manifestPath = Path.Combine(_root, "projects.json");

        var deployment = new[] { "alpha", "beta" }
            .Select((name, at) => new ProjectSettings(
                ProjectName.Create(name), Path.Combine(_root, name + ".db"),
                $"http://127.0.0.1:{9300 + at}", CatalogueFor(name), null))
            .ToList();

        ProjectManifest.Write(manifestPath, deployment);

        // Alpha already starts this workspace; beta is empty.
        await File.WriteAllTextAsync(
            CatalogueFor("alpha"),
            $$"""
              { "agents": { "orders": { "command": "claude", "workspace": {{System.Text.Json.JsonSerializer.Serialize(Workspace)}} } } }
              """,
            token);

        await File.WriteAllTextAsync(CatalogueFor("beta"), """{ "agents": {} }""", token);

        var enrolment = Path.Combine(_root, "enrolment.json");

        await File.WriteAllTextAsync(
            enrolment,
            $$"""
              {
                "node_config": {{System.Text.Json.JsonSerializer.Serialize(CatalogueFor("beta"))}},
                "agents": [
                  {
                    "workspace": {{System.Text.Json.JsonSerializer.Serialize(Workspace)}},
                    "agent": "orders",
                    "enroll": true,
                    "unattended": true
                  }
                ]
              }
              """,
            token);

        var output = new StringWriter();
        var error = new StringWriter();

        _ = await EnrollCommand.RunAsync(
            ["apply", enrolment],
            selectAll: null,
            new Resolved(CatalogueFor("beta"), ValueSource.Argument),
            output,
            error,
            token,
            deployment[1],
            deployment,
            manifestPath);

        var said = error.ToString();

        // The rule, which was already there.
        Assert.Contains("unattended by only one", said, StringComparison.Ordinal);

        // And the way past it, which was not.
        Assert.Contains("\"unattended\": false", said, StringComparison.Ordinal);

        // Nothing written, as every refusal here promises.
        Assert.False(
            File.Exists(Path.Combine(Workspace, ".mcp.json")),
            "a refused run wrote the launch configuration");
    }
}
