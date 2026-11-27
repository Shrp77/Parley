using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node finds the checkout a command means by the identity the broker sent, and by the name only when none came
/// (SPEC.md §4.1.1).
/// </summary>
/// <remarks>
/// A repository renamed in its launch configuration answers to a new role while the broker holds the old name until the
/// agent attaches. The name no longer resolves; the identifier still does, and an identifier this machine does not hold
/// falls back to the name rather than failing.
/// </remarks>
[Integration]
public sealed class StartByIdentityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-byid-" + Path.GetRandomFileName());

    public StartByIdentityTests() => Directory.CreateDirectory(_root);

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

    /// <summary>A catalogue with one checkout, whose repository now calls the agent <c>payments</c>.</summary>
    private async Task<(NodeCatalogue Catalogue, AgentIdentity Identity)> RenamedCheckoutAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "payments", "PARLEY_PROJECT": "alpha" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """,
            ct);

        var path = Path.Combine(_root, NodeCatalogue.DefaultFileName);

        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "agents": {
                "{{identity}}": {
                  "command": {{System.Text.Json.JsonSerializer.Serialize(StandingAgent.Recording(Path.Combine(_root, "launched.txt"), 1))}},
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                }
              }
            }
            """,
            ct);

        return (NodeCatalogue.ReadFrom(path), identity);
    }

    private static async Task<string> StartAsync(NodeCatalogue catalogue, string agentName, string identity, CancellationToken ct)
    {
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var said = new StringWriter();

        await NodeCommand.StartAsync(
            new Wire.StartAgent { Agent = agentName, Because = "asked", Identity = identity },
            catalogue, "alpha", residents, said, ct);

        foreach (var resident in residents.Values)
        {
            await resident.DisposeAsync();
        }

        return said.ToString();
    }

    [Fact]
    public async Task The_identity_finds_a_checkout_whose_repository_no_longer_answers_to_the_name()
    {
        var (catalogue, identity) = await RenamedCheckoutAsync();

        var said = await StartAsync(
            catalogue, "billing@" + NodeCatalogue.Machine, identity.ToString(), TestContext.Current.CancellationToken);

        Assert.Contains(": started (", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_identity_the_old_name_finds_nothing()
    {
        var (catalogue, _) = await RenamedCheckoutAsync();

        var said = await StartAsync(
            catalogue, "billing@" + NodeCatalogue.Machine, string.Empty, TestContext.Current.CancellationToken);

        Assert.Contains("no way to start it", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identity_this_machine_does_not_hold_falls_back_to_the_name()
    {
        var (catalogue, _) = await RenamedCheckoutAsync();

        var said = await StartAsync(
            catalogue,
            "payments@" + NodeCatalogue.Machine,
            AgentIdentity.Mint().ToString(),
            TestContext.Current.CancellationToken);

        Assert.Contains(": started (", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalogue_announces_the_identities_it_can_start_in_a_project()
    {
        var (catalogue, identity) = await RenamedCheckoutAsync();

        Assert.Equal([identity], catalogue.StartableIn("alpha"));
        Assert.Empty(catalogue.StartableIn("beta"));
    }
}
