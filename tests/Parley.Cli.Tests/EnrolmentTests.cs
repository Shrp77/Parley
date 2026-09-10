using Parley.Setup;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That adding a repository to a project and withdrawing it are inverses, and that neither
/// touches anything it was not asked to.
/// </summary>
/// <remarks>
/// <para>
/// <c>Enrolment</c> exists so the terminal and <c>parley enroll apply</c> write the same two
/// files through the same code (FR-008), and the failure it prevents is not a crash: it is two
/// writers that agree today and differ in six months, leaving a repository that is an agent
/// according to one of them.
/// </para>
/// <para>
/// <b>The probes here are the point.</b> Withdrawing is the operation that runs over somebody
/// else's file with an instruction to remove something from it, and every case below is one where
/// the tempting implementation destroys what it does not understand — a launch configuration
/// holding other servers, an <c>agents</c> entry of the wrong type, a catalogue line pointing
/// somewhere else. CLAUDE.md's record is that this class of defect is found by writing the
/// malicious case, never by reading the code.
/// </para>
/// </remarks>
public sealed class EnrolmentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-enrolment-" + Path.GetRandomFileName());

    public EnrolmentTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string CatalogueFile => Path.Combine(_root, "node.json");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string InstructionsFile => Path.Combine(Workspace, "CLAUDE.md");

    private static EnrollmentEntry Entry(string agent, string workspace) =>
        new(workspace, agent, null, Unattended: true) { Enroll = true };

    [Fact]
    public async Task Adding_writes_the_launch_configuration_and_the_catalogue_entry()
    {
        var outcome = await Enrolment.AddAsync(
            Entry("orders", Workspace),
            "acme",
            CatalogueFile,
            InstructionsFile,
            TestContext.Current.CancellationToken);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;
        var env = launch["mcpServers"]!["parley-acme"]!["env"]!;

        Assert.Equal("orders", env["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("acme", env["PARLEY_PROJECT"]!.GetValue<string>());

        var catalogue = JsonNode.Parse(await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken))!;

        Assert.Equal(Workspace, catalogue["agents"]!["orders"]!["workspace"]!.GetValue<string>());
        Assert.Contains(LaunchFile, outcome.Written);
        Assert.Contains(CatalogueFile, outcome.Written);
    }

    [Fact]
    public async Task Withdrawing_undoes_exactly_what_adding_did()
    {
        // The inverse property, asserted on the content rather than on a report of it: what the
        // file holds after adding and withdrawing is what it held before, other server and all.
        // Compared as parsed JSON rather than as bytes, because the round trip reformats and this
        // is a claim about what the file says, not about how it is laid out.
        await File.WriteAllTextAsync(
            LaunchFile,
            "{\n  \"mcpServers\": {\n    \"other\": {\n      \"command\": \"something\"\n    }\n  }\n}\n",
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);

        await Enrolment.AddAsync(
            Entry("orders", Workspace), "acme", CatalogueFile, InstructionsFile,
            TestContext.Current.CancellationToken);

        await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        var after = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);
        var catalogue = JsonNode.Parse(await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken))!;

        Assert.Equal(
            JsonNode.Parse(before)!.ToJsonString(),
            JsonNode.Parse(after)!.ToJsonString());

        Assert.Empty((JsonObject)catalogue["agents"]!);
    }

    [Fact]
    public async Task Withdrawing_leaves_every_other_server_and_the_file_itself()
    {
        await File.WriteAllTextAsync(
            LaunchFile,
            """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp" },
                "other": { "command": "something" }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;
        var servers = (JsonObject)launch["mcpServers"]!;

        Assert.True(File.Exists(LaunchFile), "the launch configuration was deleted rather than edited");
        Assert.False(servers.ContainsKey("parley"));
        Assert.True(servers.ContainsKey("other"), "another repository's MCP server was removed with ours");
    }

    [Fact]
    public async Task Withdrawing_never_rewrites_the_standing_instructions()
    {
        // The peers frame belongs to the project and the roster block inside it is written by
        // somebody else. Nothing here can tell a line the project added from a line a tool did,
        // and the thing that cannot tell is the thing that would be deleting it.
        const string Instructions = "# Orders\n\n## Peers\n\nSomething the project wrote itself.\n";

        await File.WriteAllTextAsync(InstructionsFile, Instructions, TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Equal(
            Instructions,
            await File.ReadAllTextAsync(InstructionsFile, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(InstructionsFile, outcome.Written);
        Assert.Contains(outcome.Notes, note => note.Contains("left untouched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Withdrawing_what_was_never_there_says_so_and_writes_nothing()
    {
        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Empty(outcome.Written);
        Assert.Contains(outcome.Notes, note => note.Contains("already absent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_launch_configuration_whose_servers_are_not_an_object_is_left_alone()
    {
        // The probe: `mcpServers` is well-formed JSON of the wrong type. Casting it away and
        // writing an object over it destroys what it held exactly as an unparseable file would,
        // and the run would report a successful withdrawal. Three instances of that one class of
        // bug were found in this feature's first form.
        const string Hostile = "{\n  \"mcpServers\": \"not an object\"\n}\n";

        await File.WriteAllTextAsync(LaunchFile, Hostile, TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Equal(Hostile, await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(LaunchFile, outcome.Written);
        Assert.Contains(outcome.Notes, note => note.Contains("left alone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_launch_configuration_that_is_not_readable_json_is_left_alone()
    {
        const string Broken = "{ this is not json";

        await File.WriteAllTextAsync(LaunchFile, Broken, TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Equal(Broken, await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(LaunchFile, outcome.Written);
    }

    [Fact]
    public async Task A_catalogue_whose_agents_are_not_an_object_is_refused_rather_than_replaced()
    {
        await File.WriteAllTextAsync(
            CatalogueFile,
            "{\n  \"agents\": [\"orders\"]\n}\n",
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => Enrolment.AddAsync(
            Entry("orders", Workspace), "acme", CatalogueFile, InstructionsFile,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_catalogue_entry_for_somebody_else_is_removed_only_by_its_own_name()
    {
        // The probe: withdrawing 'orders' must not take 'billing' with it, even though both are
        // in the same file and only one of them is being withdrawn.
        await Enrolment.AddAsync(
            Entry("orders", Workspace), "acme", CatalogueFile, InstructionsFile,
            TestContext.Current.CancellationToken);

        var billing = Path.Combine(_root, "billing");

        Directory.CreateDirectory(Path.Combine(billing, ".git"));

        await Enrolment.AddAsync(
            Entry("billing", billing), "acme", CatalogueFile, InstructionsFile,
            TestContext.Current.CancellationToken);

        await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        var agents = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken))!["agents"]!;

        Assert.False(agents.ContainsKey("orders"));
        Assert.True(agents.ContainsKey("billing"), "another agent's catalogue entry went with this one");
    }

    [Fact]
    public async Task Adding_a_repository_that_belongs_to_another_project_leaves_that_entry_alone()
    {
        // **A refusal until 009, changed deliberately.** A repository several product lines depend
        // on had to pick one set of peers, because brokers do not federate and the project it
        // joined was the only roster it could answer in. What the old refusal was really about --
        // one identity moved out of a deployment silently -- is still refused; a second entry
        // beside the first moves nothing, and this asserts exactly that.
        await Enrolment.AddAsync(
            Entry("orders", Workspace), "acme", CatalogueFile, InstructionsFile,
            TestContext.Current.CancellationToken);

        var before = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

        await Enrolment.AddAsync(
            Entry("orders", Workspace), "other", Path.Combine(_root, "other-node.json"), InstructionsFile,
            TestContext.Current.CancellationToken);

        var after = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Equal(
            ["parley-acme", "parley-other"],
            after.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));

        Assert.True(
            JsonNode.DeepEquals(before["parley-acme"], after["parley-acme"]),
            "the first project's entry changed when the second was added");
    }

    [Fact]
    public async Task A_byte_order_mark_the_instructions_had_is_kept()
    {
        // Not cosmetic. ReadAllText strips one and WriteAllText does not put it back, so a
        // twelve-line addition rewrites every byte of the file a project reads on every turn.
        await File.WriteAllTextAsync(
            InstructionsFile,
            "# Orders\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            TestContext.Current.CancellationToken);

        await Enrolment.AddAsync(
            Entry("orders", Workspace), "acme", CatalogueFile, InstructionsFile,
            TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(InstructionsFile, TestContext.Current.CancellationToken);

        Assert.True(
            bytes is [0xEF, 0xBB, 0xBF, ..],
            "the byte-order mark the file had was dropped");
    }
}
