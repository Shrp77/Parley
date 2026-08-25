using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// Starting a process and waiting for it, which is harder than it looks.
/// </summary>
/// <remarks>
/// <para>
/// The first version of this invoker hung on every invocation, including one that printed a line
/// and exited, and it was found by running it rather than by reading it.
/// </para>
/// <para>
/// <b>These tests do not catch that bug, and saying so is the point of this comment.</b> All of
/// them pass against the broken version. The cause was the child inheriting the host's standard
/// input, which for an MCP server is the live JSON-RPC pipe its client is driving it over: the
/// inherited handle holds the pipe open and the wait never returns. Calling
/// <see cref="HeadlessInvoker.InvokeAsync"/> from a test host reproduces none of that, because
/// the failure lives entirely in what the parent's standard input happens to be.
/// </para>
/// <para>
/// So the real guard is <c>InvokedAnswerTests</c> in Parley.Mcp.Tests, which drives a whole
/// server. What these cover is the part that is genuinely this class's own: exit codes, timeouts,
/// working directories, and not deadlocking on a talkative child. That division is worth keeping
/// rather than moving everything up a level — but the division was discovered, not designed, and
/// a reader deserves to know which of these would have caught the bug that prompted them. None
/// of them would.
/// </para>
/// </remarks>
public sealed class HeadlessInvokerTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-invoke-" + Guid.NewGuid().ToString("N"));

    public HeadlessInvokerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere in this repository.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A command that runs a shell one-liner, on whichever platform this is.</summary>
    /// <param name="windows">The <c>cmd</c> body.</param>
    /// <param name="unix">The <c>sh</c> body.</param>
    /// <param name="timeout">How long to allow.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    /// The tests need a process that does something specific and exits, and the cheapest one
    /// that exists everywhere is the shell. It is invoked directly rather than through
    /// <see cref="InvocationCommand.Parse"/> where the arguments matter, because Parse appends
    /// the agent prompt and a shell would treat that as another command.
    /// </remarks>
    private static InvocationCommand Shell(string windows, string unix, TimeSpan? timeout = null) =>
        OperatingSystem.IsWindows()
            ? new InvocationCommand("cmd.exe", ["/c", windows], timeout ?? TimeSpan.FromSeconds(30))
            : new InvocationCommand("/bin/sh", ["-c", unix], timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_process_that_exits_immediately_is_waited_for_successfully()
    {
        // A process this short exits before almost anything downstream is ready for it, which
        // is the shape most likely to expose a wait that is not wired up correctly. It passed
        // even while the invoker was hanging in production, which is why it is not the guard --
        // see the class comment.
        var invoker = new HeadlessInvoker(TimeProvider.System);

        var outcome = await invoker.InvokeAsync(Shell("exit 0", "exit 0"), _workspace, Ct);

        Assert.Equal(0, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task A_failing_process_reports_its_exit_code_rather_than_throwing()
    {
        // A runner that is not installed, or an agent that crashed, is a deployment problem the
        // sweep must survive and record. Throwing would make one broken agent an error in the
        // pass that serves all of them.
        var invoker = new HeadlessInvoker(TimeProvider.System);

        var outcome = await invoker.InvokeAsync(Shell("exit 3", "exit 3"), _workspace, Ct);

        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task A_process_that_will_not_finish_is_killed_and_reported_as_timed_out()
    {
        // Reported rather than thrown, and distinguished from a non-zero exit. A timeout is not
        // the agent failing: it is a question that took longer than the invoker would hold a
        // process open for, and the exchange is unharmed -- still owed, still open, tried again
        // on the next sweep.
        var invoker = new HeadlessInvoker(TimeProvider.System);

        var command = Shell(
            "ping -n 60 127.0.0.1 > nul", "sleep 60", TimeSpan.FromSeconds(2));

        var outcome = await invoker.InvokeAsync(command, _workspace, Ct);

        Assert.True(outcome.TimedOut);
        Assert.True(
            outcome.Elapsed < TimeSpan.FromSeconds(30),
            "a timeout must not wait for the process it gave up on");
    }

    [Fact]
    public async Task A_talkative_process_does_not_deadlock_on_its_own_output()
    {
        // A child whose output buffer fills blocks on the write and never exits, so an invoker
        // that read the pipes only after waiting would hang on exactly the agents that had most
        // to say -- and it would look like the agent had wedged rather than the invoker.
        var invoker = new HeadlessInvoker(TimeProvider.System);

        var command = Shell(
            "for /l %i in (1,1,3000) do @echo the quick brown fox jumps over the lazy dog",
            "for i in $(seq 1 3000); do echo the quick brown fox jumps over the lazy dog; done",
            TimeSpan.FromSeconds(60));

        var outcome = await invoker.InvokeAsync(command, _workspace, Ct);

        Assert.False(outcome.TimedOut, "the invoker deadlocked on the child's output");
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task It_runs_in_the_agents_own_workspace()
    {
        // The registry says where an agent works, and an agent started somewhere else is an
        // agent reading the wrong repository. Proven by having the process write a file and
        // looking for it where the workspace says it should be.
        var invoker = new HeadlessInvoker(TimeProvider.System);

        var outcome = await invoker.InvokeAsync(
            Shell("echo here> marker.txt", "echo here > marker.txt"), _workspace, Ct);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(
            File.Exists(Path.Combine(_workspace, "marker.txt")),
            "the process did not run in the workspace it was given");
    }

    [Fact]
    public async Task A_missing_workspace_says_so_rather_than_failing_obscurely()
    {
        // A registry entry can name a directory that has since moved. The process-start failure
        // for that is a bare Win32 error, and the exchange stays owed either way -- so the whole
        // difference is whether an operator can tell what went wrong.
        var invoker = new HeadlessInvoker(TimeProvider.System);
        var missing = Path.Combine(_workspace, "not-here");

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => invoker.InvokeAsync(Shell("exit 0", "exit 0"), missing, Ct));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_is_appended_rather_than_interpolated()
    {
        // So no template can place the prompt where it would be read as a flag, and so a
        // template cannot omit it.
        var command = InvocationCommand.Parse("claude -p");

        Assert.Equal("claude", command.FileName);
        Assert.Equal(["-p", InvocationCommand.Prompt], command.Arguments);
    }

    [Fact]
    public void A_quoted_path_survives_being_split()
    {
        // Launch configurations name executables in Program Files.
        var command = InvocationCommand.Parse("\"C:/Program Files/x/claude.exe\" -p");

        Assert.Equal("C:/Program Files/x/claude.exe", command.FileName);
        Assert.Equal(["-p", InvocationCommand.Prompt], command.Arguments);
    }

    [Fact]
    public void The_template_is_not_a_shell()
    {
        // Split on whitespace and nothing else: no pipes, no redirection, no expansion. This
        // string comes from a launch configuration, and a template a shell interpreted would
        // turn a typo in one into arbitrary execution on the machine running the sweep.
        var command = InvocationCommand.Parse("claude -p && rm -rf /");

        Assert.Equal("claude", command.FileName);
        Assert.Contains("&&", command.Arguments);
        Assert.DoesNotContain(command.Arguments, a => a.Contains("&& rm", StringComparison.Ordinal));
    }
}
