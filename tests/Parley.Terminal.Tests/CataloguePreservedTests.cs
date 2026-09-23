using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That adding one agent keeps everything already in the two files it touches.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.10. Run 2.7 asked what a withdrawal destroys that nobody named.
/// This asks the same question of the other direction, and the files are shared in a way that makes
/// it live rather than theoretical: a project's catalogue lists every agent a node may start, and a
/// repository's launch configuration may name servers that have nothing to do with Parley.
/// </para>
/// <para>
/// The catalogue is also written by <c>parley project set</c> and edited by hand, and this surface
/// draws from it continuously — so the interval between the draw and the confirmation is one in
/// which somebody may have changed it. Reading it again at the moment of writing is what makes the
/// write true, and this is the probe that it does.
/// </para>
/// <para>
/// <b>These enrolled <c>orders@test</c> and asserted a catalogue keyed by it, which is a shape no node
/// can act on.</b> A catalogue is keyed by role — <c>NodeCatalogue.WorkspaceFor</c> looks up
/// <c>agent.Role</c> and <c>NodesService.RequestStart</c> guards on <c>Startable.Contains(agent.Role)</c>
/// — so an entry under a qualified name is never found and never started. The adapter accepts either
/// spelling in <c>PARLEY_AGENT</c>, which is what made the qualified one look admissible here; that is
/// the adapter's tolerance for a hand-written file, not a licence for this writer to emit either.
/// Corrected during lens 2 run 20, along with the enrolment that had been willing to write it.
/// </para>
/// </remarks>
public sealed class CataloguePreservedTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-kept-" + Path.GetRandomFileName());

    public CataloguePreservedTests()
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

    private string CataloguePath => Path.Combine(_root, "acme-node.json");

    private string InstructionsPath => Path.Combine(Workspace, "CLAUDE.md");

    private string LaunchPath => Path.Combine(Workspace, ".mcp.json");

    [Fact]
    public async Task An_agent_added_beside_another_leaves_the_other_where_it_was()
    {
        var billing = AgentIdentity.Mint();

        await WriteCatalogueAsync((billing, "/srv/billing"));

        var orders = await AddAsync("orders");

        var agents = await AgentsAsync();

        Assert.True(agents.ContainsKey(orders.ToString()));
        Assert.Equal("/srv/billing", agents[billing.ToString()]!["workspace"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_catalogue_edited_between_the_draw_and_the_confirmation_keeps_the_edit()
    {
        // The interval that matters. The surface draws from this file and somebody may change it in
        // the next window; a write built from what was drawn would silently undo them.
        var inventory = AgentIdentity.Mint();

        await WriteCatalogueAsync();

        await WriteCatalogueAsync((inventory, "/srv/inventory"));

        var orders = await AddAsync("orders");

        var agents = await AgentsAsync();

        Assert.True(agents.ContainsKey(inventory.ToString()));
        Assert.True(agents.ContainsKey(orders.ToString()));
    }

    [Fact]
    public async Task Keys_this_program_does_not_understand_survive_a_write()
    {
        // A catalogue is a node's file, not this surface's, and a future version of the node may put
        // things in it that this build has never heard of. Dropping them would be a silent
        // downgrade of somebody's configuration performed by a screen they used for something else.
        var catalogue = new JsonObject
        {
            ["agents"] = new JsonObject(),
            ["broker"] = "http://127.0.0.1:6000",
            ["somethingLater"] = new JsonObject { ["kept"] = true },
        };

        await File.WriteAllTextAsync(
            CataloguePath, catalogue.ToJsonString(), TestContext.Current.CancellationToken);

        _ = await AddAsync("orders");

        var written = JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!;

        Assert.Equal("http://127.0.0.1:6000", written["broker"]!.GetValue<string>());
        Assert.True(written["somethingLater"]!["kept"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Another_mcp_server_in_the_repository_is_left_alone()
    {
        await File.WriteAllTextAsync(
            LaunchPath,
            """{"mcpServers":{"other":{"command":"something-else"}}}""",
            TestContext.Current.CancellationToken);

        await WriteCatalogueAsync();

        _ = await AddAsync("orders");

        var launch = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchPath, TestContext.Current.CancellationToken))!;

        Assert.Equal(
            "something-else",
            launch["mcpServers"]!["other"]!["command"]!.GetValue<string>());

        Assert.NotNull(launch["mcpServers"]!["parley-acme"]);
    }

    [Fact]
    public async Task An_instructions_file_that_already_has_a_peers_section_is_left_byte_for_byte()
    {
        // Enrolment says the instructions file is never touched by either half, because nothing here
        // can tell a line the project wrote from a line a tool did. Asserted byte for byte.
        await File.WriteAllTextAsync(
            InstructionsPath,
            "# orders\n\n## Peers\n\nSomething a person wrote and a tool must not rewrite.\n",
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(InstructionsPath, TestContext.Current.CancellationToken);

        await WriteCatalogueAsync();

        _ = await AddAsync("orders");

        Assert.Equal(
            before,
            await File.ReadAllTextAsync(InstructionsPath, TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task An_instructions_file_with_no_peers_section_gains_one_and_nothing_else()
    {
        // The other half, and the one the documentation and the code disagreed about until this was
        // written: `Enrolment` says the instructions file is never touched by either half, and
        // `EnrollmentPlan` appends the peers frame when there is no section to refuse. Both cannot
        // be true. What the code does is the narrower thing -- it appends and never rewrites -- so
        // what is asserted here is that, and the remark has been corrected to say it.
        await File.WriteAllTextAsync(
            InstructionsPath,
            "# orders" + Environment.NewLine + Environment.NewLine + "Something a person wrote."
            + Environment.NewLine,
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(InstructionsPath, TestContext.Current.CancellationToken);

        await WriteCatalogueAsync();

        _ = await AddAsync("orders");

        var after = await File.ReadAllTextAsync(InstructionsPath, TestContext.Current.CancellationToken);

        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.Contains(EnrollmentPlan.PeersHeading, after, StringComparison.Ordinal);
    }

    /// <summary>Enrols this repository, and says which agent the enrolment decided it is.</summary>
    /// <param name="agent">What this project calls it.</param>
    /// <returns>The identity the catalogue entry is keyed by.</returns>
    /// <remarks>
    /// The identity is read back out of the repository rather than returned by the call, because that is
    /// where it lives (018 R2) and reading it from the file is what proves the enrolment wrote it there.
    /// </remarks>
    private async Task<AgentIdentity> AddAsync(string agent)
    {
        await Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, agent, null, true),
            "acme",
            CataloguePath,
            InstructionsPath,
            TestContext.Current.CancellationToken);

        Assert.True(LaunchConfiguration.TryRead(Workspace, out var declared, out var problem), problem);
        Assert.True(declared.Identity.Known, "the enrolment left the repository with no identity");

        return declared.Identity;
    }

    private async Task<JsonObject> AgentsAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!["agents"]!;

    /// <summary>Writes a catalogue holding the agents given, each at its own path.</summary>
    /// <param name="agents">Which agents, and where each is.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <remarks>
    /// Keyed by identity, because a catalogue is (018). A role-keyed fixture reads as a machine that can
    /// start nothing, which would make every "it was left where it was" assertion below pass by having
    /// nothing to leave.
    /// </remarks>
    private Task WriteCatalogueAsync(params (AgentIdentity Agent, string Workspace)[] agents)
    {
        var listed = new JsonObject();

        foreach (var (agent, workspace) in agents)
        {
            Enrolment.List(listed, agent, workspace);
        }

        return File.WriteAllTextAsync(
            CataloguePath,
            new JsonObject { ["agents"] = listed }.ToJsonString(),
            TestContext.Current.CancellationToken);
    }
}
