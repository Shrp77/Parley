using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 13: the delete path, and what else lives in that file.
/// </summary>
/// <remarks>
/// <para>
/// Withdrawal is the only path in this tooling that removes something out of somebody's own file, and
/// it is the reason the key carries a prefix at all. Earlier runs asked which entry it picks and what
/// it says while picking. This asks what it can be made to destroy.
/// </para>
/// <para>
/// Two things share the file. Other projects' entries, which is the whole point of the feature — and
/// <b>other people's MCP servers</b>, which have nothing to do with Parley and which every write here
/// rewrites the file around. A launch configuration is a repository's own, shared with whatever else
/// the person has configured, and this program is a guest in it.
/// </para>
/// </remarks>
public sealed class ProbeTwentyFourTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe24-" + Path.GetRandomFileName());

    public ProbeTwentyFourTests()
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

    private Task<string> WriteAsync(JsonNode root) =>
        File.WriteAllTextAsync(LaunchFile, root.ToJsonString(), TestContext.Current.CancellationToken)
            .ContinueWith(_ => LaunchFile, TestContext.Current.CancellationToken,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task<JsonObject> AfterAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

    private static JsonObject Ours(string agent, string? project) =>
        new()
        {
            ["command"] = EnrollmentPlan.AdapterCommand,
            ["env"] = project is null
                ? new JsonObject { ["PARLEY_AGENT"] = agent }
                : new JsonObject { ["PARLEY_AGENT"] = agent, ["PARLEY_PROJECT"] = project },
        };

    /// <summary>Somebody else's MCP server, which has nothing to do with any of this.</summary>
    private static JsonObject Stranger() =>
        new()
        {
            ["command"] = "npx",
            ["args"] = new JsonArray("-y", "@modelcontextprotocol/server-github"),
            ["env"] = new JsonObject { ["GITHUB_TOKEN"] = "env:GITHUB_TOKEN" },
        };

    private Task<Enrolment.Outcome> WithdrawAsync(string? project) =>
        Enrolment.WithdrawAsync(
            Workspace, "orders", project, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_withdrawal_leaves_somebody_elses_server_exactly_as_it_was()
    {
        // A launch configuration is shared with whatever else the person has configured. This
        // program rewrites the whole file to remove one entry from it, so every other server in it
        // is rewritten too — and one of them holds a credential reference.
        var stranger = Stranger();
        var before = stranger.ToJsonString();

        await WriteAsync(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["parley-alpha"] = Ours("orders", "alpha"),
                ["github"] = stranger,
            },
        });

        await WithdrawAsync("alpha");

        var servers = (JsonObject)(await AfterAsync())["mcpServers"]!;

        Assert.False(servers.ContainsKey("parley-alpha"));
        Assert.Equal(before, servers["github"]!.ToJsonString());
    }

    [Fact]
    public async Task A_withdrawal_leaves_the_rest_of_the_file_alone()
    {
        // `mcpServers` is not the only thing in a launch configuration. A rewrite that rebuilt the
        // document rather than editing it would drop everything it did not know about — silently,
        // in a file the person did not ask this to touch beyond one entry.
        await WriteAsync(new JsonObject
        {
            ["inputs"] = new JsonArray(new JsonObject { ["id"] = "token", ["type"] = "promptString" }),
            ["mcpServers"] = new JsonObject { ["parley-alpha"] = Ours("orders", "alpha") },
        });

        await WithdrawAsync("alpha");

        Assert.True((await AfterAsync()).ContainsKey("inputs"));
    }

    [Fact]
    public async Task An_entry_whose_key_and_environment_disagree_does_not_take_another_project_with_it()
    {
        // **The attack on the delete path.** The key says one project and the environment says
        // another, which no honest run of this writes — so whichever of the two decides, the entry
        // that plainly belongs to the other project must survive.
        await WriteAsync(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["parley-alpha"] = Ours("orders", "beta"),
                ["parley-beta"] = Ours("orders", "beta"),
            },
        });

        var outcome = await WithdrawAsync("alpha");

        var servers = (JsonObject)(await AfterAsync())["mcpServers"]!;

        Assert.True(servers.ContainsKey("parley-beta"), "withdrawing from alpha removed beta's entry");

        // **And it was decided rather than skipped.** The safety assertion above is satisfied by a
        // run that did nothing at all, which is the control this suite would otherwise be missing:
        // either the alpha-keyed entry went, or a note says why it stayed.
        Assert.True(
            !servers.ContainsKey("parley-alpha") || outcome.Notes.Count > 0,
            "the mismatch was neither acted on nor explained: " + string.Join(" | ", outcome.Notes));
    }

    [Fact]
    public async Task A_stranger_squatting_our_key_is_not_deleted()
    {
        // An entry at a key this program owns that this program did not write. Deleting it would be
        // this tooling removing a server somebody configured, on the strength of its name.
        var squatter = Stranger();
        var before = squatter.ToJsonString();

        await WriteAsync(new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["parley-alpha"] = squatter },
        });

        var outcome = await WithdrawAsync("alpha");

        var servers = (JsonObject)(await AfterAsync())["mcpServers"]!;

        Assert.Equal(before, servers["parley-alpha"]!.ToJsonString());
        Assert.NotEmpty(outcome.Notes);
    }

    [Fact]
    public async Task Withdrawing_from_one_project_leaves_every_other_projects_entry()
    {
        await WriteAsync(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["parley"] = Ours("orders", null),
                ["parley-alpha"] = Ours("orders", "alpha"),
                ["parley-beta"] = Ours("orders", "beta"),
                ["parley-gamma"] = Ours("orders", "gamma"),
            },
        });

        await WithdrawAsync("beta");

        var servers = (JsonObject)(await AfterAsync())["mcpServers"]!;

        Assert.Equal(
            ["parley", "parley-alpha", "parley-gamma"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
    }
}
