using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>What the adapter makes of this machine's block (SPEC.md §4.1.2).</summary>
/// <remarks>The machine is <c>test</c>, fixed for this assembly as it is for the rest.</remarks>
[Integration]
public sealed class MachineBlockOptionsTests : IDisposable
{
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";

    private readonly string _root = Directory.CreateTempSubdirectory("parley-block-").FullName;

    public MachineBlockOptionsTests() => Directory.CreateDirectory(Path.Combine(Checkout, ".git"));

    private string Checkout => Path.Combine(_root, "infra");

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

    private void File(string machines, string shared = "") =>
        System.IO.File.WriteAllText(
            Path.Combine(Checkout, ".mcp.json"),
            $$"""
            { "parleyAgentId": "{{Id}}",
              "mcpServers": { "parley-acme": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme"{{shared}} } } }
              {{(machines.Length == 0 ? string.Empty : ", \"parleyMachines\": " + machines)}} }
            """);

    private ServerOptions Read(params (string Name, string Value)[] variables)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PARLEY_AGENT"] = "infra",
            ["PARLEY_PROJECT"] = "acme",
            ["PARLEY_CONFIG"] = Path.Combine(_root, "no-projects-here"),
        };

        foreach (var (name, value) in variables)
        {
            all[name] = value;
        }

        return ServerOptions.Read(name => all.GetValueOrDefault(name), Checkout);
    }

    [Fact]
    public void This_machines_line_and_address_are_used_with_no_project_on_this_machine()
    {
        File("""{ "test": { "parley-acme": { "PARLEY_OWNS": "Makes changes.", "PARLEY_BROKER": "http://127.0.0.1:19391" } } }""");

        var options = Read();

        Assert.Equal("Makes changes.", options.Owns);
        Assert.Equal("http://127.0.0.1:19391", options.Broker);
        Assert.Equal("infra@test", options.Agent.Value);
    }

    [Fact]
    public void An_address_in_a_block_is_said_on_purpose_so_nothing_is_noticed()
    {
        File("""{ "test": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19391" } } }""");

        Assert.Null(Read().BrokerNotice);
    }

    [Fact]
    public void Both_set_outside_a_block_keep_the_notice()
    {
        File("""{ "test": { "parley-acme": { } } }""");

        var options = Read((ServerOptions.BrokerVariable, "http://127.0.0.1:19391"));

        Assert.Contains("both set", options.BrokerNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_beats_the_environment()
    {
        File("""{ "test": { "parley-acme": { "PARLEY_OWNS": "The block's.", "PARLEY_BROKER": "http://127.0.0.1:19391" } } }""");

        var options = Read(
            (ServerOptions.OwnsVariable, "A person's."),
            (ServerOptions.BrokerVariable, "http://127.0.0.1:1"));

        Assert.Equal("The block's.", options.Owns);
        Assert.Equal("http://127.0.0.1:19391", options.Broker);
    }

    [Fact]
    public void Another_machines_block_is_not_this_ones()
    {
        File("""{ "server": { "parley-acme": { "PARLEY_OWNS": "Theirs.", "PARLEY_BROKER": "http://127.0.0.1:19391" } }, "test": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19392" } } }""");

        var options = Read((ServerOptions.OwnsVariable, "The launch's."));

        Assert.Equal("The launch's.", options.Owns);
        Assert.Equal("http://127.0.0.1:19392", options.Broker);
    }

    [Fact]
    public void A_section_makes_the_found_folder_the_workspace_and_gives_the_identity()
    {
        File("""{ "test": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19391" } } }""");

        var options = Read((ServerOptions.WorkspaceVariable, Path.Combine(_root, "another-machines-path")));

        Assert.Equal(Checkout, options.Workspace);
        Assert.Equal(Id, options.Identity.ToString());
    }

    [Fact]
    public void A_file_with_no_section_changes_nothing()
    {
        File(string.Empty, ", \"PARLEY_OWNS\": \"Shared.\"");

        var options = Read((ServerOptions.BrokerVariable, "http://127.0.0.1:19391"));

        Assert.Null(options.Workspace);
        Assert.False(options.Identity.Known);
        Assert.Contains("both set", options.BrokerNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_section_stops_the_launch_and_names_the_setting()
    {
        File("""{ "server": { "parley-acme": { "PARLEY_AGENT": "other" } } }""");

        var refused = Assert.Throws<InvalidOperationException>(() => Read());

        Assert.Contains("server", refused.Message, StringComparison.Ordinal);
        Assert.Contains("PARLEY_AGENT", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_with_no_way_to_a_broker_is_told_which_blocks_the_file_has()
    {
        File("""{ "server": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19391" } } }""");

        var refused = Assert.Throws<InvalidOperationException>(() => Read());

        Assert.Contains($"This machine is {MachineName.Current}", refused.Message, StringComparison.Ordinal);
        Assert.Contains("has blocks for: server", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_line_in_a_block_stops_the_launch()
    {
        File("""{ "test": { "parley-acme": { "PARLEY_OWNS": "two\nlines" } } }""");

        var refused = Assert.Throws<InvalidOperationException>(() => Read());

        Assert.Contains("PARLEY_OWNS", refused.Message, StringComparison.Ordinal);
    }
}
