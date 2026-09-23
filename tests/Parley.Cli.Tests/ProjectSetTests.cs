using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Changing what a named project says, without removing and re-adding it.
/// </summary>
/// <remarks>
/// <para>
/// The case this exists for is a change made after the fact: the only way to alter a project was to
/// remove it — which prints a paragraph about what was not deleted, for a change that never meant to
/// delete anything.
/// </para>
/// <para>
/// <b>019 left it two fields.</b> <c>--db</c> is gone, because a project's store is derived from its name
/// and there is no value to set: moving a project's data is moving its folder, which is a different act
/// than editing a field. What survives is the endpoint and the description.
/// </para>
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

    private string Root => _directory;

    private async Task<(int Code, string Out, string Error)> RunAsync(
        string[] arguments, ProjectFields? fields = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            arguments,
            Root,
            fields ?? new ProjectFields(null, null),
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    private Task<(int Code, string Out, string Error)> AddAsync(string name) =>
        RunAsync(["add", name], new ProjectFields(null, null));

    private IReadOnlyList<ProjectSettings> Projects()
    {
        Assert.True(ProjectManifest.TryRead(Root, out var projects, out var problem), problem);

        return projects;
    }

    private string EndpointOf(string name) =>
        Projects().Single(p => p.Name.Value == name).Endpoint;

    [Fact]
    public async Task Only_the_fields_given_change()
    {
        await AddAsync("orders");

        var was = EndpointOf("orders");

        var (code, output, error) = await RunAsync(
            ["set", "orders"], new ProjectFields(null, "the one that started it all"));

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("Changed 'orders'", output, StringComparison.Ordinal);
        Assert.Contains("next start", output, StringComparison.Ordinal);

        var orders = Assert.Single(Projects());

        Assert.Equal(ProjectFolder.StoreFor(Root, ProjectName.Create("orders")), orders.Store);
        Assert.Equal(was, orders.Endpoint);
        Assert.Equal("the one that started it all", orders.Description);
    }

    [Fact]
    public async Task An_empty_value_clears_an_optional_field()
    {
        // **Written about `node_config`, which 018 removed the meaning of and 019 removed the field.**
        // The rule survived both: an empty value clears an optional one rather than being read as "leave
        // it alone", and `description` is what now has to hold it.
        await AddAsync("orders");
        await RunAsync(["set", "orders"], new ProjectFields(null, "the one that started it all"));

        Assert.Equal("the one that started it all", Assert.Single(Projects()).Description);

        var (code, _, _) = await RunAsync(["set", "orders"], new ProjectFields(null, string.Empty));

        Assert.Equal(0, code);
        Assert.Null(Assert.Single(Projects()).Description);
    }

    [Fact]
    public async Task A_change_that_collides_with_another_project_is_refused_whole()
    {
        await AddAsync("orders");
        await AddAsync("billing");

        var ordersOn = EndpointOf("orders");
        var billingOn = EndpointOf("billing");

        var (code, _, error) = await RunAsync(["set", "billing"], new ProjectFields(ordersOn, null));

        Assert.Equal(78, code);
        Assert.Contains("unchanged", error, StringComparison.Ordinal);
        Assert.Equal(billingOn, EndpointOf("billing"));
    }

    [Fact]
    public async Task A_project_cannot_be_pointed_at_another_projects_store_because_it_cannot_be_pointed_anywhere()
    {
        // **A lens 2 probe, converted to an absence.** It found two ways to make `remove --destroy`
        // delete data that is not the project's own: point its store at another project's, or at the
        // manifest. Both were refused by `set` as they were by `add`, and a third way in — pointing a
        // project at the manifest as its catalogue — went with `--node-config` in 018.
        //
        // **All three are now unreachable rather than refused.** A store is derived from the name, so
        // there is no field to set, no flag to carry a value, and nothing `set` could be asked to do.
        // That is what closes the hazard: `remove --destroy` deletes what is inside the project's own
        // folder, and nothing can make that folder somebody else's.
        await AddAsync("orders");
        await AddAsync("billing");

        // The command declines outright, rather than accepting a change it cannot make.
        var (nothing, _, empty) = await RunAsync(["set", "billing"]);

        Assert.Equal(64, nothing);
        Assert.Contains("Nothing to change", empty, StringComparison.Ordinal);

        // And the option it would have taken is not one the tool has. Driven through the real binary,
        // because the refusal lives in the entry point.
        var (code, _, refused) = ToolProcess.RunFully(
            [], "project", "set", "billing", "--db", ProjectFolder.StoreFor(Root, ProjectName.Create("orders")));

        Assert.Equal(64, code);
        Assert.Contains("--db", refused, StringComparison.Ordinal);

        Assert.Equal(
            ProjectFolder.StoreFor(Root, ProjectName.Create("billing")),
            Projects().Single(p => p.Name.Value == "billing").Store);
    }

    [Fact]
    public async Task A_project_that_is_not_there_and_a_change_that_is_nothing_are_both_said()
    {
        await AddAsync("orders");

        var (missing, _, notThere) = await RunAsync(["set", "billing"], new ProjectFields(null, "x"));

        Assert.Equal(78, missing);
        Assert.Contains("No project named 'billing'", notThere, StringComparison.Ordinal);

        var (nothing, _, empty) = await RunAsync(["set", "orders"]);

        Assert.Equal(64, nothing);
        Assert.Contains("Nothing to change", empty, StringComparison.Ordinal);

        // The refusal names what it would take, and the list is two now rather than four.
        Assert.Contains("--listen", empty, StringComparison.Ordinal);
        Assert.Contains("--description", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("--db", empty, StringComparison.Ordinal);
    }
}
