using Parley.Core;
using Parley.Setup;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 3: the guard that reads one file and writes another, and what a bulk
/// run leaves behind when it stops in the middle.
/// </summary>
/// <remarks>
/// <para>
/// Runs 1 and 2 attacked single writes to single files and found four defects. This one attacks the
/// two places where a decision is made from one file and acted on in a different one: FR-013 reads
/// every <i>other</i> project's catalogue and then writes this project's, and <c>enroll apply</c>
/// decides per repository while writing as it goes.
/// </para>
/// <para>
/// Both are shapes where holding a file cannot help, because the thing being checked is not the
/// thing being written.
/// </para>
/// </remarks>
public sealed class ProbeThreeTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe3-" + Path.GetRandomFileName());

    // **Named per project and started on demand**, because this probe enrols into several and each
    // has to have a broker answering: enrolment refuses a project whose does not. Its endpoint used
    // to read `127.0.0.1:51707` with no scheme at all, which nothing could have connected to.
    private readonly Dictionary<string, ServedProject> _served = new(StringComparer.Ordinal);

    public ProbeThreeTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var served in _served.Values)
        {
            await served.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name).Replace('\\', '/');

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private string CatalogueFor(string project) => Path.Combine(_root, $"{project}-node.json");

    private ProjectSettings Project(string name)
    {
        if (!_served.TryGetValue(name, out var served))
        {
            served = ServedProject.Start(name, Path.Combine(_root, $"{name}.db"), CatalogueFor(name));
            _served[name] = served;
        }

        return served.Settings;
    }

    private static EnrollmentEntry Entry(string workspace, string agent, bool unattended) =>
        new(workspace, agent, "What it owns. Not what it does not.", unattended)
        {
            Enroll = true,
            OwnsApproved = true,
        };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Two_projects_claiming_one_workspace_unattended_at_once_do_not_both_win(int round)
    {
        // Repeated, because one pass of a race is luck rather than evidence. The round number only
        // separates the fixtures.
        _ = round;

        // **The check reads other projects' catalogues and writes its own.** Holding a file cannot
        // close that: the thing checked is not the thing written. Two projects enrolling one
        // repository unattended at the same moment can each look and see nothing.
        //
        // The rule exists because a node starts an agent with the repository as its working
        // directory, so two consents is two models editing one checkout with nobody watching.
        var workspace = Workspace("inventory");
        var deployment = new[] { Project("alpha"), Project("beta") };

        // The gate is the manifest itself, so the probe has to have one -- which is also the honest
        // shape: a machine with two projects has a manifest describing them.
        var manifest = Path.Combine(_root, "projects.json");

        await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken);

        var both = new[] { "alpha", "beta" }.Select(project => Task.Run(async () =>
        {
            try
            {
                await Enrolment.AddAsync(
                    Entry(workspace, project, unattended: true),
                    project,
                    CatalogueFor(project),
                    Path.Combine(workspace, "CLAUDE.md"),
                    TestContext.Current.CancellationToken,
                    deployment,
                    manifest);

                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return false;
            }
        })).ToArray();

        await Task.WhenAll(both);

        var consenting = new[] { "alpha", "beta" }
            .Where(project => File.Exists(CatalogueFor(project)))
            .Count(project =>
                (JsonNode.Parse(File.ReadAllText(CatalogueFor(project)))?["agents"] as JsonObject)
                    is { Count: > 0 });

        Assert.True(
            consenting <= 1,
            $"{consenting} projects consented to start one workspace unattended");
    }

    [Fact]
    public async Task A_bulk_run_that_refuses_one_repository_has_not_written_the_ones_before_it()
    {
        // `enroll apply` refuses a half-enrolled set by design — *"the repository that missed out is
        // indistinguishable from one you never meant to enroll"* — and validates every row before
        // writing any. FR-013 is a refusal that arrives during the writing phase, which is a
        // different thing, and this asks whether it undoes that promise.
        var first = Workspace("orders");
        var second = Workspace("inventory");

        // `alpha` already starts `second` unattended, so enrolling it into `beta` must be refused.
        await File.WriteAllTextAsync(
            CatalogueFor("alpha"),
            $$"""
              { "agents": { "inventory": { "command": "claude", "workspace": "{{second}}" } } }
              """,
            TestContext.Current.CancellationToken);

        var manifest = Path.Combine(_root, "enrol.json");

        await File.WriteAllTextAsync(
            manifest,
            JsonSerializer.Serialize(new
            {
                node_config = CatalogueFor("beta"),
                agents = new[]
                {
                    new { workspace = first, agent = "orders", owns = "Carts.", unattended = true, enroll = true, owns_approved = true },
                    new { workspace = second, agent = "stock", owns = "Stock.", unattended = true, enroll = true, owns_approved = true },
                },
            }),
            TestContext.Current.CancellationToken);

        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.RunAsync(
            ["apply", manifest],
            selectAll: null,
            new Resolved(CatalogueFor("beta"), ValueSource.Argument),
            output,
            error,
            TestContext.Current.CancellationToken,
            Project("beta"),
            [Project("alpha"), Project("beta")]);

        Assert.NotEqual(0, code);

        // The first repository must not have been written. If it was, the run has produced exactly
        // the state the command refuses everywhere else: one enrolled, one not, and nothing to say
        // which was meant.
        Assert.False(
            File.Exists(Path.Combine(first, ".mcp.json")),
            "the repository before the refusal was written anyway, which is the half-applied set "
            + "`enroll apply` refuses by design");
    }

    [Fact]
    public async Task A_waiting_writer_gets_the_file_rather_than_an_error()
    {
        // The hold waits on purpose: a collision between two ordinary runs is not contention worth
        // reporting. If the wait did not actually work, every concurrent pair would surface as a
        // failure and the fix would have traded a silent loss for a noisy one.
        var workspace = Workspace("orders");
        var launch = Path.Combine(workspace, ".mcp.json");

        await File.WriteAllTextAsync(launch, """{ "mcpServers": {} }""", TestContext.Current.CancellationToken);

        using var blocker = new FileStream(
            launch, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var enrolling = Enrolment.AddAsync(
            Entry(workspace, "orders", unattended: false),
            "acme",
            CatalogueFor("acme"),
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        blocker.Dispose();

        await enrolling;

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(launch, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.NotNull(servers["parley-acme"]);
    }

    [Fact]
    public async Task A_launch_path_that_is_a_directory_is_reported_rather_than_thrown_raw()
    {
        // Somebody's checkout, and directories named `.mcp.json` do happen — a bad archive, a
        // careless `mkdir`. What matters is that the failure names the file.
        var workspace = Workspace("orders");

        Directory.CreateDirectory(Path.Combine(workspace, ".mcp.json"));

        var failure = await Record.ExceptionAsync(() => Enrolment.AddAsync(
            Entry(workspace, "orders", unattended: false),
            "acme",
            CatalogueFor("acme"),
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken));

        Assert.NotNull(failure);
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"a directory in the way threw {failure.GetType().Name}, which no caller handles");
    }

    [Fact]
    public async Task A_withdrawal_from_a_project_with_no_catalogue_still_takes_the_launch_entry()
    {
        // The catalogue hold is acquired first and is null when the project names no catalogue. A
        // guard that assumed it was there would skip the launch half entirely, leaving a repository
        // announcing itself as an agent of somewhere it was removed from.
        var workspace = Workspace("orders");

        await Enrolment.AddAsync(
            Entry(workspace, "orders", unattended: false),
            "acme",
            CatalogueFor("acme"),
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            workspace, "orders", "acme", null, TestContext.Current.CancellationToken);

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(workspace, ".mcp.json"), TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Empty(servers);
        Assert.Contains(outcome.Notes, note => note.Contains("this project has none", StringComparison.Ordinal));
    }
}
