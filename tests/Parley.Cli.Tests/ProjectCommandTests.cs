using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Maintaining the manifest without knowing where it lives.
/// </summary>
/// <remarks>
/// <para>
/// The manifest exists to stop operators maintaining paths by hand, so a file they must
/// hand-create — in a location they have to look up — would keep most of the problem. Writing it
/// through the tool also puts every rule in one place: a duplicate endpoint is caught here, at the
/// moment somebody types it, rather than three days later as a broker that will not start with
/// nothing connecting the two events.
/// </para>
/// <para>
/// <b>Removal is where the danger is, and it is danger by omission.</b> Taking a project out of
/// the manifest forgets how to reach it and destroys nothing — the same distinction <c>retire</c>
/// and <c>purge</c> already draw. A configuration command that deleted a store as a side effect
/// would be the most destructive operation in this tool wearing the least alarming name, so what
/// is asserted below is that the files are still there afterwards and that the output says so.
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

    /// <summary>Where a manifest would live, inside a directory that does not exist yet.</summary>
    /// <remarks>
    /// Nested on purpose: <c>add</c> has to create the directory as well as the file, because the
    /// real default is under a per-user configuration directory that no Parley has ever written to.
    /// </remarks>
    private string ManifestPath => Path.Combine(_directory, "config", "Parley", "projects.json");

    private string Store(string name) => Path.Combine(_directory, name + ".db");

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

    private Task<(int Code, string Out, string Error)> AddAsync(
        string name, string? store = null, string? endpoint = null, string? node = null) =>
        RunAsync(
            ["add", name],
            new ProjectFields(store ?? Store(name), endpoint ?? $"http://127.0.0.1:{Port(name)}", node, null));

    /// <summary>A stable, distinct port per project name, so fixtures cannot collide.</summary>
    /// <param name="name">The project's name.</param>
    /// <returns>A port in the ephemeral range.</returns>
    private static int Port(string name) => 51800 + (Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 100);

    // ---- add -------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_creates_the_manifest_and_the_directory_holding_it()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(ManifestPath)));

        var (code, output, _) = await AddAsync("orders");

        Assert.Equal(0, code);
        Assert.True(File.Exists(ManifestPath));
        Assert.Contains(ManifestPath, output, StringComparison.Ordinal);

        Assert.True(ProjectManifest.TryRead(ManifestPath, out var projects, out _));
        Assert.Equal("orders", Assert.Single(projects).Name.Value);
    }

    [Fact]
    public async Task Writing_leaves_no_half_written_file_behind()
    {
        // The manifest is written beside itself and moved over, because it names every store on
        // the machine and nothing else holds a copy: writing in place truncates first, and a
        // process stopped between the truncate and the write leaves a file every reader then
        // refuses — correctly, and permanently. What is asserted here is the visible half of that,
        // the part that would rot if the temporary file were ever orphaned.
        await AddAsync("orders");
        await AddAsync("billing");

        var directory = Path.GetDirectoryName(ManifestPath)!;

        Assert.Equal(
            [Path.GetFileName(ManifestPath)],
            Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_project_needs_a_store_and_an_endpoint()
    {
        var (code, _, error) = await RunAsync(["add", "orders"]);

        Assert.Equal(64, code);
        Assert.Contains("--db", error, StringComparison.Ordinal);
        Assert.Contains("--listen", error, StringComparison.Ordinal);
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task A_name_that_is_not_one_is_refused_with_the_rule()
    {
        var (code, _, error) = await AddAsync("Orders");

        Assert.Equal(64, code);
        Assert.Contains("lowercase", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_endpoint_is_refused_and_the_manifest_is_unchanged()
    {
        await AddAsync("orders", endpoint: "http://127.0.0.1:51801");

        var before = await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken);

        var (code, _, error) = await AddAsync("billing", endpoint: "http://127.0.0.1:51801");

        Assert.Equal(78, code);
        Assert.Contains("only one broker can hold a port", error, StringComparison.Ordinal);
        Assert.Contains("unchanged", error, StringComparison.Ordinal);

        Assert.Equal(
            before,
            await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_duplicate_store_is_refused_and_the_manifest_is_unchanged()
    {
        // The one with teeth. Two brokers on one SQLite file breaks the isolation SPEC.md §4.1
        // relies on, and it does not fail loudly — WAL mode needs coherent shared memory between
        // every opener, so the damage is to the file rather than to either process.
        await AddAsync("orders", store: Store("shared"));

        var before = await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken);

        var (code, _, error) = await AddAsync("billing", store: Store("shared"));

        Assert.Equal(78, code);
        Assert.Contains("share a store", error, StringComparison.Ordinal);

        Assert.Equal(
            before,
            await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_duplicate_name_is_refused()
    {
        await AddAsync("orders");

        var (code, _, error) = await AddAsync("orders", store: Store("other"), endpoint: "http://127.0.0.1:51999");

        Assert.Equal(78, code);
        Assert.Contains("share a name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relative_path_is_refused_because_it_means_whatever_the_caller_was_standing_in()
    {
        var (code, _, error) = await AddAsync("orders", store: "orders.db");

        Assert.Equal(78, code);
        Assert.Contains("relative", error, StringComparison.Ordinal);
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task Adding_refuses_rather_than_starting_a_fresh_manifest_over_an_unreadable_one()
    {
        // The failure this repository has now met three times, arriving at the file that names
        // every store. Overwriting a configuration that could not be read destroys the only record
        // of what the machine was configured for — and the run that does it reports success.
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);

        const string damaged = "{ \"projects\": [ { \"name\": \"orders\", ";
        await File.WriteAllTextAsync(ManifestPath, damaged, TestContext.Current.CancellationToken);

        var (code, _, error) = await AddAsync("billing");

        Assert.Equal(78, code);
        Assert.Contains(ManifestPath, error, StringComparison.Ordinal);

        Assert.Equal(
            damaged,
            await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    // ---- remove ----------------------------------------------------------------------------

    [Fact]
    public async Task Removing_takes_out_the_entry_and_deletes_nothing()
    {
        await AddAsync("orders", node: Path.Combine(_directory, "node.json"));

        // Real files, so "deletes nothing" is a fact about the disk rather than about the prose.
        await File.WriteAllTextAsync(Store("orders"), "not really a database", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "node.json"), "{}", TestContext.Current.CancellationToken);

        var (code, output, _) = await RunAsync(["remove", "orders"]);

        Assert.Equal(0, code);
        Assert.True(ProjectManifest.TryRead(ManifestPath, out var projects, out _));
        Assert.Empty(projects);

        Assert.True(File.Exists(Store("orders")), "the store was deleted");
        Assert.True(File.Exists(Path.Combine(_directory, "node.json")), "the catalogue was deleted");

        // Said every time, because somebody removing a project may well have meant to delete its
        // data too — and the only safe way to offer that is to say what still exists and let them
        // do it deliberately.
        Assert.Contains("Nothing was deleted", output, StringComparison.Ordinal);
        Assert.Contains(Store("orders"), output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_directory, "node.json"), output, StringComparison.Ordinal);
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
    public async Task Listing_nothing_is_not_an_error_and_says_where_a_manifest_would_live()
    {
        // Exit zero, because a machine with no projects is an ordinary machine. Telling the
        // operator where the file would go is more use than telling them it is missing — this is
        // the one command where naming the manifest is the answer rather than noise.
        var (code, output, _) = await RunAsync(["list"]);

        Assert.Equal(0, code);
        Assert.Contains(ManifestPath, output, StringComparison.Ordinal);
        Assert.Contains("parley project add", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_manifest_is_refused_rather_than_listed_as_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        await File.WriteAllTextAsync(ManifestPath, "[]", TestContext.Current.CancellationToken);

        var (code, _, error) = await RunAsync(["list"]);

        Assert.Equal(78, code);
        Assert.Contains("not a JSON object", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_project_is_listed_as_such_rather_than_dropped()
    {
        // A project whose broker is down is still configured, and the listing is how an operator
        // finds out it is down. Omitting it would answer "what is configured here" with a subset,
        // which is the answer most likely to be believed and least likely to be true.
        await AddAsync("orders", endpoint: $"http://127.0.0.1:{Closed()}");

        var (code, output, _) = await RunAsync(["list"]);

        Assert.Equal(0, code);
        Assert.Contains("orders", output, StringComparison.Ordinal);
        Assert.Contains("not reachable", output, StringComparison.Ordinal);
        Assert.Contains(Store("orders"), output, StringComparison.Ordinal);
        Assert.Contains(ManifestPath, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_description_is_shown_because_that_is_the_only_reason_to_record_one()
    {
        await RunAsync(
            ["add", "orders"],
            new ProjectFields(Store("orders"), "http://127.0.0.1:51801", null, "the shop's own deployment"));

        var (_, output, _) = await RunAsync(["list"]);

        Assert.Contains("the shop's own deployment", output, StringComparison.Ordinal);
    }

    /// <summary>A port nothing is listening on.</summary>
    /// <returns>A port number, free at the moment of asking.</returns>
    /// <remarks>
    /// Taken from the kernel and released, rather than picked. A hardcoded "nothing runs here"
    /// port is a guess about the machine running the suite, and the failure it produces — a
    /// reachability probe that succeeds against somebody else's service — reads as this feature
    /// being broken.
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
