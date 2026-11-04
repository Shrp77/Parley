using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>Enrolling, accepting a line and withdrawing change only this machine's block (SPEC.md §4.1.2).</summary>
[Integration]
public sealed class OneMachineOneBlockTests : IDisposable
{
    private static readonly string Here = MachineName.Current;

    private readonly string _root = Directory.CreateTempSubdirectory("parley-blocks-").FullName;

    public OneMachineOneBlockTests() => Directory.CreateDirectory(Workspace);

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

    private string Workspace => Path.Combine(_root, "infra");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private static AgentName Agent => AgentName.Create("infra@" + Here);

    private string Written(string machines, string env = "\"PARLEY_AGENT\": \"infra\", \"PARLEY_PROJECT\": \"acme\"")
    {
        var text = new JsonObject
        {
            ["parleyAgentId"] = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011",
            ["mcpServers"] = JsonNode.Parse(
                $$"""{ "parley-acme": { "command": "parley-mcp", "env": { {{env}} } }, "other": { "command": "x" } }"""),
            ["parleyMachines"] = JsonNode.Parse(machines),
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

        File.WriteAllText(LaunchFile, text);

        return text;
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(LaunchFile))!;

    private static string Block(string owns) => $$"""{ "parley-acme": { "PARLEY_OWNS": "{{owns}}" } }""";

    [Fact]
    public async Task Accepting_a_line_writes_only_this_machines_block()
    {
        Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": {{Block("Old.")}} }""");

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, "Answers questions.", TestContext.Current.CancellationToken));

        var root = Read();

        Assert.Equal("Answers questions.", (string?)root["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_OWNS"]);
        Assert.Equal("Makes changes.", (string?)root["parleyMachines"]!["server"]!["parley-acme"]!["PARLEY_OWNS"]);
        Assert.Null(root["mcpServers"]!["parley-acme"]!["env"]!["PARLEY_OWNS"]);
    }

    [Fact]
    public async Task A_file_in_the_writers_format_changes_only_on_this_machines_lines()
    {
        var before = Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": {{Block("Old.")}} }""");

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, "New.", TestContext.Current.CancellationToken));

        var after = File.ReadAllText(LaunchFile);

        Assert.Equal(before.Replace("\"Old.\"", "\"New.\"", StringComparison.Ordinal), after);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task A_write_keeps_the_files_own_line_endings(string newline)
    {
        // A laptop and a Linux server share this file, and a write in the platform's own endings changes every line of it.
        var before = Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": {{Block("Old.")}} }""").ReplaceLineEndings(newline);

        File.WriteAllText(LaunchFile, before);

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, "New.", TestContext.Current.CancellationToken));

        Assert.Equal(before.Replace("\"Old.\"", "\"New.\"", StringComparison.Ordinal), File.ReadAllText(LaunchFile));
    }

    [Fact]
    public async Task Withdrawing_keeps_a_byte_order_mark()
    {
        var text = Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": {{Block("Answers.")}} }""");

        File.WriteAllText(LaunchFile, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        _ = await Enrolment.WithdrawAsync(Workspace, "infra", "acme", null, TestContext.Current.CancellationToken);

        var written = File.ReadAllBytes(LaunchFile);

        Assert.True(written is [0xEF, 0xBB, 0xBF, ..]);
        Assert.Null(Read()["parleyMachines"]![Here]);
    }

    [Fact]
    public async Task Withdrawing_from_a_window_takes_the_block_that_reaches_a_broker()
    {
        Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19100" } } }""");

        _ = await Enrolment.WithdrawAsync(
            Workspace, "infra", ProjectManifest.WindowName, null, TestContext.Current.CancellationToken);

        var root = Read();

        Assert.Null(root["parleyMachines"]![Here]);
        Assert.NotNull(root["mcpServers"]!["parley-acme"]);
    }

    [Fact]
    public void Nothing_is_enrolled_into_a_window()
    {
        var refused = Assert.Throws<InvalidDataException>(() => EnrollmentPlan.Describe(
            new EnrollmentEntry(Workspace, "infra", null, Unattended: true), null, null, false, ProjectManifest.WindowName));

        Assert.Contains("parley enroll", refused.Message, StringComparison.Ordinal);
        Assert.Contains("--broker", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolling_under_a_new_role_drops_the_line_the_old_one_had()
    {
        Written($$"""{ "{{Here}}": {{Block("The old role's line.")}} }""");

        var root = Planned("renamed", null);

        Assert.Null(root["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_OWNS"]);
    }

    [Fact]
    public void Enrolling_into_a_project_here_takes_a_blocks_address_out()
    {
        // Stale or planted, it would quietly beat the project this machine describes.
        Written($$"""{ "{{Here}}": { "parley-acme": { "PARLEY_BROKER": "http://192.0.2.71:19100" } } }""");

        Assert.Null(Planned("infra", null)["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_BROKER"]);
    }

    [Fact]
    public void A_shared_value_a_client_would_expand_is_not_carried_across()
    {
        File.WriteAllText(
            LaunchFile,
            """{ "mcpServers": { "parley-acme": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme", "PARLEY_OWNS": "Stock ${GITHUB_TOKEN}", "PARLEY_RESPONDER": "${SECRET}" } } } }""");

        var env = Planned("infra", null)["mcpServers"]!["parley-acme"]!["env"]!.AsObject();

        Assert.False(env.ContainsKey("PARLEY_OWNS"));
        Assert.False(env.ContainsKey("PARLEY_RESPONDER"));
    }

    [Fact]
    public async Task Accepting_in_a_file_from_before_blocks_leaves_the_shared_line()
    {
        File.WriteAllText(
            LaunchFile,
            """{ "mcpServers": { "parley-acme": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme", "PARLEY_OWNS": "Shared." } } } }""");

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, "Mine.", TestContext.Current.CancellationToken));

        var root = Read();

        Assert.Equal("Shared.", (string?)root["mcpServers"]!["parley-acme"]!["env"]!["PARLEY_OWNS"]);
        Assert.Equal("Mine.", (string?)root["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_OWNS"]);
    }

    [Fact]
    public async Task Taking_a_line_out_keeps_the_block()
    {
        Written($$"""{ "{{Here}}": {{Block("Old.")}} }""");

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, null, TestContext.Current.CancellationToken));

        var block = Read()["parleyMachines"]![Here]!["parley-acme"]!.AsObject();

        Assert.Empty(block);
    }

    [Fact]
    public async Task Taking_a_line_out_leaves_no_shared_one_to_come_back()
    {
        Written($$"""{ "{{Here}}": {{Block("Old.")}} }""", "\"PARLEY_AGENT\": \"infra\", \"PARLEY_OWNS\": \"Shared.\"");

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, null, TestContext.Current.CancellationToken));

        Assert.Null(Read()["mcpServers"]!["parley-acme"]!["env"]!["PARLEY_OWNS"]);
    }

    [Fact]
    public async Task A_shared_line_another_machine_may_read_is_refused_rather_than_taken()
    {
        var before = Written(
            $$"""{ "server": { "parley-acme": { } }, "{{Here}}": { "parley-acme": { } } }""",
            "\"PARLEY_AGENT\": \"infra\", \"PARLEY_OWNS\": \"Shared.\"");

        var refused = await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, null, TestContext.Current.CancellationToken);

        Assert.Contains("server", refused, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(LaunchFile));
    }

    [Fact]
    public async Task Another_machines_agent_is_still_refused_and_nothing_is_written()
    {
        var before = Written($$"""{ "server": {{Block("Makes changes.")}} }""");

        var refused = await OwnershipLine.WriteAsync(
            Workspace, "acme", AgentName.Create("infra@server"), "Mine.", TestContext.Current.CancellationToken);

        Assert.NotNull(refused);
        Assert.Equal(before, File.ReadAllText(LaunchFile));
    }

    [Fact]
    public async Task A_broken_section_is_never_written_into()
    {
        var before = Written($$"""{ "server": { "parley-acme": { "PARLEY_AGENT": "other" } } }""");

        var refused = await OwnershipLine.WriteAsync(
            Workspace, "acme", Agent, "Mine.", TestContext.Current.CancellationToken);

        Assert.Contains("PARLEY_AGENT", refused, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(LaunchFile));
    }

    [Fact]
    public async Task A_window_reaches_the_entry_this_machines_block_goes_through()
    {
        Written($$"""{ "{{Here}}": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19100" } } }""");

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, ProjectManifest.WindowName, Agent, "Mine.", TestContext.Current.CancellationToken));

        Assert.Equal("Mine.", (string?)Read()["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_OWNS"]);
    }

    [Fact]
    public async Task A_window_with_no_block_address_is_refused()
    {
        var before = Written($$"""{ "{{Here}}": { "parley-acme": { } } }""");

        Assert.NotNull(await OwnershipLine.WriteAsync(
            Workspace, ProjectManifest.WindowName, Agent, "Mine.", TestContext.Current.CancellationToken));

        Assert.Equal(before, File.ReadAllText(LaunchFile));
    }

    [Fact]
    public async Task Withdrawing_leaves_the_entry_while_another_machine_answers_through_it()
    {
        Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": {{Block("Answers.")}} }""");

        _ = await Enrolment.WithdrawAsync(Workspace, "infra", "acme", null, TestContext.Current.CancellationToken);

        var root = Read();

        Assert.NotNull(root["mcpServers"]!["parley-acme"]);
        Assert.NotNull(root["parleyAgentId"]);
        Assert.Null(root["parleyMachines"]![Here]);
        Assert.Equal("Makes changes.", (string?)root["parleyMachines"]!["server"]!["parley-acme"]!["PARLEY_OWNS"]);
    }

    [Fact]
    public async Task Withdrawing_on_a_machine_with_no_block_takes_nothing_from_the_one_that_has()
    {
        var before = Written($$"""{ "server": {{Block("Makes changes.")}} }""");

        _ = await Enrolment.WithdrawAsync(Workspace, "infra", "acme", null, TestContext.Current.CancellationToken);

        Assert.Equal(before, File.ReadAllText(LaunchFile));
    }

    [Fact]
    public async Task The_last_block_takes_the_entry_the_section_and_the_identity_with_it()
    {
        Written($$"""{ "{{Here}}": {{Block("Answers.")}} }""");

        _ = await Enrolment.WithdrawAsync(Workspace, "infra", "acme", null, TestContext.Current.CancellationToken);

        var root = Read();

        Assert.Null(root["mcpServers"]!["parley-acme"]);
        Assert.Null(root["parleyMachines"]);
        Assert.Null(root["parleyAgentId"]);
        Assert.NotNull(root["mcpServers"]!["other"]);
    }

    [Fact]
    public async Task Withdrawing_keeps_this_machines_catalogue_line_only_while_it_still_answers()
    {
        Written($$"""{ "server": {{Block("Makes changes.")}}, "{{Here}}": {{Block("Answers.")}} }""");

        var catalogue = Path.Combine(_root, "node.json");
        File.WriteAllText(
            catalogue,
            """{ "agents": { "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011": { "command": "claude" } } }""");

        _ = await Enrolment.WithdrawAsync(Workspace, "infra", "acme", catalogue, TestContext.Current.CancellationToken);

        Assert.Empty(JsonNode.Parse(File.ReadAllText(catalogue))!["agents"]!.AsObject());
    }

    [Fact]
    public void Enrolling_adds_this_machines_block_and_keeps_the_others()
    {
        Written("""{ "server": { "parley-acme": { "PARLEY_OWNS": "Makes changes." } } }""");

        var root = Planned("infra", "Answers.");

        Assert.Equal("Answers.", (string?)root["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_OWNS"]);
        Assert.Equal("Makes changes.", (string?)root["parleyMachines"]!["server"]!["parley-acme"]!["PARLEY_OWNS"]);
        Assert.Equal("9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011", (string?)root["parleyAgentId"]);
    }

    [Fact]
    public void A_new_entry_holds_no_path_and_an_empty_block_is_written()
    {
        var root = Planned("infra", null, launch: null);

        var env = root["mcpServers"]!["parley-acme"]!["env"]!.AsObject();

        Assert.False(env.ContainsKey("PARLEY_WORKSPACE"));
        Assert.False(env.ContainsKey("PARLEY_OWNS"));
        Assert.DoesNotContain(_root.Replace('\\', '/'), root.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(root["parleyMachines"]![Here]!["parley-acme"]!.AsObject());
    }

    [Fact]
    public void An_address_goes_in_this_machines_block_with_the_project_shared()
    {
        var root = Planned("infra", null, launch: null, address: "http://127.0.0.1:19100");

        Assert.Equal("acme", (string?)root["mcpServers"]!["parley-acme"]!["env"]!["PARLEY_PROJECT"]);
        Assert.Null(root["mcpServers"]!["parley-acme"]!["env"]!["PARLEY_BROKER"]);
        Assert.Equal("http://127.0.0.1:19100", (string?)root["parleyMachines"]![Here]!["parley-acme"]!["PARLEY_BROKER"]);
    }

    [Fact]
    public void Re_enrolling_keeps_shared_values_except_a_folder()
    {
        File.WriteAllText(
            LaunchFile,
            """{ "mcpServers": { "parley-acme": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme", "PARLEY_OWNS": "Shared.", "PARLEY_RESPONDER": "some-model", "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-C:/elsewhere}" } } } }""");

        var env = Planned("infra", null)["mcpServers"]!["parley-acme"]!["env"]!.AsObject();

        Assert.Equal("Shared.", (string?)env["PARLEY_OWNS"]);
        Assert.Equal("some-model", (string?)env["PARLEY_RESPONDER"]);
        Assert.False(env.ContainsKey("PARLEY_WORKSPACE"));
    }

    [Fact]
    public void Enrolling_under_another_role_is_refused_while_another_machine_uses_the_entry()
    {
        Written("""{ "server": { "parley-acme": { } } }""");

        var refused = Assert.Throws<InvalidDataException>(() => Planned("renamed", null));

        Assert.Contains("server", refused.Message, StringComparison.Ordinal);
        Assert.Contains("renamed", refused.Message, StringComparison.Ordinal);
        Assert.Contains("infra", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolling_under_another_role_is_allowed_when_nobody_else_uses_the_entry()
    {
        Written($$"""{ "{{Here}}": { "parley-acme": { } } }""");

        Assert.Equal("renamed", (string?)Planned("renamed", null)["mcpServers"]!["parley-acme"]!["env"]!["PARLEY_AGENT"]);
    }

    [Fact]
    public void A_broken_section_stops_an_enrolment()
    {
        Written("""{ "server": { "parley-acme": { "PARLEY_AGENT": "other" } } }""");

        var refused = Assert.Throws<InvalidDataException>(() => Planned("infra", null));

        Assert.Contains("PARLEY_AGENT", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolling_again_changes_nothing()
    {
        var entry = new EnrollmentEntry(Workspace, "infra", "Answers.", Unattended: true);
        var first = EnrollmentPlan.Describe(entry, null, "x\n## Peers\n", nodeAlreadyLists: true, "acme", nodeWorkspace: Workspace);
        var second = EnrollmentPlan.Describe(entry, first.LaunchConfiguration, "x\n## Peers\n", nodeAlreadyLists: true, "acme", nodeWorkspace: Workspace);

        Assert.Null(second.LaunchConfiguration);
    }

    private JsonObject Planned(string role, string? owns, string? launch = "read", string? address = null)
    {
        var text = launch is null ? null : File.ReadAllText(LaunchFile);
        var plan = EnrollmentPlan.Describe(
            new EnrollmentEntry(Workspace, role, owns, Unattended: true),
            text,
            "x\n## Peers\n",
            nodeAlreadyLists: true,
            "acme",
            nodeWorkspace: Workspace,
            brokerAddress: address);

        return (JsonObject)JsonNode.Parse(plan.LaunchConfiguration ?? text!)!;
    }
}
