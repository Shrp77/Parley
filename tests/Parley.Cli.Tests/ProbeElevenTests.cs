using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Whether the two writers agree.
/// </summary>
/// <remarks>
/// <para>
/// <c>Enrolment</c> exists because two writers of these files "would agree today and differ in six
/// months" — the terminal adds one repository at a time and <c>parley enroll</c> drives a whole
/// directory in bulk. The bulk path has its own hold, its own decision function and its own
/// catalogue handling, which is exactly the drift that argument warns about, so this asks
/// whether what they write is still the same thing.
/// </para>
/// <para>
/// The failure would not be a crash. It would be a repository enrolled by one surface that the other
/// cannot see, withdraw, or leave alone — and it would be found by a person, months later, with an
/// agent that answers in a roster nothing on the machine admits to.
/// </para>
/// </remarks>
[Integration]
public sealed class ProbeElevenTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe11-" + Path.GetRandomFileName());

    // Listening, because enrolment refuses a project whose broker does not answer.
    private readonly ServedProject _alpha;

    public ProbeElevenTests()
    {
        Directory.CreateDirectory(Checkouts);

        _alpha = ServedProject.Start("alpha", Path.Combine(_root, "alpha.db"), Catalogue);

        foreach (var name in new[] { "byCommand", "byTerminal" })
        {
            Directory.CreateDirectory(Workspace(name));
            Directory.CreateDirectory(Path.Combine(Workspace(name), ".git"));
        }

        ProjectManifest.Write(ManifestPath, [Project()]);
    }

    public async ValueTask DisposeAsync()
    {
        await _alpha.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Checkouts => Path.Combine(_root, "checkouts");

    // **A root, not the old manifest file.** Writing through a path ending in `projects.json`
    // creates a *directory* of that name and every read of it afterwards is refused — which is
    // why `ProjectManifest.Write` now refuses it too.
    private string ManifestPath => _root;

    private string Catalogue => Path.Combine(_root, "alpha-node.json");

    private string Workspace(string name) => Path.Combine(Checkouts, name);

    private ProjectSettings Project() => _alpha.Settings;

    /// <summary>What the terminal does: one repository, through <c>Enrolment</c>.</summary>
    private Task<Enrolment.Outcome> TerminalAsync(string name, string agent) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace(name), agent, "What it owns. Not what it does not.", Unattended: false)
            {
                Enroll = true,
            },
            "alpha",
            Catalogue,
            Path.Combine(Workspace(name), "CLAUDE.md"),
            TestContext.Current.CancellationToken,
            ManifestPath);

    /// <summary>The row either surface hands to the bulk writer, ticked and approved.</summary>
    /// <remarks>
    /// It was a manifest the command wrote and read straight back; it is the row that file held. The
    /// drift this probe is about is between two <i>writers</i>, and neither of them was ever the file.
    /// </remarks>
    private EnrollmentEntry Chosen(string name, string agent) =>
        new(
            Workspace(name).Replace(Path.DirectorySeparatorChar, '/'),
            agent,
            "What it owns. Not what it does not.",
            Unattended: false)
        {
            Enroll = true,
        };

    /// <summary>What the command does: the same repository, in bulk.</summary>
    private async Task<int> CommandAsync(string name, string agent) =>
        await EnrollCommand.EnrolAsync(
            [Chosen(name, agent)],
            describe: false,
            new Resolved(Catalogue, ValueSource.Argument),
            new StringWriter(),
            new StringWriter(),
            Project(),
            ManifestPath,
            TestContext.Current.CancellationToken);

    private async Task<JsonNode> EntryAsync(string name) =>
        JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(Workspace(name), ".mcp.json"), TestContext.Current.CancellationToken))!
            ["mcpServers"]!["parley-alpha"]!;

    [Fact]
    public async Task Both_writers_produce_the_same_entry_for_the_same_repository()
    {
        // The claim `Enrolment` was created to make, checked rather than trusted — and checked after
        // a feature that gave the bulk path its own hold, its own decision function and its own
        // catalogue handling.
        await TerminalAsync("byTerminal", "orders");

        Assert.Equal(0, await CommandAsync("byCommand", "orders"));

        // Normalised on the one thing that legitimately differs: they are two repositories, so their
        // workspaces differ and must. Comparing them raw would
        // report that as drift — a probe that has not said what it expects to differ cannot tell drift
        // from its own fixture.
        var terminal = (await EntryAsync("byTerminal")).ToJsonString()
            .Replace("byTerminal", "X", StringComparison.Ordinal);

        var command = (await EntryAsync("byCommand")).ToJsonString()
            .Replace("byCommand", "X", StringComparison.Ordinal);

        Assert.Equal(terminal, command);
    }

    [Fact]
    public async Task Both_writers_produce_the_same_file_and_not_merely_the_same_entry()
    {
        // Formatting counts. This file is usually committed, so two writers that differ in
        // whitespace produce a diff every time a machine is configured by the other surface.
        await TerminalAsync("byTerminal", "orders");

        Assert.Equal(0, await CommandAsync("byCommand", "orders"));

        var terminal = await File.ReadAllTextAsync(
            Path.Combine(Workspace("byTerminal"), ".mcp.json"), TestContext.Current.CancellationToken);

        var command = await File.ReadAllTextAsync(
            Path.Combine(Workspace("byCommand"), ".mcp.json"), TestContext.Current.CancellationToken);

        // **One field cannot be equal and must not be.** The identity is minted per repository
        // and these are two, so comparing it would be asking two agents to be one. Both must carry one,
        // which is asserted rather than assumed, and the rest of the file is compared as before —
        // including where the member sits and how it is spelled, since those are what a diff shows.
        Assert.Equal(
            Standardised(terminal).Replace("byTerminal", "X", StringComparison.Ordinal),
            Standardised(command).Replace("byCommand", "X", StringComparison.Ordinal));
    }

    /// <summary>One launch configuration with its minted identity replaced by a fixed one.</summary>
    /// <param name="launch">The file's contents.</param>
    /// <returns>The same file, comparable with another repository's.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">It carries no identity.</exception>
    private static string Standardised(string launch)
    {
        var root = JsonNode.Parse(launch)!.AsObject();

        Assert.True(
            AgentIdentity.TryRead(
                root[LaunchConfiguration.IdentityMember]?.GetValue<string>(), out _),
            "the writer left the repository with no identity, so nothing can start it");

        root[LaunchConfiguration.IdentityMember] = "00000000-0000-0000-0000-000000000000";

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public async Task What_one_writer_wrote_the_other_reports_as_already_correct()
    {
        // The drift that would matter most: each surface thinking the other's entry needs rewriting,
        // so a machine configured from a screen and then from a shell rewrites every repository.
        await TerminalAsync("byTerminal", "orders");

        var before = await File.ReadAllBytesAsync(
            Path.Combine(Workspace("byTerminal"), ".mcp.json"), TestContext.Current.CancellationToken);

        var output = new StringWriter();

        await EnrollCommand.EnrolAsync(
            [Chosen("byTerminal", "orders")],
            describe: false,
            new Resolved(Catalogue, ValueSource.Argument),
            output,
            new StringWriter(),
            Project(),
            ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.Contains("already correct", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(
            Path.Combine(Workspace("byTerminal"), ".mcp.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task What_the_command_wrote_the_terminal_can_withdraw()
    {
        // The inverse, and the one with teeth: withdrawal is the path that deletes. A surface that
        // could not find the other's entry would report success and leave the repository announcing
        // itself as an agent of a project it had been removed from.
        Assert.Equal(0, await CommandAsync("byCommand", "orders"));

        var outcome = await Enrolment.WithdrawAsync(
            Workspace("byCommand"), "orders", "alpha", Catalogue, TestContext.Current.CancellationToken);

        Assert.Contains(Path.Combine(Workspace("byCommand"), ".mcp.json"), outcome.Written);

        var servers = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(Workspace("byCommand"), ".mcp.json"), TestContext.Current.CancellationToken))!
            ["mcpServers"]!;

        Assert.Empty(servers);
    }
}
