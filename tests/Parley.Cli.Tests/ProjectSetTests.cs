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
/// <b>019 left it one field.</b> <c>--db</c> went because a project's store is derived from its name, and
/// <c>--listen</c> went because its port is claimed when it is created — so neither has a value to set.
/// Moving a project's data is moving its folder and moving its port is a claim; both are acts rather than
/// edits. What survives is the description, which is the one thing about a project that is somebody's to
/// choose.
/// </para>
/// </remarks>
public sealed class ProjectSetTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-project-set-" + Guid.NewGuid().ToString("N"));

    public ProjectSetTests()
    {
        Directory.CreateDirectory(_directory);

        // A band of this class's own, because every root otherwise starts at the bottom of the same
        // one — see TestBand, and research.md R3, which predicted exactly this.
        TestBand.GiveItsOwn(Root);
    }

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
            fields ?? new ProjectFields(null),
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    private Task<(int Code, string Out, string Error)> AddAsync(string name) =>
        RunAsync(["add", name], new ProjectFields(null));

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
            ["set", "orders"], new ProjectFields("the one that started it all"));

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
        await RunAsync(["set", "orders"], new ProjectFields("the one that started it all"));

        Assert.Equal("the one that started it all", Assert.Single(Projects()).Description);

        var (code, _, _) = await RunAsync(["set", "orders"], new ProjectFields(string.Empty));

        Assert.Equal(0, code);
        Assert.Null(Assert.Single(Projects()).Description);
    }

    [Fact]
    public async Task A_project_cannot_be_moved_onto_another_projects_port_because_it_cannot_be_moved()
    {
        // **This asserted a refusal and now asserts an absence.** `set --listen <another project's
        // endpoint>` was refused whole, with the collision named and the project left exactly as it was.
        //
        // There is nothing to refuse: a port is claimed when a project is created and no command changes
        // it afterwards, so two projects cannot be brought onto one port by any sequence of commands.
        // Kept as a test, because a suite that simply dropped the case would leave the next reader unable
        // to tell a rule that was removed from one nobody wrote.
        await AddAsync("orders");
        await AddAsync("billing");

        var ordersOn = EndpointOf("orders");
        var billingOn = EndpointOf("billing");

        Assert.NotEqual(ordersOn, billingOn);

        // Nothing `set` takes can touch an endpoint; the one thing it does take leaves both alone.
        Assert.Equal(0, (await RunAsync(["set", "billing"], new ProjectFields("moved?"))).Code);

        Assert.Equal(ordersOn, EndpointOf("orders"));
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
        foreach (var gone in new[] { "--db", "--listen" })
        {
            var (code, _, refused) = ToolProcess.RunFully([], "project", "set", "billing", gone, "anything");

            Assert.Equal(64, code);
            Assert.Contains(gone, refused, StringComparison.Ordinal);

            // Named with what replaced it, rather than "not an option this command takes" — which would
            // be true and useless to somebody typing what every deployment was made with.
            Assert.Contains("parley project add <name>", refused, StringComparison.Ordinal);
        }

        Assert.Equal(
            ProjectFolder.StoreFor(Root, ProjectName.Create("billing")),
            Projects().Single(p => p.Name.Value == "billing").Store);
    }

    [Fact]
    public async Task A_project_that_is_not_there_and_a_change_that_is_nothing_are_both_said()
    {
        await AddAsync("orders");

        var (missing, _, notThere) = await RunAsync(["set", "billing"], new ProjectFields("x"));

        Assert.Equal(78, missing);
        Assert.Contains("No project named 'billing'", notThere, StringComparison.Ordinal);

        var (nothing, _, empty) = await RunAsync(["set", "orders"]);

        Assert.Equal(64, nothing);
        Assert.Contains("Nothing to change", empty, StringComparison.Ordinal);

        // The refusal names what it would take, and the list is one now rather than four.
        Assert.Contains("--description", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("--db", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("--listen", empty, StringComparison.Ordinal);
    }
}
