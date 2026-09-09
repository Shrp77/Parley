using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 7: the paths that promise to write nothing, and the promise made to
/// machines configured before this release.
/// </summary>
/// <remarks>
/// <para>
/// Six runs have attacked writing. This one attacks <i>not</i> writing — `enroll plan` exists to say
/// what would change without changing it, and run 6 moved the write inside the same method that
/// decides, which is exactly the edit that turns a dry run into a live one. The failure would be
/// silent and would land in somebody's repository.
/// </para>
/// <para>
/// The second half is 0.22.0's compatibility promise, which is narrower than it sounds and is the
/// half nobody would notice breaking: stop writing the old flags, stop accepting them, **keep reading
/// them**, because every machine with an autostart entry written before 0.19.0 still holds one.
/// </para>
/// </remarks>
public sealed class ProbeSevenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe7-" + Path.GetRandomFileName());

    public ProbeSevenTests() => Directory.CreateDirectory(Checkouts);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Checkouts => Path.Combine(_root, "checkouts");

    private string Catalogue => Path.Combine(_root, "node.json");

    private string Repository
    {
        get
        {
            var path = Path.Combine(Checkouts, "orders");

            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, ".git"));

            return path;
        }
    }

    private async Task<string> ManifestAsync(string name = "enrol.json")
    {
        var manifest = Path.Combine(_root, name);
        var repository = Repository.Replace(Path.DirectorySeparatorChar, '/');

        await File.WriteAllTextAsync(
            manifest,
            $$"""
              {
                "node_config": "{{Catalogue.Replace(Path.DirectorySeparatorChar, '/')}}",
                "agents": [ {
                  "workspace": "{{repository}}",
                  "agent": "orders",
                  "owns": "Carts and totals. No tax.",
                  "unattended": true,
                  "enroll": true,
                  "owns_approved": true
                } ]
              }
              """,
            TestContext.Current.CancellationToken);

        return manifest;
    }

    private async Task<int> RunAsync(string verb, string manifest) =>
        await EnrollCommand.RunAsync(
            [verb, manifest],
            selectAll: null,
            new Resolved(Catalogue, ValueSource.Argument),
            new StringWriter(),
            new StringWriter(),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Planning_creates_nothing_at_all()
    {
        // The whole contract of a dry run. Run 6 moved the write inside the method that decides,
        // which is the edit that turns this into a live run without anybody noticing — the files it
        // would create do not exist beforehand, so nothing complains and a repository quietly gains
        // a launch configuration from a command that said it was only looking.
        var manifest = await ManifestAsync();
        var repository = Repository;

        Assert.Equal(0, await RunAsync("plan", manifest));

        Assert.False(File.Exists(Path.Combine(repository, ".mcp.json")), ".mcp.json was created by a plan");
        Assert.False(File.Exists(Path.Combine(repository, "CLAUDE.md")), "CLAUDE.md was created by a plan");
        Assert.False(File.Exists(Catalogue), "the catalogue was created by a plan");
    }

    [Fact]
    public async Task Planning_over_files_that_exist_leaves_every_byte_of_them()
    {
        var manifest = await ManifestAsync();
        var repository = Repository;
        var launch = Path.Combine(repository, ".mcp.json");
        var instructions = Path.Combine(repository, "CLAUDE.md");

        await File.WriteAllTextAsync(
            launch, """{"mcpServers":{"other":{"command":"x"}}}""", TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(instructions, "# Orders\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Catalogue, "{\n  \"agents\": {}\n}\n", TestContext.Current.CancellationToken);

        var before = (
            await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(instructions, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(Catalogue, TestContext.Current.CancellationToken));

        Assert.Equal(0, await RunAsync("plan", manifest));

        Assert.Equal(before.Item1, await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken));
        Assert.Equal(before.Item2, await File.ReadAllBytesAsync(instructions, TestContext.Current.CancellationToken));
        Assert.Equal(before.Item3, await File.ReadAllBytesAsync(Catalogue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Applying_twice_writes_nothing_the_second_time()
    {
        var manifest = await ManifestAsync();
        var repository = Repository;

        Assert.Equal(0, await RunAsync("apply", manifest));

        var launch = Path.Combine(repository, ".mcp.json");
        var before = await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken);
        var catalogue = await File.ReadAllBytesAsync(Catalogue, TestContext.Current.CancellationToken);
        var written = File.GetLastWriteTimeUtc(launch);

        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.Equal(0, await RunAsync("apply", manifest));

        Assert.Equal(before, await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken));
        Assert.Equal(catalogue, await File.ReadAllBytesAsync(Catalogue, TestContext.Current.CancellationToken));

        // Not merely identical: untouched. An unchanged rewrite is still a modification to everything
        // watching a working tree, and this file is usually committed.
        Assert.Equal(written, File.GetLastWriteTimeUtc(launch));
    }

    [Fact]
    public async Task A_plan_and_an_apply_decide_the_same_thing()
    {
        // If they could differ, the dry run would be a description of something else. They share one
        // decision function now, which is what makes this checkable rather than hopeful.
        var manifest = await ManifestAsync();
        var repository = Repository;

        var planning = new StringWriter();

        await EnrollCommand.RunAsync(
            ["plan", manifest],
            selectAll: null,
            new Resolved(Catalogue, ValueSource.Argument),
            planning,
            new StringWriter(),
            TestContext.Current.CancellationToken);

        var applying = new StringWriter();

        await EnrollCommand.RunAsync(
            ["apply", manifest],
            selectAll: null,
            new Resolved(Catalogue, ValueSource.Argument),
            applying,
            new StringWriter(),
            TestContext.Current.CancellationToken);

        foreach (var note in new[] { "create", "orders" })
        {
            Assert.Contains(note, planning.ToString(), StringComparison.Ordinal);
            Assert.Contains(note, applying.ToString(), StringComparison.Ordinal);
        }

        Assert.NotNull(JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(repository, ".mcp.json"), TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("serve --db x.db --with-node --console http://127.0.0.1:5280")]
    [InlineData("serve --db x.db --console http://127.0.0.1:5280 --with-node")]
    public void An_entry_written_before_the_rename_still_reports_its_web_ui(string arguments)
    {
        // **0.22.0's compatibility promise, and the half nobody would notice breaking.** The old
        // flags are refused on the command line now; a reader that also forgot them would report
        // every entry installed before 0.19.0 as having no web UI, so `autostart status` would say
        // one thing while the scheduled task did another, and a reinstall carrying the arguments
        // forward would silently drop it.
        var entry = InstalledEntry.Parse(arguments);

        Assert.NotNull(entry);
        Assert.Equal("http://127.0.0.1:5280", entry.Console);
    }

    [Fact]
    public void An_entry_written_after_the_rename_reports_the_same_thing()
    {
        var entry = InstalledEntry.Parse("serve --db x.db --with-node --web-ui http://127.0.0.1:5280");

        Assert.NotNull(entry);
        Assert.Equal("http://127.0.0.1:5280", entry.Console);
    }
}
