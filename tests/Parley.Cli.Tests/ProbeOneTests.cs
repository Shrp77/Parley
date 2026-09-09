using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 1: what happens when two things happen at once, and what a file with
/// a shape nobody writes by hand does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Probes, not readings.</b> The highest-value findings in this repository's history came from
/// writing the malicious or concurrent case and watching it be admitted — a self-referral asserting
/// both halves of a contradiction, a gate cycle no sub-question could resolve, an agent writing
/// <c>ghosted</c> over its own answered exchanges. None were visible by inspection.
/// </para>
/// <para>
/// This feature multiplies the entries in one file and every writer of it does read-modify-write on
/// the whole file. That is the shape worth attacking first.
/// </para>
/// </remarks>
public sealed class ProbeOneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe1-" + Path.GetRandomFileName());

    public ProbeOneTests()
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
        }
    }

    private string Workspace => Path.Combine(_root, "inventory").Replace('\\', '/');

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string CatalogueFor(string project) => Path.Combine(_root, $"{project}-node.json");

    private static EnrollmentEntry Entry(string workspace, string agent) =>
        new(workspace, agent, "What is on hand.", Unattended: false) { Enroll = true, OwnsApproved = true };

    [Fact]
    public async Task Two_enrolments_of_one_repository_at_once_do_not_lose_an_entry()
    {
        // **The probe.** Every writer here reads the whole launch configuration, decides, and writes
        // the whole thing back. Two of those interleaved is the classic lost update, and this feature
        // is the first thing that gives one repository two writers with a reason to run together —
        // two projects being set up on one machine.
        var alpha = Enrolment.AddAsync(
            Entry(Workspace, "inventory"),
            "alpha",
            CatalogueFor("alpha"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        var beta = Enrolment.AddAsync(
            Entry(Workspace, "stock"),
            "beta",
            CatalogueFor("beta"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(alpha, beta);
        }
        catch (IOException)
        {
            // A refusal is an acceptable outcome: the loss is what matters, not the success.
            return;
        }

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Equal(
            ["parley-alpha", "parley-beta"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Repeated_at_pressure_the_same_way()
    {
        // Once is an anecdote. Eight projects racing over one file is the case a person setting up a
        // machine actually produces, and it is where a lost update stops being theoretical.
        var projects = Enumerable.Range(0, 8).Select(i => $"p{i}").ToArray();

        var writing = projects.Select(project => Task.Run(async () =>
        {
            try
            {
                await Enrolment.AddAsync(
                    Entry(Workspace, project),
                    project,
                    CatalogueFor(project),
                    Path.Combine(Workspace, "CLAUDE.md"),
                    TestContext.Current.CancellationToken);

                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return false;
            }
        })).ToArray();

        var accepted = (await Task.WhenAll(writing)).Count(ok => ok);

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Equal(accepted, servers.Count);
    }

    [Fact]
    public void A_launch_configuration_with_two_entries_under_one_key_is_not_silently_halved()
    {
        // JSON allows a repeated member and nothing in this codebase writes one. What matters is
        // which way the reader fails: throwing is refused-and-reported, last-wins is an entry that
        // exists in the file, is invisible to every reader, and survives a withdrawal that reports
        // success.
        const string Repeated = """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "first" } },
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "second" } }
              }
            }
            """;

        // **Found here: it crashed.** `JsonNode.Parse` accepts the file and the throw arrives later,
        // out of the enumerator, as an ArgumentException nothing caught -- so a scan crashed, a
        // withdrawal crashed, and `enroll apply` crashed part-way through a run, leaving exactly the
        // half-applied set that command refuses by design.
        var servers = JsonNode.Parse(Repeated)?["mcpServers"] as JsonObject;

        var refusal = Assert.Throws<InvalidDataException>(() => LaunchEntries.Read(servers));

        Assert.Contains("twice", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_member_is_reported_by_the_paths_a_person_actually_runs()
    {
        // The reader refusing is only half the fix: what matters is that each caller turns the
        // refusal into a sentence rather than a stack trace. Enrolling, withdrawing and scanning all
        // read this file and all three used to crash on it.
        await File.WriteAllTextAsync(
            LaunchFile,
            """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "first" } },
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "second" } }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);

        var withdrawal = await Enrolment.WithdrawAsync(
            Workspace, "first", "alpha", CatalogueFor("alpha"), TestContext.Current.CancellationToken);

        Assert.Contains(withdrawal.Notes, note => note.Contains("left alone", StringComparison.Ordinal));
        Assert.Empty(withdrawal.Written);

        await Assert.ThrowsAsync<InvalidDataException>(() => Enrolment.AddAsync(
            Entry(Workspace, "inventory"),
            "alpha",
            CatalogueFor("alpha"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken));

        // Nothing touched it, which is the whole of what a file this cannot read is owed.
        Assert.Equal(before, await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_key_differing_only_in_case_is_a_different_server_and_is_left_alone()
    {
        // MCP clients key servers by an exact string. If the reader were case-insensitive anywhere,
        // `Parley-Alpha` would be claimed as ours and overwritten; if the writer were, two entries
        // could collide. Both directions are checked because they fail differently.
        var servers = (JsonObject)JsonNode.Parse("""
            { "mcpServers": { "Parley-Alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders" } } } }
            """)!["mcpServers"]!;

        Assert.Empty(LaunchEntries.Read(servers));

        var (key, landed) = LaunchEntries.Landing(servers, "alpha");

        Assert.Equal("parley-alpha", key);
        Assert.Null(landed);
    }

    [Fact]
    public async Task Withdrawing_while_another_project_is_being_added_leaves_a_coherent_file()
    {
        // The other pairing: one writer adding and one removing, over the same file. Whichever wins,
        // the file must still parse and must still name every server that was not the subject.
        await Enrolment.AddAsync(
            Entry(Workspace, "inventory"),
            "alpha",
            CatalogueFor("alpha"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        var adding = Enrolment.AddAsync(
            Entry(Workspace, "stock"),
            "beta",
            CatalogueFor("beta"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        var removing = Enrolment.WithdrawAsync(
            Workspace,
            "inventory",
            "alpha",
            CatalogueFor("alpha"),
            TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(adding, removing);
        }
        catch (IOException)
        {
            // Acceptable. What is not is a file left unreadable.
        }

        var text = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);

        Assert.NotNull(JsonNode.Parse(text));
    }

    [Fact]
    public void The_workspace_comparison_used_by_the_catalogue_rule_is_the_specified_one()
    {
        // FR-013 rests entirely on this. If it ever stopped agreeing with SPEC.md §4.1.1, the rule
        // would pass on the machine it was written for and fail on somebody else's spelling.
        Assert.True(WorkspacePath.SameRepository("C:/src/x", "C:\\src\\x"));
        Assert.True(WorkspacePath.SameRepository("C:/src/x/", "C:/src/x"));
        Assert.False(WorkspacePath.SameRepository("C:/src/x", "C:/src/xy"));
    }
}
