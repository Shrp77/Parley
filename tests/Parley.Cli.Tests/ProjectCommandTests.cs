using Parley.Core;
using Parley.Setup;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Parley.Broker;
using Parley.Configuration;
using Parley.Server;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Naming a project, and never having to say where anything goes.
/// </summary>
/// <remarks>
/// <para>
/// <b>019 turned this command into one argument.</b> It required <c>--db &lt;path&gt;</c> and
/// <c>--listen &lt;url&gt;</c>, neither defaulted, so a person naming a project had to invent a
/// filesystem path and pick a free port and then remember both — and the form could only tell them
/// afterwards that one of them clashed. A project's store is derived from its name now and its port is
/// claimed, so those two questions have gone rather than acquired better defaults.
/// </para>
/// <para>
/// So the tests that asserted the refusals are converted by the rule the rest of this conversion uses:
/// where the rule outlived its subject it is repointed, and where the behaviour genuinely ended the test
/// asserts the <b>absence</b> — that two projects cannot be given one store because there is no store to
/// give, rather than that the attempt is refused.
/// </para>
/// <para>
/// <b>Removal is where the danger is, and it is danger by omission.</b> Taking a project out forgets how
/// to reach it and destroys nothing — the same distinction <c>retire</c> and <c>purge</c> already draw. A
/// configuration command that deleted a store as a side effect would be the most destructive operation in
/// this tool wearing the least alarming name, so what is asserted below is that the files are still there
/// afterwards and that the output says so.
/// </para>
/// </remarks>
public sealed class ProjectCommandTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-project-" + Guid.NewGuid().ToString("N"));

    public ProjectCommandTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>The root, inside a directory that does not exist yet.</summary>
    /// <remarks>
    /// Nested on purpose: <c>add</c> has to create the whole layout, because the real default is under a
    /// per-user configuration directory that no Parley has ever written to.
    /// </remarks>
    private string Root => Path.Combine(_directory, "config", "Parley");

    private string Store(string name) =>
        ProjectFolder.StoreFor(Root, ProjectName.Create(name));

    private string ConfigurationOf(string name) =>
        ProjectFolder.ConfigurationFor(Root, ProjectName.Create(name));

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

    // **One argument.** The store and the endpoint used to be required here and are not supplied
    // anywhere in this file any more, which is the shape of the change more plainly than any assertion.
    private Task<(int Code, string Out, string Error)> AddAsync(
        string name, string? endpoint = null, string? description = null) =>
        RunAsync(["add", name], new ProjectFields(description));

    // ---- add -------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_creates_the_layout_and_says_where_everything_went()
    {
        Assert.False(Directory.Exists(Root));

        var (code, output, error) = await AddAsync("orders");

        Assert.Equal(0, code);
        Assert.True(File.Exists(ConfigurationOf("orders")), error);

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out _));

        var project = Assert.Single(projects);

        Assert.Equal("orders", project.Name.Value);
        Assert.Equal(Store("orders"), project.Store);

        // **Both facts, because both were things a person used to have to supply.** The endpoint is what
        // every other command needs to reach it, and the folder is what a backup has to include —
        // neither is anywhere they can look up without being told once.
        Assert.Contains(project.Endpoint, output, StringComparison.Ordinal);
        Assert.Contains(ProjectFolder.For(Root, ProjectName.Create("orders")), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_is_the_whole_of_it()
    {
        // **SC-001 at this level**: the claim that an operator types one project name and has a working
        // deployment. Nothing is passed but the name, and what comes back is a port in the band and a
        // store inside the project's own folder.
        var (code, _, error) = await AddAsync("orders");

        Assert.Equal(0, code);

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out _), error);

        var project = Assert.Single(projects);
        var port = new Uri(project.Endpoint).Port;

        Assert.InRange(port, PortLedger.BandFirst, PortLedger.BandLast);
        Assert.Equal(Store("orders"), project.Store);
        Assert.StartsWith(ProjectFolder.For(Root, ProjectName.Create("orders")), project.Store, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_leaves_no_half_written_file_behind()
    {
        // Each project's configuration is written beside itself and moved over, because nothing else
        // holds a copy of where its broker listens: writing in place truncates first, and a process
        // stopped between the truncate and the write leaves a file every reader then refuses —
        // correctly, and permanently. What is asserted here is the visible half of that, the part that
        // would rot if a temporary file were ever orphaned.
        await AddAsync("orders");
        await AddAsync("billing");

        foreach (var name in new[] { "orders", "billing" })
        {
            Assert.Equal(
                [ProjectFolder.ConfigurationFileName],
                Directory.GetFiles(ProjectFolder.For(Root, ProjectName.Create(name)))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
        }

        // The lock is named rather than the assertion loosened to ignore extra files. It is a real
        // sibling and stays behind on purpose, because a lock somebody has to delete is a lock that
        // eventually is not taken. Listing it keeps this strict: a stray `.writing`, or a backup some
        // future rewrite leaves, still fails here rather than passing through a wildcard.
        Assert.Equal(
            [".lock"],
            Directory.GetFiles(ProjectFolder.ProjectsIn(Root))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_name_that_is_not_one_is_refused_with_the_rule()
    {
        var (code, _, error) = await AddAsync("Orders");

        Assert.Equal(64, code);
        Assert.Contains("lowercase", error, StringComparison.Ordinal);

        // And nothing was created on the way to refusing it — the name is the folder, so a refusal that
        // had already made one would leave a directory nothing can read back as a project.
        Assert.False(Directory.Exists(ProjectFolder.ProjectsIn(Root)));
    }

    [Fact]
    public async Task Two_projects_are_never_handed_one_port()
    {
        // **What `A_duplicate_endpoint_is_refused` used to assert, from the other side.** Nobody types an
        // endpoint now, so the way to collide is to be handed the same one — which is what the claim
        // exists to prevent, and what `PortLedgerTests` probes concurrently. Here it is the ordinary
        // sequential case, through the command a person actually runs.
        await AddAsync("orders");
        await AddAsync("billing");
        await AddAsync("inventory");

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out var problem), problem);

        var ports = projects.Select(p => new Uri(p.Endpoint).Port).ToList();

        Assert.Equal(3, ports.Count);
        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    [Fact]
    public async Task A_stated_endpoint_is_taken_and_a_clash_with_it_is_refused()
    {
        // `--listen` survives, for the case the band cannot serve: SPEC.md §16 permits a broker on a
        // private network somebody controls, and nothing here knows which interface they mean.
        var stated = $"http://127.0.0.1:{Closed().ToString(CultureInfo.InvariantCulture)}";

        Assert.Equal(0, (await AddAsync("orders", endpoint: stated)).Code);

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out _));
        Assert.Equal(stated, Assert.Single(projects).Endpoint);

        // Recorded as spoken for even though it did not come from the band, so a second project asking
        // for it is refused rather than quietly given a port somebody is about to serve on.
        var (code, _, error) = await AddAsync("billing", endpoint: stated);

        Assert.Equal(78, code);
        Assert.Contains("orders", error, StringComparison.Ordinal);
        Assert.Contains("only one broker can hold a port", error, StringComparison.Ordinal);

        Assert.True(ProjectManifest.TryRead(Root, out var after, out _));
        Assert.Single(after);
    }

    [Fact]
    public async Task A_stated_endpoint_that_is_not_an_address_is_refused_and_creates_nothing()
    {
        var (code, _, error) = await AddAsync("orders", endpoint: "nonsense");

        Assert.Equal(78, code);
        Assert.Contains("nonsense", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ProjectFolder.For(Root, ProjectName.Create("orders"))));
    }

    [Fact]
    public async Task Two_projects_cannot_be_given_one_store_because_neither_is_given_one()
    {
        // **This asserted a refusal and now asserts an absence.** The refusal had teeth: two brokers on
        // one SQLite file breaks the isolation SPEC.md §4.1 relies on, and it does not fail loudly — WAL
        // mode needs coherent shared memory between every opener, so the damage is to the file rather
        // than to either process.
        //
        // That hazard is gone rather than guarded. A store is `parley.db` inside a folder named by the
        // project, so distinct names give distinct stores by construction — there is no flag that could
        // point two projects at one file, which is stronger than refusing it. Kept as a test, because a
        // suite that simply dropped the case would leave the next reader unable to tell a rule that was
        // removed from one nobody wrote.
        await AddAsync("orders");
        await AddAsync("billing");

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out _));

        var stores = projects.Select(p => p.Store).ToList();

        Assert.Equal(2, stores.Count);
        Assert.Equal(stores.Count, stores.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task A_duplicate_name_is_refused_and_the_project_that_is_there_is_untouched()
    {
        await AddAsync("orders");

        var before = await File.ReadAllTextAsync(ConfigurationOf("orders"), TestContext.Current.CancellationToken);

        var (code, _, error) = await AddAsync("orders");

        Assert.Equal(78, code);
        Assert.Contains("already a project", error, StringComparison.Ordinal);

        // **The refusal is what makes the folder-is-the-name model safe.** Without it a second add would
        // write over the first project's configuration and hand it a different port, and the broker
        // serving the old one would go on holding the store under a name that now means something else.
        Assert.Equal(
            before,
            await File.ReadAllTextAsync(ConfigurationOf("orders"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_store_cannot_be_named_at_all_and_saying_so_is_the_point()
    {
        // **Said rather than ignored.** Every deployment on the owner's machine was made with
        // `project add --db <path>`, so somebody will type it again from memory or from a script — and a
        // flag silently passed over is a person believing their store went where they said it would.
        //
        // Driven through the real binary, because the refusal lives in the entry point: an in-process
        // call never reaches it, which is the reason `FilesCommand.OwnOptions` is tested this way too.
        var (exitCode, _, error) = ToolProcess.RunFully(
            [], "project", "add", "orders", "--db", Path.Combine(_directory, "somewhere.db"));

        Assert.Equal(64, exitCode);
        Assert.Contains("--db", error, StringComparison.Ordinal);
        Assert.Contains("derived from its name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_refuses_rather_than_starting_fresh_over_a_configuration_it_cannot_read()
    {
        // The failure this repository has now met three times, arriving at the file that says where a
        // broker listens. Overwriting a configuration that could not be read destroys the only record of
        // what the machine was configured for — and the run that does it reports success.
        Directory.CreateDirectory(ProjectFolder.For(Root, ProjectName.Create("orders")));

        const string damaged = "{ \"endpoint\": ";
        await File.WriteAllTextAsync(ConfigurationOf("orders"), damaged, TestContext.Current.CancellationToken);

        var (code, _, error) = await AddAsync("billing");

        Assert.Equal(78, code);
        Assert.Contains(ConfigurationOf("orders"), error, StringComparison.Ordinal);

        Assert.Equal(
            damaged,
            await File.ReadAllTextAsync(ConfigurationOf("orders"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_add_leaves_no_port_stranded()
    {
        // The claim happens before the folder is written, so a write that then fails would leave a port
        // recorded against a project that does not exist — unavailable for ever, and invisible, because
        // the record is exactly what the next claim steps over.
        Directory.CreateDirectory(ProjectFolder.For(Root, ProjectName.Create("orders")));

        await File.WriteAllTextAsync(
            ConfigurationOf("orders"), "{ \"endpoint\": ", TestContext.Current.CancellationToken);

        Assert.Equal(78, (await AddAsync("billing")).Code);

        Assert.DoesNotContain("billing", PortLedger.Read(Root).Keys, StringComparer.Ordinal);
    }

    // ---- remove ----------------------------------------------------------------------------

    [Fact]
    public async Task Removing_takes_out_the_configuration_and_deletes_nothing()
    {
        await AddAsync("orders");

        // A real file, so "deletes nothing" is a fact about the disk rather than about the prose.
        await File.WriteAllTextAsync(
            Store("orders"), "not really a database", TestContext.Current.CancellationToken);

        var (code, output, _) = await RunAsync(["remove", "orders"]);

        Assert.Equal(0, code);
        Assert.True(ProjectManifest.TryRead(Root, out var projects, out _));
        Assert.Empty(projects);

        Assert.True(File.Exists(Store("orders")), "the store was deleted");

        // Said every time, because somebody removing a project may well have meant to delete its data
        // too — and the only safe way to offer that is to say what still exists and let them do it
        // deliberately.
        Assert.Contains("Nothing was deleted", output, StringComparison.Ordinal);
        Assert.Contains(Store("orders"), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_gives_the_port_back()
    {
        // FR-014. A port kept against a project that has gone is a port nothing can ever use again, and
        // nothing would say so — the band would simply be one narrower every time somebody changed their
        // mind about a name.
        await AddAsync("orders");

        Assert.True(ProjectManifest.TryRead(Root, out var before, out _));

        var was = Assert.Single(before).Endpoint;

        Assert.Equal(0, (await RunAsync(["remove", "orders"])).Code);
        Assert.DoesNotContain("orders", PortLedger.Read(Root).Keys, StringComparer.Ordinal);

        Assert.Equal(0, (await AddAsync("billing")).Code);

        Assert.True(ProjectManifest.TryRead(Root, out var after, out _));
        Assert.Equal(was, Assert.Single(after).Endpoint);
    }

    [Fact]
    public async Task Removing_a_project_whose_broker_is_still_running_says_so()
    {
        // FR-029, which was implemented and never exercised — the branch existed, and nothing had ever
        // taken it. Forgetting how to reach a broker does not stop the broker: the process keeps its
        // port and its store, and the configuration just deleted was the only record of where it was.
        // Somebody who meant to shut a project down and ran this instead needs telling, and they need
        // telling at the moment they do it.
        //
        // A real broker on a real port, because the check is a connection attempt and a listener that
        // does not speak HTTP/2 would be reported unreachable — which is the answer that makes this test
        // pass while proving nothing.
        var endpoint = $"http://127.0.0.1:{Closed().ToString(CultureInfo.InvariantCulture)}";

        Assert.Equal(0, (await AddAsync("orders", endpoint: endpoint)).Code);

        var runtime = BrokerRuntime.Open(
            Store("orders"), BrokerOptions.Default, TimeProvider.System);

        await using var server = await ParleyServer.StartAsync(
            runtime, endpoint, TestContext.Current.CancellationToken);

        var (code, output, _) = await RunAsync(["remove", "orders"]);

        Assert.Equal(0, code);
        Assert.Contains("still appears to be running", output, StringComparison.Ordinal);
        Assert.Contains(endpoint, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_project_that_is_not_there_lists_what_is()
    {
        await AddAsync("orders");

        var (code, _, error) = await RunAsync(["remove", "billing"]);

        Assert.Equal(78, code);
        Assert.Contains("billing", error, StringComparison.Ordinal);
        Assert.Contains("orders", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_from_a_machine_with_nothing_configured_says_so()
    {
        var (code, _, error) = await RunAsync(["remove", "orders"]);

        Assert.Equal(78, code);
        Assert.Contains("none configured", error, StringComparison.Ordinal);
    }

    // ---- list ------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_nothing_is_not_an_error_and_says_where_a_project_would_live()
    {
        // Exit zero, because a machine with no projects is an ordinary machine. Telling the operator
        // where they would go is more use than telling them there are none.
        var (code, output, _) = await RunAsync(["list"]);

        Assert.Equal(0, code);
        Assert.Contains(ProjectFolder.ProjectsIn(Root), output, StringComparison.Ordinal);

        // And it offers the whole command, which is now short enough to type from reading it.
        Assert.Contains("parley project add <name>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_configuration_is_refused_rather_than_listed_as_empty()
    {
        Directory.CreateDirectory(ProjectFolder.For(Root, ProjectName.Create("orders")));
        await File.WriteAllTextAsync(ConfigurationOf("orders"), "[]", TestContext.Current.CancellationToken);

        var (code, _, error) = await RunAsync(["list"]);

        Assert.Equal(78, code);
        Assert.Contains("does not hold an object", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_project_is_listed_as_such_rather_than_dropped()
    {
        // A project whose broker is down is still configured, and the listing is how an operator finds
        // out it is down. Omitting it would answer "what is configured here" with a subset, which is the
        // answer most likely to be believed and least likely to be true.
        await AddAsync("orders");

        var (code, output, _) = await RunAsync(["list"]);

        Assert.Equal(0, code);
        Assert.Contains("orders", output, StringComparison.Ordinal);
        Assert.Contains("not reachable", output, StringComparison.Ordinal);
        Assert.Contains(Store("orders"), output, StringComparison.Ordinal);
        Assert.Contains(ProjectFolder.ProjectsIn(Root), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_description_is_shown_because_that_is_the_only_reason_to_record_one()
    {
        await AddAsync("orders", description: "the shop's own deployment");

        var (_, output, _) = await RunAsync(["list"]);

        Assert.Contains("the shop's own deployment", output, StringComparison.Ordinal);
    }

    /// <summary>A port nothing is listening on.</summary>
    /// <returns>A port number, free at the moment of asking.</returns>
    /// <remarks>
    /// Taken from the kernel and released, rather than picked. A hardcoded "nothing runs here" port is a
    /// guess about the machine running the suite, and the failure it produces — a reachability probe that
    /// succeeds against somebody else's service — reads as this feature being broken.
    /// </remarks>
    private static int Closed()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}
