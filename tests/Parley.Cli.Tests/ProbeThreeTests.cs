using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The guard that reads one file and writes another, and what a bulk
/// run leaves behind when it stops in the middle.
/// </summary>
/// <remarks>
/// <para>
/// Other probes attack single writes to single files. This one attacks the place where a decision
/// is made from one file and acted on in a different one: <c>enroll apply</c> decides per repository
/// while writing as it goes.
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
        };

    // **There is no race between two projects over one workspace's grant.** One catalogue keyed by the
    // agent's identity means one checkout has one entry, so there is no second grant to win. The
    // machine-wide gate in `Catalogues` is still raced below, because two enrolments still share one file.

    [Fact]
    public async Task A_bulk_run_that_refuses_one_repository_has_not_written_the_ones_before_it()
    {
        // Enrolling refuses a half-enrolled set by design — *"the repository that missed out is
        // indistinguishable from one you never meant to enroll"* — and validates every row before
        // writing any. A refusal that arrives during the *writing* phase is a different thing, and this
        // asks whether it undoes that promise.
        //
        // **The specimen refusal** is the copied repository — one identity offered at a second
        // path while the first is still there — which is the one refusal left that cannot be decided
        // before the catalogue is read.
        var first = Workspace("orders");
        var second = Workspace("inventory");
        var original = Workspace("inventory-original");
        var identity = AgentIdentity.Mint();

        foreach (var checkout in (string[])[second, original])
        {
            await File.WriteAllTextAsync(
                Path.Combine(checkout, LaunchConfiguration.FileName),
                $$"""
                {
                  "mcpServers": {
                    "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "stock" } }
                  },
                  "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
                }
                """,
                TestContext.Current.CancellationToken);
        }

        // This machine already starts that agent from the original checkout, which is still there.
        await File.WriteAllTextAsync(
            CatalogueFor("beta"),
            $$"""
              { "agents": { "{{identity}}": { "command": "claude", "workspace": "{{original.Replace('\\', '/')}}" } } }
              """,
            TestContext.Current.CancellationToken);

        // The rows in the order a surface offers them, which is what makes "the ones before it"
        // mean anything: `orders` is written first and the copied checkout is what refuses.
        var chosen = new[]
        {
            new EnrollmentEntry(first, "orders", "Carts.", Unattended: true)
            {
                Enroll = true,
            },
            new EnrollmentEntry(second, "stock", "Stock.", Unattended: true)
            {
                Enroll = true,
            },
        };

        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.EnrolAsync(
            chosen,
            describe: false,
            new Resolved(CatalogueFor("beta"), ValueSource.Argument),
            output,
            error,
            Project("beta"),
            cancellationToken: TestContext.Current.CancellationToken);

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
