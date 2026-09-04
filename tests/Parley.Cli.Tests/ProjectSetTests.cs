using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Changing what a named project says, without removing and re-adding it.
/// </summary>
/// <remarks>
/// The case this exists for is a catalogue added after the fact: a project named before its
/// machine started agents for it has no <c>node_config</c>, an autostart entry serving the
/// manifest hosts a node only where one is named, and the only way to add one was to remove the
/// project — which prints a paragraph about what was not deleted, for a change that never meant
/// to delete anything.
/// </remarks>
public sealed class ProjectSetTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-project-set-" + Guid.NewGuid().ToString("N"));

    public ProjectSetTests() => Directory.CreateDirectory(_directory);

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

    private string ManifestPath => Path.Combine(_directory, "projects.json");

    private async Task<(int Code, string Out, string Error)> RunAsync(
        string[] arguments, ProjectFields? fields = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            arguments,
            ManifestPath,
            fields ?? new ProjectFields(null, null, null, null),
            output,
            error,
            TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    private Task<(int Code, string Out, string Error)> AddAsync(string name, int port) =>
        RunAsync(
            ["add", name],
            new ProjectFields(Path.Combine(_directory, name + ".db"), $"http://127.0.0.1:{port}", null, null));

    private IReadOnlyList<Parley.Core.ProjectSettings> Projects()
    {
        Assert.True(ProjectManifest.TryRead(ManifestPath, out var projects, out var problem), problem);

        return projects;
    }

    [Fact]
    public async Task Only_the_fields_given_change()
    {
        await AddAsync("orders", 51901);

        var catalogue = Path.Combine(_directory, "node.json");

        var (code, output, error) = await RunAsync(
            ["set", "orders"], new ProjectFields(null, null, catalogue, "the one that started it all"));

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("Changed 'orders'", output, StringComparison.Ordinal);
        Assert.Contains("next start", output, StringComparison.Ordinal);

        var orders = Assert.Single(Projects());

        Assert.Equal(Path.Combine(_directory, "orders.db"), orders.Store);
        Assert.Equal("http://127.0.0.1:51901", orders.Endpoint);
        Assert.Equal(catalogue, orders.NodeConfig);
        Assert.Equal("the one that started it all", orders.Description);
    }

    [Fact]
    public async Task An_empty_value_clears_an_optional_field()
    {
        await AddAsync("orders", 51902);
        await RunAsync(["set", "orders"], new ProjectFields(null, null, Path.Combine(_directory, "node.json"), null));

        var (code, _, _) = await RunAsync(["set", "orders"], new ProjectFields(null, null, string.Empty, null));

        Assert.Equal(0, code);
        Assert.Null(Assert.Single(Projects()).NodeConfig);
    }

    [Fact]
    public async Task A_change_that_collides_with_another_project_is_refused_whole()
    {
        await AddAsync("orders", 51903);
        await AddAsync("billing", 51904);

        var (code, _, error) = await RunAsync(
            ["set", "billing"], new ProjectFields(null, "http://127.0.0.1:51903", null, null));

        Assert.Equal(78, code);
        Assert.Contains("unchanged", error, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:51904", Projects().Single(p => p.Name.Value == "billing").Endpoint);
    }

    [Fact]
    public async Task A_project_cannot_be_pointed_at_another_projects_store_or_at_the_manifest()
    {
        // Lens 2 probe. Two ways to make `remove --destroy` delete data that is not the
        // project's own: point its store at another project's, or at the manifest. Both must be
        // refused by `set` as they are by `add`, and the entry left exactly as it was.
        await AddAsync("orders", 51906);
        await AddAsync("billing", 51907);

        var (shared, _, sharedWhy) = await RunAsync(
            ["set", "billing"], new ProjectFields(Path.Combine(_directory, "orders.db"), null, null, null));

        Assert.Equal(78, shared);
        Assert.Contains("unchanged", sharedWhy, StringComparison.Ordinal);

        var (manifest, _, manifestWhy) = await RunAsync(
            ["set", "billing"], new ProjectFields(ManifestPath, null, null, null));

        Assert.Equal(78, manifest);
        Assert.Contains("is this manifest", manifestWhy, StringComparison.Ordinal);

        var (catalogue, _, catalogueWhy) = await RunAsync(
            ["set", "billing"], new ProjectFields(null, null, ManifestPath, null));

        Assert.Equal(78, catalogue);
        Assert.Contains("is this manifest", catalogueWhy, StringComparison.Ordinal);

        var billing = Projects().Single(p => p.Name.Value == "billing");

        Assert.Equal(Path.Combine(_directory, "billing.db"), billing.Store);
        Assert.Null(billing.NodeConfig);
    }

    [Fact]
    public async Task A_project_that_is_not_there_and_a_change_that_is_nothing_are_both_said()
    {
        await AddAsync("orders", 51905);

        var (missing, _, notThere) = await RunAsync(
            ["set", "billing"], new ProjectFields(null, null, null, "x"));

        Assert.Equal(78, missing);
        Assert.Contains("No project named 'billing'", notThere, StringComparison.Ordinal);

        var (nothing, _, empty) = await RunAsync(["set", "orders"]);

        Assert.Equal(64, nothing);
        Assert.Contains("Nothing to change", empty, StringComparison.Ordinal);
    }
}
