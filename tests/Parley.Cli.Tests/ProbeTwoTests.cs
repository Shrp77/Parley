using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 2: the <i>other</i> file this writes, and whether writing twice is the
/// same as writing once.
/// </summary>
/// <remarks>
/// <para>
/// Run 1 attacked the launch configuration and found two defects there. Enrolling writes a second
/// file — the project's node catalogue — by exactly the same read-modify-write shape, and nothing in
/// run 1 touched it. Two agents added to <i>one</i> project is a far commoner pairing than two
/// projects over one repository: it is what setting up a deployment looks like.
/// </para>
/// <para>
/// The rest of this run asks whether the writes are stable. A tool that rewrites a file it did not
/// need to change produces a diff in somebody's repository for nothing, and a tool whose output
/// depends on how many times it has run is one nobody can check.
/// </para>
/// </remarks>
public sealed class ProbeTwoTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe2-" + Path.GetRandomFileName());

    public ProbeTwoTests() => Directory.CreateDirectory(_root);

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

    private string CatalogueFile => Path.Combine(_root, "node.json");

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name).Replace('\\', '/');

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private static EnrollmentEntry Entry(string workspace, string agent, bool unattended = true) =>
        new(workspace, agent, "What it owns. Not what it does not.", unattended)
        {
            Enroll = true,
            OwnsApproved = true,
        };

    private Task AddAsync(string workspace, string agent, string project = "acme") =>
        Enrolment.AddAsync(
            Entry(workspace, agent),
            project,
            CatalogueFile,
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Six_agents_added_to_one_project_at_once_are_all_in_its_catalogue()
    {
        // **The probe run 1 did not make.** The catalogue is the second file an enrolment writes and
        // it is written the same way: read the whole thing, add a line, write the whole thing back.
        // Six repositories joining one project is what setting a deployment up looks like, and it is
        // a far commoner pairing than two projects over one repository.
        var agents = Enumerable.Range(0, 6).Select(i => $"a{i}").ToArray();

        var adding = agents.Select(agent => Task.Run(async () =>
        {
            try
            {
                await AddAsync(Workspace(agent), agent);

                return agent;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return null;
            }
        })).ToArray();

        var accepted = (await Task.WhenAll(adding)).Where(name => name is not null).ToArray();

        var listed = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken))!["agents"]!;

        foreach (var agent in accepted)
        {
            Assert.True(
                listed.ContainsKey(agent!),
                $"'{agent}' was told its enrolment succeeded and is not in the catalogue");
        }
    }

    [Fact]
    public async Task Enrolling_the_same_repository_twice_changes_nothing_the_second_time()
    {
        var workspace = Workspace("orders");

        await AddAsync(workspace, "orders");

        var launch = Path.Combine(workspace, ".mcp.json");
        var before = await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken);
        var catalogue = await File.ReadAllBytesAsync(CatalogueFile, TestContext.Current.CancellationToken);

        await AddAsync(workspace, "orders");

        // Byte-for-byte, both files. A tool whose output depends on how many times it has run is one
        // nobody can check, and this one writes into repositories people commit.
        Assert.Equal(before, await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken));
        Assert.Equal(
            catalogue,
            await File.ReadAllBytesAsync(CatalogueFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_repository_already_correct_is_not_rewritten_at_all()
    {
        // Stronger than byte equality: the file must not be touched. An unchanged rewrite still
        // shows up as a modification to every tool watching the working tree.
        var workspace = Workspace("orders");

        await AddAsync(workspace, "orders");

        var launch = Path.Combine(workspace, ".mcp.json");
        var written = File.GetLastWriteTimeUtc(launch);

        await Task.Delay(30, TestContext.Current.CancellationToken);
        await AddAsync(workspace, "orders");

        Assert.Equal(written, File.GetLastWriteTimeUtc(launch));
    }

    [Fact]
    public async Task Other_servers_keep_their_order_and_their_content_through_both_halves()
    {
        var workspace = Workspace("orders");
        var launch = Path.Combine(workspace, ".mcp.json");

        await File.WriteAllTextAsync(
            launch,
            """
            {
              "mcpServers": {
                "zebra": { "command": "z" },
                "alpha": { "command": "a", "env": { "KEEP": "yes" } },
                "middle": { "command": "m" }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await AddAsync(workspace, "orders");

        await Enrolment.WithdrawAsync(
            workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(launch, TestContext.Current.CancellationToken))!["mcpServers"]!;

        // Order preserved, which is not decoration: a reordered file is a diff somebody has to read
        // to discover that nothing changed.
        Assert.Equal(["zebra", "alpha", "middle"], servers.Select(pair => pair.Key));
        Assert.Equal("yes", servers["alpha"]!["env"]!["KEEP"]!.GetValue<string>());
    }

    [Fact]
    public async Task Withdrawing_the_middle_of_three_projects_leaves_the_other_two_byte_for_byte()
    {
        var workspace = Workspace("inventory");
        var launch = Path.Combine(workspace, ".mcp.json");

        await AddAsync(workspace, "inventory", "alpha");
        await AddAsync(workspace, "stock", "beta");
        await AddAsync(workspace, "warehouse", "gamma");

        var before = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(launch, TestContext.Current.CancellationToken))!["mcpServers"]!;

        var alphaBefore = before["parley-alpha"]!.ToJsonString();
        var gammaBefore = before["parley-gamma"]!.ToJsonString();

        await Enrolment.WithdrawAsync(
            workspace, "stock", "beta", CatalogueFile, TestContext.Current.CancellationToken);

        var after = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(launch, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Equal(["parley-alpha", "parley-gamma"], after.Select(pair => pair.Key));
        Assert.Equal(alphaBefore, after["parley-alpha"]!.ToJsonString());
        Assert.Equal(gammaBefore, after["parley-gamma"]!.ToJsonString());
    }

    [Fact]
    public async Task A_launch_configuration_with_two_hundred_other_servers_survives_intact()
    {
        // Not a performance test. The file is read, parsed, mutated and written whole, and a probe
        // that only ever uses three servers would not notice a path that truncated or reordered at
        // scale.
        var workspace = Workspace("orders");
        var launch = Path.Combine(workspace, ".mcp.json");

        var servers = new JsonObject();

        for (var i = 0; i < 200; i++)
        {
            servers[$"server-{i:D3}"] = new JsonObject { ["command"] = $"c{i}" };
        }

        await File.WriteAllTextAsync(
            launch,
            new JsonObject { ["mcpServers"] = servers }.ToJsonString(),
            TestContext.Current.CancellationToken);

        await AddAsync(workspace, "orders");

        var after = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(launch, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Equal(201, after.Count);
        Assert.Equal("c199", after["server-199"]!["command"]!.GetValue<string>());
        Assert.NotNull(after["parley-acme"]);
    }

    [Fact]
    public async Task A_cancelled_enrolment_leaves_no_half_written_file()
    {
        var workspace = Workspace("orders");
        var launch = Path.Combine(workspace, ".mcp.json");

        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        try
        {
            await Enrolment.AddAsync(
                Entry(workspace, "orders"),
                "acme",
                CatalogueFile,
                Path.Combine(workspace, "CLAUDE.md"),
                cancelled.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // Either it never started or it finished; what must not exist is a file holding half of a
        // JSON document, which every reader afterwards reports as "not readable JSON".
        if (File.Exists(launch))
        {
            Assert.NotNull(JsonNode.Parse(
                await File.ReadAllTextAsync(launch, TestContext.Current.CancellationToken)));
        }
    }
}
