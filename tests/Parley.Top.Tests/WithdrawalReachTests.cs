using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That withdrawing one agent reaches that agent's files and no other's.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.7. Every earlier probe of this path asked whether the
/// destructive act happens when it should. This one asks the other question: when it does happen,
/// what does it touch — and specifically, whether the file it edits belongs to the agent whose name
/// was typed back.
/// </para>
/// <para>
/// The workspace comes from the registry, which is what the agent said about itself, possibly some
/// time ago. A repository re-enrolled under a second name has one launch configuration and two
/// registry rows pointing at it, and the first row's withdrawal must not take the second row's
/// entry with it. <c>PARLEY_AGENT</c> in that entry is the whole of the trust model per SPEC.md
/// §16 — deleting somebody else's copy of it is a destructive act on an agent nobody named.
/// </para>
/// </remarks>
public sealed class WithdrawalReachTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-reach-" + Path.GetRandomFileName());

    public WithdrawalReachTests() => Directory.CreateDirectory(Workspace);

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

    private string Workspace => Path.Combine(_root, "shared");

    private string LaunchPath => Path.Combine(Workspace, ".mcp.json");

    private string CataloguePath => Path.Combine(_root, "acme-node.json");

    [Fact]
    public async Task An_entry_that_names_a_different_agent_is_left_where_it_is()
    {
        // The case a stale workspace produces. billing re-enrolled this repository; orders' registry
        // row still points at it. Withdrawing orders must not unhook billing.
        await WriteLaunchAsync("billing@test");
        await WriteCatalogueAsync("orders@test", "billing@test");

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", CataloguePath, TestContext.Current.CancellationToken);

        Assert.Equal("billing@test", await NamedAsync());
        Assert.Contains(
            outcome.Notes,
            note => note.Contains("billing@test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_catalogue_line_still_goes_when_the_launch_file_stays()
    {
        // The two halves are separate facts. The catalogue line is keyed by the agent's own name, so
        // it is unambiguously this agent's and goes; the file is shared and does not.
        await WriteLaunchAsync("billing@test");
        await WriteCatalogueAsync("orders@test", "billing@test");

        await Enrolment.WithdrawAsync(
            Workspace, "orders@test", CataloguePath, TestContext.Current.CancellationToken);

        var catalogue = await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("orders@test", catalogue, StringComparison.Ordinal);
        Assert.Contains("billing@test", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_that_names_this_agent_goes()
    {
        await WriteLaunchAsync("orders@test");
        await WriteCatalogueAsync("orders@test");

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", CataloguePath, TestContext.Current.CancellationToken);

        Assert.Null(await NamedAsync());
        Assert.Contains(LaunchPath, outcome.Written);
    }

    [Fact]
    public async Task An_entry_that_names_nobody_goes()
    {
        // An entry written before names were recorded, or by hand. It cannot be somebody else's,
        // because nothing identifies it as anybody's, so the withdrawal proceeds as it always did.
        await File.WriteAllTextAsync(
            LaunchPath,
            """{"mcpServers":{"parley":{"command":"parley-mcp"}}}""",
            TestContext.Current.CancellationToken);

        await WriteCatalogueAsync("orders@test");

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", CataloguePath, TestContext.Current.CancellationToken);

        Assert.Contains(LaunchPath, outcome.Written);
    }

    [Fact]
    public async Task Other_servers_in_a_shared_file_are_untouched_either_way()
    {
        // The existing promise, re-checked on the path that now declines: declining must not become
        // a rewrite, and the file must be byte-identical when nothing was this agent's.
        await WriteLaunchAsync("billing@test", withOther: true);

        var before = await File.ReadAllTextAsync(LaunchPath, TestContext.Current.CancellationToken);

        await Enrolment.WithdrawAsync(
            Workspace, "orders@test", null, TestContext.Current.CancellationToken);

        Assert.Equal(before, await File.ReadAllTextAsync(LaunchPath, TestContext.Current.CancellationToken));
    }

    /// <summary>What the launch configuration's parley entry says it is, or null.</summary>
    private async Task<string?> NamedAsync()
    {
        var parsed = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchPath, TestContext.Current.CancellationToken));

        return parsed?["mcpServers"]?["parley"]?["env"]?["PARLEY_AGENT"]?.GetValue<string>();
    }

    private Task WriteLaunchAsync(string agent, bool withOther = false)
    {
        var servers = new JsonObject
        {
            ["parley"] = new JsonObject
            {
                ["command"] = "parley-mcp",
                ["env"] = new JsonObject { ["PARLEY_AGENT"] = agent },
            },
        };

        if (withOther)
        {
            servers["other"] = new JsonObject { ["command"] = "something-else" };
        }

        return File.WriteAllTextAsync(
            LaunchPath,
            new JsonObject { ["mcpServers"] = servers }.ToJsonString(),
            TestContext.Current.CancellationToken);
    }

    private Task WriteCatalogueAsync(params string[] agents)
    {
        var listed = new JsonObject();

        foreach (var agent in agents)
        {
            Enrolment.List(listed, agent, Workspace);
        }

        return File.WriteAllTextAsync(
            CataloguePath,
            new JsonObject { ["agents"] = listed }.ToJsonString(),
            TestContext.Current.CancellationToken);
    }
}
