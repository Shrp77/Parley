using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Which agent an entry is for, when a repository is several.
/// </summary>
/// <remarks>
/// <para>
/// Identity is the single thing this protocol makes unforgeable — it comes from the launch environment
/// and from nowhere else, so the file this feature writes <i>is</i> the identity. Multiplying the
/// entries multiplies the ways one can be attributed to the wrong agent, and the two that matter are
/// a withdrawal taking the wrong one and a re-enrolment renaming one without saying so.
/// </para>
/// <para>
/// The hard case is deliberate and legitimate: two projects may use the <i>same</i> name for one
/// repository, because names collide freely across projects. So "the names match" cannot be what
/// decides which entry a withdrawal takes.
/// </para>
/// </remarks>
public sealed class ProbeSeventeenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe17-" + Path.GetRandomFileName());

    public ProbeSeventeenTests()
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

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private Task<Enrolment.Outcome> EnrolAsync(string project, string agent) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, agent, "Carts and totals.", Unattended: false)
            {
                Enroll = true,
            },
            project,
            Path.Combine(_root, $"{project}-node.json"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    private async Task<JsonObject> ServersAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

    [Fact]
    public async Task Two_projects_may_use_one_name_for_one_repository()
    {
        // Legitimate and load-bearing: SPEC.md §4.1 says names collide freely across projects,
        // because they are different files. A refusal here would be this tooling inventing a global
        // naming rule the protocol spent its design avoiding.
        await EnrolAsync("alpha", "orders");
        await EnrolAsync("beta", "orders");

        var servers = await ServersAsync();

        Assert.Equal(
            ["parley-alpha", "parley-beta"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal("orders", servers["parley-alpha"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("orders", servers["parley-beta"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
    }

    [Fact]
    public async Task Withdrawing_when_both_names_match_still_takes_only_the_named_project()
    {
        // **The case the name-matching guard cannot decide.** That guard exists because a withdrawal
        // once removed a different agent's entry and reported success — and here both entries name
        // the same agent, so it agrees with either. Only the project can tell them apart.
        await EnrolAsync("alpha", "orders");
        await EnrolAsync("beta", "orders");

        await Enrolment.WithdrawAsync(
            Workspace, "orders", "beta", Path.Combine(_root, "beta-node.json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["parley-alpha"], (await ServersAsync()).Select(pair => pair.Key));
    }

    [Fact]
    public async Task Re_enrolling_under_a_different_name_says_the_name_changed()
    {
        // An agent's name *is* its identity, and re-enrolling a repository under a different one in
        // the same project retires one identity and creates another — from the registry's point of
        // view, an agent stops existing. A run that did that without saying so would leave a person
        // wondering why the old name still appears in a roster nothing will answer for.
        await EnrolAsync("alpha", "orders");

        var outcome = await EnrolAsync("alpha", "sales");

        var servers = await ServersAsync();

        Assert.Equal("sales", servers["parley-alpha"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());

        Assert.Contains(
            outcome.Notes,
            note => note.Contains("orders", StringComparison.Ordinal)
                    && note.Contains("sales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_name_written_is_the_one_asked_for_and_never_the_one_already_in_the_file()
    {
        // The merge reads the existing entry to decide where to write. If any of it leaked into what
        // gets written, a repository could keep an identity somebody had just changed — and identity
        // is the one thing this protocol refuses to let anything but the launch decide.
        await File.WriteAllTextAsync(
            LaunchFile,
            """
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "somebody-else", "PARLEY_PROJECT": "alpha",
                "PARLEY_OWNS": "Something else entirely." } } } }
            """,
            TestContext.Current.CancellationToken);

        await EnrolAsync("alpha", "orders");

        var env = (await ServersAsync())["parley-alpha"]!["env"]!;

        Assert.Equal("orders", env["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("Carts and totals.", env["PARLEY_OWNS"]!.GetValue<string>());
    }
}
