using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Which entry an enrolment lands on, case by case.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case here is a different answer to "does a live agent's vocabulary change".</b> A client
/// namespaces an MCP server's tools by its key, so an entry moved from <c>parley</c> to
/// <c>parley-alpha</c> turns <c>mcp__parley__parley_request</c> into <c>mcp__parley-alpha__parley_request</c>
/// in a repository nobody asked to change. The precedence exists so that never happens by accident,
/// and each case is named for the situation rather than numbered, because the numbering is an
/// implementation detail and the situation is what somebody debugging is looking at.
/// </para>
/// </remarks>
[Unit]
public sealed class LaunchPrecedenceTests
{
    private static JsonObject Servers(string json) =>
        (JsonObject)JsonNode.Parse(json)!["mcpServers"]!;

    [Fact]
    public void A_repository_in_no_project_at_all_gets_the_derived_key()
    {
        var (key, existing) = LaunchEntries.Landing(null, "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.Null(existing);
    }

    [Fact]
    public void An_entry_already_at_the_derived_key_is_the_one_updated()
    {
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } } } }
                """),
            "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.Equal("orders", existing?.Agent);
    }

    [Fact]
    public void A_legacy_entry_pinned_to_this_project_is_updated_where_it_stands()
    {
        // The case the whole precedence exists for. Every repository in an existing deployment is
        // this shape, and the right answer is the key it already has.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } } } }
                """),
            "alpha");

        Assert.Equal("parley", key);
        Assert.Equal("orders", existing?.Agent);
    }

    [Fact]
    public void A_legacy_entry_pinned_elsewhere_is_left_alone_and_a_new_entry_is_added()
    {
        // A repository answering in alpha, being enrolled in beta. The alpha entry is not the
        // landing site: it is somebody's working configuration for a different roster.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } } } }
                """),
            "beta");

        Assert.Equal("parley-beta", key);
        Assert.Null(existing);
    }

    [Fact]
    public void An_entry_naming_no_project_is_re_pointed_where_it_stands()
    {
        // A `parley` entry that pins no project is updated in place when one is named.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders" } } } }
                """),
            "alpha");

        Assert.Equal("parley", key);
        Assert.Equal("orders", existing?.Agent);
    }

    [Fact]
    public void A_pinned_entry_wins_over_an_unpinned_one_for_its_own_project()
    {
        // The ordering that stops an unpinned entry swallowing an enrolment meant to be a second one.
        // Both are candidates by shape; only one of them is this project's.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": {
                    "parley-alpha": { "command": "parley-mcp", "env": {
                        "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } },
                    "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "legacy" } }
                } }
                """),
            "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.Equal("orders", existing?.Agent);
    }

    [Fact]
    public void An_entry_whose_key_and_environment_disagree_is_handed_back_rather_than_hidden()
    {
        // The key says alpha and the running adapter would reach beta. This must reach the caller:
        // writing at the key would silently re-point beta's agent, and skipping the key would write
        // over the entry that is there. Neither is a decision this function may make.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta" } } } }
                """),
            "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.True(existing?.Mismatched);
    }

    [Fact]
    public void A_stranger_at_the_derived_key_is_handed_back_naming_nobody()
    {
        // The raw material of refusing to replace a stranger: the key is ours to write at, the entry there is not ours to replace.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""{ "mcpServers": { "parley-alpha": { "command": "something-else" } } }"""),
            "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.NotNull(existing);
        Assert.Null(existing.Agent);
    }

    [Fact]
    public void With_no_project_named_the_landing_is_the_key_every_repository_already_has()
    {
        // SPEC.md 4.1 keeps a deployment configured entirely by explicit paths supported, and this is
        // what enrolling in one writes: exactly what it wrote before this feature existed.
        var (key, existing) = LaunchEntries.Landing(
            Servers("""
                { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders" } } } }
                """),
            project: null);

        Assert.Equal("parley", key);
        Assert.Equal("orders", existing?.Agent);
    }

    [Fact]
    public void Other_servers_are_never_a_landing_site()
    {
        var (key, existing) = LaunchEntries.Landing(
            Servers("""{ "mcpServers": { "some-other-server": { "command": "whatever" } } }"""),
            "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.Null(existing);
    }
}
