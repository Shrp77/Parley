using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Withdrawing a repository from one project and leaving the others standing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Withdrawal is the only path in this tooling that deletes something out of somebody's own
/// file</b>, and this repository has already shipped one that removed a different agent's entry and
/// reported success. Multiplying the entries multiplies that failure, so every case below asserts on
/// what was <i>left</i> rather than on what went.
/// </para>
/// <para>
/// <b>It uses the same precedence enrolling uses, run backwards.</b> A withdrawal removes what an
/// enrolment into this project would have written at and nothing else — two rules would drift, and
/// the direction they drift in leaves a repository still announcing itself as an agent of somewhere
/// it was removed from, which is worse than either state alone.
/// </para>
/// </remarks>
public sealed class WithdrawOneProjectTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-withdraw-" + Path.GetRandomFileName());

    public WithdrawOneProjectTests()
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

    private string Workspace => Path.Combine(_root, "inventory");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string CatalogueFile => Path.Combine(_root, "node.json");

    private async Task WriteAsync(string launch) =>
        await File.WriteAllTextAsync(LaunchFile, launch, TestContext.Current.CancellationToken);

    private async Task<JsonObject> ServersAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

    private const string InTwo = """
        {
          "mcpServers": {
            "parley-alpha": { "command": "parley-mcp", "env": {
              "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "alpha" } },
            "parley-beta": { "command": "parley-mcp", "env": {
              "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "beta" } },
            "some-other-server": { "command": "whatever" }
          }
        }
        """;

    [Fact]
    public async Task Withdrawing_from_one_project_leaves_the_other_answering()
    {
        await WriteAsync(InTwo);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", "beta", CatalogueFile, TestContext.Current.CancellationToken);

        var servers = await ServersAsync();

        Assert.Equal(
            ["parley-alpha", "some-other-server"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));

        Assert.Contains(LaunchFile, outcome.Written);
    }

    [Fact]
    public async Task The_run_says_how_much_is_left()
    {
        await WriteAsync(InTwo);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", "beta", CatalogueFile, TestContext.Current.CancellationToken);

        // Somebody withdrawing from one of several is entitled to see that the others survived,
        // in the run rather than by opening the file afterwards.
        Assert.Contains(
            outcome.Notes,
            note => note.Contains("1 parley", StringComparison.Ordinal)
                    && note.Contains("1 other server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_withdrawal_naming_no_project_refuses_when_there_are_several()
    {
        await WriteAsync(InTwo);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", null, CatalogueFile, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(LaunchFile, outcome.Written);
        Assert.Equal(3, (await ServersAsync()).Count);
        Assert.Contains(
            outcome.Notes,
            note => note.Contains("alpha", StringComparison.Ordinal)
                    && note.Contains("beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_withdrawal_naming_no_project_takes_the_only_entry_there_is()
    {
        // Nothing to be ambiguous about, so refusing would be pedantry. This is also every
        // deployment that existed before this feature.
        await WriteAsync("""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "inventory" } }
              }
            }
            """);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", null, CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Empty(await ServersAsync());
        Assert.Contains(LaunchFile, outcome.Written);
    }

    [Fact]
    public async Task An_entry_belonging_to_another_agent_is_left_alone_and_named()
    {
        // The guard that exists because a withdrawal once removed a different agent's entry and
        // reported success. The workspace comes from the registry, which is what an agent said about
        // itself and may be long out of date, so two rows can point at one file.
        await WriteAsync(InTwo);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "somebody-else", "beta", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Equal(3, (await ServersAsync()).Count);
        Assert.Contains(
            outcome.Notes,
            note => note.Contains("'inventory'", StringComparison.Ordinal)
                    && note.Contains("somebody-else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Withdrawing_from_a_project_it_does_not_answer_in_removes_nothing()
    {
        await WriteAsync(InTwo);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", "gamma", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Equal(3, (await ServersAsync()).Count);
        Assert.DoesNotContain(LaunchFile, outcome.Written);
        Assert.Contains(outcome.Notes, note => note.Contains("gamma", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_legacy_entry_is_withdrawn_by_naming_the_project_it_answers_in()
    {
        await WriteAsync("""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "beta" } }
              }
            }
            """);

        await Enrolment.WithdrawAsync(
            Workspace, "inventory", "alpha", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Equal(["parley-beta"], (await ServersAsync()).Select(pair => pair.Key));
    }

    [Fact]
    public async Task An_address_pinned_entry_is_left_alone_because_nothing_here_can_match_it()
    {
        // Enrolling resolves an address through the machine's manifest. This has no manifest, and
        // removing an entry that may be another project's agent is the more expensive way to be
        // wrong — the person finds out when a peer stops being answered.
        await WriteAsync("""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "inventory", "PARLEY_BROKER": "127.0.0.1:51707" } }
              }
            }
            """);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", "alpha", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Single(await ServersAsync());
        Assert.DoesNotContain(LaunchFile, outcome.Written);
        Assert.Contains(outcome.Notes, note => note.Contains("127.0.0.1:51707", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stranger_at_our_key_is_not_removed_by_a_withdrawal_either()
    {
        // It occupies a key this tooling writes at, which is why it is read at all — but it names
        // nobody and runs something else, so it is not ours to take.
        await WriteAsync("""
            { "mcpServers": { "parley-alpha": { "command": "some-other-thing" } } }
            """);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "inventory", "alpha", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.Single(await ServersAsync());
        Assert.DoesNotContain(LaunchFile, outcome.Written);
    }
}
