using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 5: the file a node runs.
/// </summary>
/// <remarks>
/// <para>
/// The launch configuration is read by an MCP client; the <b>catalogue</b> is read by the node, which
/// starts the command it names, in the directory it names, with nobody watching. It is the only file
/// this feature writes whose contents are executed — so what may reach a <c>command</c>, and what may
/// be overwritten in one, is a different question from everything the earlier runs asked.
/// </para>
/// <para>
/// Two directions matter. Nothing a person types into an enrolment manifest may decide what gets run;
/// and an entry a person put there deliberately must not be replaced by a run that was about something
/// else.
/// </para>
/// </remarks>
public sealed class ProbeSixteenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe16-" + Path.GetRandomFileName());

    public ProbeSixteenTests() => Directory.CreateDirectory(_root);

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

    private string Catalogue => Path.Combine(_root, "node.json");

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private Task<Enrolment.Outcome> EnrolAsync(string workspace, string agent) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(workspace, agent, "What it owns. Not what it does not.", Unattended: true)
            {
                Enroll = true,
            },
            "alpha",
            Catalogue,
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    private async Task<JsonObject> AgentsAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(Catalogue, TestContext.Current.CancellationToken))!["agents"]!;

    /// <summary>Which agent a checkout says it is.</summary>
    /// <param name="workspace">The checkout.</param>
    /// <returns>Its identity.</returns>
    private static AgentIdentity IdentityOf(string workspace)
    {
        Assert.True(LaunchConfiguration.TryRead(workspace, out var declared, out var problem), problem);

        return declared.Identity;
    }

    /// <summary>Gives a checkout an identity and catalogues it, the way an earlier enrolment would have.</summary>
    /// <param name="workspace">The checkout, whose launch configuration is written.</param>
    /// <param name="command">What the entry says to run.</param>
    /// <param name="at">Where the entry says the checkout is.</param>
    /// <param name="beside">Another machine-mate's entry, or null.</param>
    /// <returns>The identity the checkout was given.</returns>
    /// <remarks>
    /// **Both files, because 018 split what they say.** The catalogue entry is keyed by an identity and
    /// the identity lives in the repository, so a fixture that wrote only the catalogue would describe a
    /// state no enrolment can produce — and the run under test would rightly mint a second identity and
    /// leave the seeded entry alone, which is not the case any of these probes is about.
    /// </remarks>
    private async Task<AgentIdentity> SeedAsync(
        string workspace, string command, string at, (AgentIdentity Agent, string At)? beside = null)
    {
        var identity = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            Path.Combine(workspace, LaunchConfiguration.FileName),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """,
            TestContext.Current.CancellationToken);

        var others = beside is { } other
            ? $$""",{{"\n"}}    "{{other.Agent}}": { "command": "claude", "workspace": "{{other.At}}" }"""
            : string.Empty;

        await File.WriteAllTextAsync(
            Catalogue,
            $$"""
            {
              "agents": {
                "{{identity}}": { "command": {{System.Text.Json.JsonSerializer.Serialize(command)}}, "workspace": {{System.Text.Json.JsonSerializer.Serialize(at)}} }{{others}}
              }
            }
            """,
            TestContext.Current.CancellationToken);

        return identity;
    }

    [Fact]
    public async Task Nothing_in_a_manifest_decides_what_the_node_will_run()
    {
        // The command is this tooling's, not the manifest's. An enrolment manifest is an ordinary
        // file a person edits, and if anything in it reached `command` then editing one would be
        // choosing what runs unattended on the machine.
        var workspace = Workspace("orders");

        await EnrolAsync(workspace, "orders");

        var listed = (await AgentsAsync())[IdentityOf(workspace).ToString()]!;

        Assert.Equal("claude", listed["command"]!.GetValue<string>());
        Assert.Equal(workspace, listed["workspace"]!.GetValue<string>());
    }

    [Fact]
    public async Task Enrolling_one_agent_leaves_another_agents_line_exactly_as_it_was()
    {
        // The catalogue holds every agent this machine may start for the project. A run about one of
        // them that rewrote another's line would change what runs, unattended, for an agent nobody
        // mentioned — and the person would have no reason to look.
        var billing = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            Catalogue,
            $$"""
            {
              "agents": {
                "{{billing}}": { "command": "claude --some-flag", "workspace": "/srv/billing" }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var before = (await AgentsAsync())[billing.ToString()]!.ToJsonString();

        await EnrolAsync(Workspace("orders"), "orders");

        Assert.Equal(before, (await AgentsAsync())[billing.ToString()]!.ToJsonString());
    }

    [Fact]
    public async Task Re_enrolling_an_agent_whose_command_was_customised_says_what_it_did()
    {
        // **The one place this could lose something a person chose deliberately.** A catalogue entry
        // may name a command somebody edited — the file is theirs — and re-enrolment writes the entry
        // afresh. Whether it keeps or replaces is a judgement; doing either silently is not.
        var identity = await SeedAsync(
            Workspace("orders"),
            "claude --dangerously-skip-permissions",
            Workspace("orders").Replace(Path.DirectorySeparatorChar, '/'));

        var outcome = await EnrolAsync(Workspace("orders"), "orders");

        var command = (await AgentsAsync())[identity.ToString()]!["command"]!.GetValue<string>();

        if (string.Equals(command, "claude", StringComparison.Ordinal))
        {
            Assert.Contains(
                outcome.Notes,
                note => note.Contains("node.json", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_withdrawal_takes_one_line_out_of_the_catalogue_and_no_other()
    {
        var billing = AgentIdentity.Mint();

        var orders = await SeedAsync(
            Workspace("orders"), "claude", "/srv/orders", (billing, "/srv/billing"));

        await Enrolment.WithdrawAsync(
            Workspace("orders"), "orders", "alpha", Catalogue, TestContext.Current.CancellationToken);

        var agents = await AgentsAsync();

        Assert.False(agents.ContainsKey(orders.ToString()));
        Assert.Equal("/srv/billing", agents[billing.ToString()]!["workspace"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_workspace_recorded_is_the_one_enrolled_and_not_one_read_from_elsewhere()
    {
        // The node starts the command *in this directory*. A path that came from anywhere but the
        // enrolment — another entry, the registry, a stale line — would run an agent's work against
        // somebody else's checkout.
        var workspace = Workspace("orders");

        // **The checkout is catalogued at a path nothing is at, which is what a move looks like** (018).
        // Enrolling it here repoints the one entry rather than adding a second, and the path that ends up
        // in it must be the one this run was given.
        var identity = await SeedAsync(workspace, "claude", "/somewhere/else");

        await EnrolAsync(workspace, "orders");

        Assert.Equal(
            workspace, (await AgentsAsync())[identity.ToString()]!["workspace"]!.GetValue<string>());
    }
}
