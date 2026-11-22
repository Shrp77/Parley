using System.Diagnostics;
using System.Text.Json;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>That a repository's file made a named pipe, or a link to one, is refused at once by every reader, never waited on.</summary>
/// <remarks>
/// Found by review on Linux (lens 2). An agent can make its <c>.mcp.json</c> a pipe; an ordinary open of one waits for a
/// writer and cannot be cancelled, and the node reads every catalogued repository's file one after another on each join
/// and each start, so one agent froze its machine's node for every agent. The terminal's agents list, every command typed
/// in a repository, enrolment and the person's placement of a file waited the same way. Windows has no pipe at a path in
/// a folder, so these are skipped there. Each read runs on its own task with a deadline, so a read that waits fails the
/// test rather than hanging the run.
/// </remarks>
[Integration]
public sealed class NothingWaitsOnAPipeTests : IDisposable
{
    // Far longer than a refusal takes (milliseconds), far shorter than a wait for a writer that never comes.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("parley-pipe-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_launch_configuration_that_is_a_pipe_is_refused_at_once(bool linked)
    {
        var workspace = Workspace("orders", linked ? Linked : Piped);

        var (read, problem) = await Promptly(() =>
            (LaunchConfiguration.TryRead(workspace, out _, out var said), said));

        Assert.False(read);
        Assert.Contains("not an ordinary file", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_configuration_linked_to_a_device_is_refused()
    {
        var workspace = Workspace("orders", (folder, name) =>
        {
            SkipOnWindows();
            _ = File.CreateSymbolicLink(Path.Combine(folder, name), "/dev/null");
        });

        var (read, problem) = await Promptly(() =>
            (LaunchConfiguration.TryRead(workspace, out _, out var said), said));

        Assert.False(read);
        Assert.Contains("not an ordinary file", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_node_announces_every_other_repository_and_reports_the_pipe(bool linked)
    {
        var orders = AgentIdentity.Mint();
        var billing = AgentIdentity.Mint();
        var catalogue = NodeCatalogue.ReadFrom(Catalogue(
            (orders, Workspace("orders", Written)),
            (billing, Workspace("billing", linked ? Linked : Piped))));

        var (names, _, unread) = await Promptly(() => catalogue.Announceable(project: null));

        Assert.Equal(["orders"], names);

        var reported = Assert.Single(unread);

        Assert.Equal(billing, reported.Identity);
        Assert.Contains("not an ordinary file", reported.Problem, StringComparison.Ordinal);
        Assert.True(await Promptly(() => catalogue.TryResolve("orders", null, out _, out _)));
        Assert.Equal("orders", Assert.Single(await Promptly(() => catalogue.NamesIn(null))).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_typed_in_the_repository_does_not_wait_on_its_launch_configuration(bool linked)
    {
        var workspace = Workspace("orders", linked ? Linked : Piped);
        _ = Directory.CreateDirectory(Path.Combine(workspace, ".git"));

        Assert.Empty(await Promptly(() => RepositoryProjects.Read(workspace)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Describing_an_enrolment_does_not_wait_on_an_instructions_file(bool linked)
    {
        var workspace = Workspace("orders", Written);
        _ = Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        (linked ? (Action<string, string>)Linked : Piped)(workspace, "CLAUDE.md");
        var output = new StringWriter();
        var error = new StringWriter();

        _ = await PromptlyAsync(() => EnrollCommand.EnrolAsync(
            [new EnrollmentEntry(workspace, "orders", null, Unattended: true) { Enroll = true }],
            describe: true,
            new Resolved(Path.Combine(_root.FullName, "node.json"), ValueSource.Argument),
            output,
            error,
            cancellationToken: Ct));

        var said = output.ToString() + error;

        // A link there is declined as the write declines one, left alone with its note, and read only to describe it; a
        // pipe that is the file itself stops the run, saying what it is.
        Assert.True(
            said.Contains(linked ? ": left alone, " : "not an ordinary file", StringComparison.Ordinal),
            said);
    }

    [Fact]
    public async Task Holding_a_launch_configuration_to_rewrite_it_refuses_a_pipe()
    {
        var workspace = Workspace("orders", Piped);

        var refused = await PromptlyAsync<IOException?>(async () =>
        {
            try
            {
                var held = await Enrolment.HoldAsync(Path.Combine(workspace, LaunchConfiguration.FileName), Ct);

                held?.Dispose();

                return null;
            }
            catch (IOException failed)
            {
                return failed;
            }
        });

        Assert.NotNull(refused);
        Assert.Contains("not an ordinary file", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pipe_the_person_names_is_not_placed(bool linked)
    {
        var folder = _root.CreateSubdirectory("place").FullName;
        (linked ? (Action<string, string>)Linked : Piped)(folder, "notes.md");
        var path = Path.Combine(folder, "notes.md");

        var problem = await Promptly(() => PlaceableFile.Problem(path));

        Assert.Contains(linked ? "is a link" : "not an ordinary file", problem, StringComparison.Ordinal);

        // Read even so, as a path changed after the check would be: the open itself refuses it.
        var refused = await Promptly<IOException?>(() =>
        {
            try
            {
                PlaceableFile.OpenRead(path).Dispose();

                return null;
            }
            catch (IOException failed)
            {
                return failed;
            }
        });

        Assert.NotNull(refused);
        Assert.Contains(linked ? "is a link" : "not an ordinary file", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_file_is_still_placed_and_read()
    {
        SkipOnWindows();

        var workspace = Workspace("orders", Written);
        var path = Path.Combine(workspace, LaunchConfiguration.FileName);

        Assert.Null(PlaceableFile.Problem(path));

        using (var stream = PlaceableFile.OpenRead(path))
        {
            Assert.True(stream.Length > 0);
        }

        Assert.True(LaunchConfiguration.TryRead(workspace, out var configuration, out var problem), problem);
        Assert.Equal("orders", Assert.Single(configuration.Memberships).Name);
    }

    private static Task<T> Promptly<T>(Func<T> read) => Within(Task.Run(read, Ct));

    private static Task<T> PromptlyAsync<T>(Func<Task<T>> read) => Within(Task.Run(read, Ct));

    private static async Task<T> Within<T>(Task<T> read)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            return await read.WaitAsync(Deadline, Ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"the read waited on the pipe for {watch.Elapsed} and is still waiting");

            throw;
        }
    }

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no named pipe at a path in a folder.");

    private static void Written(string folder, string name) =>
        File.WriteAllText(
            Path.Combine(folder, name),
            """{"mcpServers":{"parley":{"command":"parley-mcp","env":{"PARLEY_AGENT":"orders"}}}}""");

    private static void Piped(string folder, string name)
    {
        SkipOnWindows();

        var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(folder, name));

        using var made = Process.Start(start)!;
        made.WaitForExit();

        Assert.Equal(0, made.ExitCode);
    }

    // The pipe beside the folder, and the file a link to it, as an agent would leave it.
    private static void Linked(string folder, string name)
    {
        var elsewhere = Path.Combine(Path.GetDirectoryName(folder)!, "elsewhere-" + Path.GetFileName(folder));
        _ = Directory.CreateDirectory(elsewhere);
        Piped(elsewhere, name);

        _ = File.CreateSymbolicLink(Path.Combine(folder, name), Path.Combine(elsewhere, name));
    }

    private string Workspace(string name, Action<string, string> launchFile)
    {
        var workspace = _root.CreateSubdirectory(name).FullName;

        launchFile(workspace, LaunchConfiguration.FileName);

        return workspace;
    }

    private string Catalogue(params (AgentIdentity Identity, string Workspace)[] agents)
    {
        var path = Path.Combine(_root.FullName, "node.json");
        var entries = agents.Select(agent =>
            $"\"{agent.Identity}\": {{ \"command\": \"claude\", \"workspace\": {JsonSerializer.Serialize(agent.Workspace)} }}");

        File.WriteAllText(path, "{ \"agents\": { " + string.Join(", ", entries) + " } }");

        return path;
    }
}
