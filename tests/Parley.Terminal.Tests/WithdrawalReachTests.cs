using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Terminal.Tests;

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
/// <para>
/// <b>018 extended the same reasoning to the catalogue line, and reversed one of these tests.</b> That
/// line used to be keyed by the agent's name, so it was unambiguously the withdrawn agent's and went with
/// the withdrawal. It is keyed by the repository's identity now, and one entry serves every name the
/// checkout answers to — so removing it because one of them was withdrawn takes the machine's only way of
/// starting the others, which is the same destructive reach this file is about, one file along.
/// </para>
/// </remarks>
[Integration]
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
        _ = await WriteCatalogueAsync();

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", "acme", CataloguePath, TestContext.Current.CancellationToken);

        Assert.Equal("billing@test", await NamedAsync());
        Assert.Contains(
            outcome.Notes,
            note => note.Contains("billing@test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_catalogue_line_stays_while_the_repository_still_answers_to_somebody()
    {
        // **This asserted the opposite until 018, and the reversal is the point.** The catalogue line was
        // keyed by the agent's name, so it was unambiguously the withdrawn agent's and went. It is keyed
        // by the repository's identity now — one entry, however many names the checkout answers to — and
        // billing is still one of them here, so taking it would leave billing registered, addressable, and
        // impossible for this machine to start, with nothing saying why.
        await WriteLaunchAsync("billing@test");

        var mine = await WriteCatalogueAsync();

        await Enrolment.WithdrawAsync(
            Workspace, "orders@test", "acme", CataloguePath, TestContext.Current.CancellationToken);

        var catalogue = await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken);

        Assert.Contains(mine.ToString(), catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalogue_line_goes_once_nothing_answers_from_the_repository()
    {
        // The other half, and what keeps the one above from being "never remove anything". The last
        // parley entry left with this withdrawal, so the checkout is an agent nowhere and the machine has
        // no reason left to be able to start it.
        await WriteLaunchAsync("orders@test");

        var mine = await WriteCatalogueAsync();

        await Enrolment.WithdrawAsync(
            Workspace, "orders@test", "acme", CataloguePath, TestContext.Current.CancellationToken);

        var catalogue = await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(mine.ToString(), catalogue, StringComparison.Ordinal);

        // And somebody else's entry is still there, which is the reach this file exists to bound.
        Assert.Contains(Elsewhere.ToString(), catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_that_names_this_agent_goes()
    {
        await WriteLaunchAsync("orders@test");
        _ = await WriteCatalogueAsync();

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", "acme", CataloguePath, TestContext.Current.CancellationToken);

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

        _ = await WriteCatalogueAsync();

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", "acme", CataloguePath, TestContext.Current.CancellationToken);

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
            Workspace, "orders@test", "acme", null, TestContext.Current.CancellationToken);

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

    /// <summary>Another machine-mate's checkout, which no withdrawal here may touch.</summary>
    private static AgentIdentity Elsewhere { get; } = AgentIdentity.Mint();

    /// <summary>Catalogues this repository, and one other, the way enrolment would have.</summary>
    /// <returns>The identity this repository was catalogued under.</returns>
    /// <remarks>
    /// The identity is written into the repository as well as into the catalogue, because that is what a
    /// withdrawal reads to decide which entry is this checkout's. A fixture that wrote only the catalogue
    /// would describe a state enrolment cannot produce, and the withdrawal would rightly decline to act
    /// on it.
    /// </remarks>
    private async Task<AgentIdentity> WriteCatalogueAsync()
    {
        var mine = AgentIdentity.Mint();
        var listed = new JsonObject();

        Enrolment.List(listed, mine, Workspace);
        Enrolment.List(listed, Elsewhere, Path.Combine(_root, "somewhere-else"));

        await File.WriteAllTextAsync(
            CataloguePath,
            new JsonObject { ["agents"] = listed }.ToJsonString(),
            TestContext.Current.CancellationToken);

        var launch = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchPath, TestContext.Current.CancellationToken))!.AsObject();

        launch[LaunchConfiguration.IdentityMember] = mine.ToString();

        await File.WriteAllTextAsync(
            LaunchPath, launch.ToJsonString(), TestContext.Current.CancellationToken);

        return mine;
    }
}
