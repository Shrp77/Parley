using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Reading the parley servers out of a launch configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this prevents is a reader that answers "nowhere" about a repository that is
/// enrolled.</b> Two readers took <c>mcpServers.parley.env</c> by literal key, so the moment an entry
/// could sit under another name they would have reported every multiply-enrolled repository as
/// unenrolled — and a scan that says a repository is enrolled nowhere is how it comes to be enrolled
/// twice in one project. This is the same shape as 007's <c>--console</c> reader, one layer down.
/// </para>
/// <para>
/// The malicious and the merely untidy cases are here together on purpose: a stranger's server, an
/// entry naming nobody, an entry whose key and environment disagree. Each of them is a file somebody
/// edited by hand, which is the ordinary state of this file rather than an attack.
/// </para>
/// </remarks>
[Unit]
public sealed class LaunchEntriesTests
{
    private static JsonObject Servers(string json) =>
        (JsonObject)JsonNode.Parse(json)!["mcpServers"]!;

    [Fact]
    public void Nothing_at_all_is_no_entries_rather_than_a_failure()
    {
        Assert.Empty(LaunchEntries.Read(null));
        Assert.Empty(LaunchEntries.Read([]));
    }

    [Fact]
    public void A_legacy_entry_is_read_and_its_project_comes_from_its_environment()
    {
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders",
                "PARLEY_PROJECT": "alpha",
                "PARLEY_OWNS": "Carts and totals."
            } } } }
            """));

        var entry = Assert.Single(entries);

        Assert.Equal("parley", entry.Key);
        Assert.Equal("alpha", entry.Project);
        Assert.Equal("orders", entry.Agent);
        Assert.Equal("Carts and totals.", entry.Owns);
        Assert.False(entry.Mismatched);
    }

    [Fact]
    public void An_entry_enrolled_before_projects_existed_names_no_project()
    {
        // The shape every repository in a deployment older than project names has. Null here is a
        // fact about the file, and it is the one case the precedence may re-point in place.
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders"
            } } } }
            """));

        Assert.Null(Assert.Single(entries).Project);
    }

    [Fact]
    public void A_project_keyed_entry_takes_its_project_from_the_key_when_the_environment_is_silent()
    {
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley-beta": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "stock"
            } } } }
            """));

        Assert.Equal("beta", Assert.Single(entries).Project);
    }

    [Fact]
    public void One_repository_may_hold_an_entry_for_each_project_it_answers_in()
    {
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "alpha",
                    "PARLEY_OWNS": "Stock levels. Not pricing." } },
                "parley-beta": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "stock", "PARLEY_PROJECT": "beta",
                    "PARLEY_OWNS": "What is on hand." } }
            } }
            """));

        Assert.Equal(2, entries.Count);
        Assert.Equal(["alpha", "beta"], entries.Select(entry => entry.Project));

        // Different names and different owns lines for one repository, which is the point rather than
        // an accident: the same repository is worth asking about different things by different rosters.
        Assert.Equal(["inventory", "stock"], entries.Select(entry => entry.Agent));
        Assert.Equal(2, entries.Select(entry => entry.Owns).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_legacy_entry_and_a_project_keyed_one_are_both_found()
    {
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": {
                "parley": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": {
                    "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta" } }
            } }
            """));

        Assert.Equal(["parley", "parley-beta"], entries.Select(entry => entry.Key));
    }

    [Fact]
    public void Somebody_elses_server_is_skipped_rather_than_reported()
    {
        // Not damaged, not ours, not this program's business. A repository may run any number of
        // other MCP servers and every one of them must survive everything here untouched.
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": {
                "some-other-server": { "command": "whatever" },
                "parleyx": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } },
                "parley-Alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } },
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
            } }
            """));

        Assert.Equal("parley-alpha", Assert.Single(entries).Key);
    }

    [Fact]
    public void An_entry_at_our_key_that_names_nobody_is_read_and_names_nobody()
    {
        // Deliberately not skipped. It occupies a key this tooling would write at, so a writer has to
        // see it in order to refuse; and it names nobody, so a withdrawal may take it. Those are two
        // different decisions made by two different callers, and both need the entry.
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley-alpha": { "command": "something-else" } } }
            """));

        var entry = Assert.Single(entries);

        Assert.Null(entry.Agent);
        Assert.Equal("alpha", entry.Project);
    }

    [Fact]
    public void An_address_pinned_entry_is_read_with_its_address()
    {
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_BROKER": "127.0.0.1:51707"
            } } } }
            """));

        var entry = Assert.Single(entries);

        Assert.Equal("127.0.0.1:51707", entry.Broker);
        Assert.Null(entry.Project);
    }

    [Fact]
    public void A_key_and_an_environment_naming_different_projects_is_marked_rather_than_resolved()
    {
        // The environment wins, because that is what the running adapter reads — but the disagreement
        // is carried out to the caller, because the two readings send a withdrawal to two different
        // entries and choosing one silently is how the wrong project's configuration is deleted.
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta"
            } } } }
            """));

        var entry = Assert.Single(entries);

        Assert.Equal("beta", entry.Project);
        Assert.True(entry.Mismatched);
    }

    [Fact]
    public void An_empty_variable_is_the_same_as_an_absent_one()
    {
        var entries = LaunchEntries.Read(Servers("""
            { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "  ", "PARLEY_OWNS": "", "PARLEY_PROJECT": 7
            } } } }
            """));

        var entry = Assert.Single(entries);

        Assert.Null(entry.Agent);
        Assert.Null(entry.Owns);

        // A number where a name should be answers "no name", not a crash: the file is edited by hand.
        Assert.Null(entry.Project);
    }
}
