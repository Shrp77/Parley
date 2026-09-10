using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The two claims in <c>data-model.md</c> that nothing else checks.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 10. Most of that document is checked already — the four-case precedence by
/// <c>LaunchPrecedenceTests</c>, injectivity and the <c>parley-parley</c> case by
/// <c>LaunchKeyTests</c>, what is never written by run 15's probe. Two are not.
/// </para>
/// <para>
/// <b>The bound.</b> The document says a key is "safe unescaped in JSON and in a client's tool names:
/// at most 39 characters". That number is not decoration — the key is a component of every tool name
/// the agent behind it sees, so it is <c>mcp__parley-&lt;project&gt;__parley_ask</c> that has to stay
/// inside whatever a client allows, and the arithmetic is `parley-` plus the longest a project name
/// may be.
/// </para>
/// <para>
/// <b>The invariant.</b> "At most one entry per project per repository. Enforced by the precedence,
/// not by a uniqueness check after the fact — there is no moment at which two entries for one project
/// exist." That is the feature's central promise, and *enforced by construction* is exactly the kind
/// of claim that is true until a fifth case is added to the four.
/// </para>
/// </remarks>
public sealed class OneEntryPerProjectTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-one-entry-" + Path.GetRandomFileName());

    public OneEntryPerProjectTests()
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

    private Task<Enrolment.Outcome> EnrolAsync(string project) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: false)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            project,
            Path.Combine(_root, "node.json"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    private async Task<JsonObject> ServersAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

    /// <summary>How many entries in the file answer for <paramref name="project"/>.</summary>
    /// <remarks>
    /// Counted by what each entry *says* rather than by its key, because the legacy entry answers for
    /// a project without its key naming one — and an invariant checked against the derived key alone
    /// would be blind to exactly the case the precedence exists for.
    /// </remarks>
    private static int AnsweringFor(JsonObject servers, string project) =>
        servers.Count(pair =>
            LaunchKey.IsOurs(pair.Key)
            && string.Equals(
                LaunchKey.ProjectOf(pair.Key)
                ?? pair.Value?["env"]?[EnrollmentPlan.ProjectVariable]?.GetValue<string>(),
                project,
                StringComparison.Ordinal));

    [Fact]
    public void The_longest_key_is_the_length_the_document_promises()
    {
        var longest = "p" + new string('a', ProjectNameLimit - 1);

        Assert.Equal(39, LaunchKey.For(longest).Length);
    }

    [Fact]
    public void The_longest_key_is_still_safe_unescaped()
    {
        var key = LaunchKey.For("p" + new string('a', ProjectNameLimit - 1));

        Assert.Equal(key, JsonValue.Create(key)!.ToJsonString().Trim('"'));
        Assert.All(key, character =>
            Assert.True(
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-',
                $"a key carried '{character}', which a tool name may not"));
    }

    [Fact]
    public async Task Enrolling_the_same_project_twice_leaves_one_entry()
    {
        await EnrolAsync("alpha");
        await EnrolAsync("alpha");

        Assert.Equal(1, AnsweringFor(await ServersAsync(), "alpha"));
    }

    [Fact]
    public async Task Re_pointing_a_legacy_entry_does_not_leave_two_answering_for_one_project()
    {
        // **The case the invariant is actually about.** A repository enrolled before projects existed
        // holds a `parley` entry naming no project; enrolling it into one re-points that entry in
        // place. A fifth precedence case that wrote the derived key instead would leave the legacy
        // entry answering for the same project — two entries, one project, and the invariant gone.
        await File.WriteAllTextAsync(
            LaunchFile,
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [LaunchKey.Unnamed] = new JsonObject
                    {
                        ["command"] = EnrollmentPlan.AdapterCommand,
                        ["env"] = new JsonObject { [EnrollmentPlan.AgentVariable] = "orders" },
                    },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        await EnrolAsync("alpha");

        var servers = await ServersAsync();

        Assert.Equal(1, AnsweringFor(servers, "alpha"));
        Assert.True(servers.ContainsKey(LaunchKey.Unnamed), "the legacy entry was renamed after all");
    }

    [Fact]
    public async Task Three_projects_are_three_entries_and_one_each()
    {
        // The control. An invariant satisfied by never writing a second entry at all would pass
        // everything above.
        foreach (var project in (string[])["alpha", "beta", "gamma"])
        {
            await EnrolAsync(project);
        }

        var servers = await ServersAsync();

        Assert.Equal(3, servers.Count);

        foreach (var project in (string[])["alpha", "beta", "gamma"])
        {
            Assert.Equal(1, AnsweringFor(servers, project));
        }
    }

    /// <summary>The longest a project name may be, asked of the grammar rather than written here.</summary>
    private static int ProjectNameLimit => Parley.Core.ProjectName.MaxLength;
}
