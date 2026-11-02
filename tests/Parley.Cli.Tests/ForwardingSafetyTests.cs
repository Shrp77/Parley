using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the forwarding wrapper can and cannot be made to run.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper that starts another program is a new way into this machine, and it did not exist
/// before this change. Two questions follow from that and neither is answerable by reading:
/// whether anything about the arguments can reach a shell, and whether the tool it starts is
/// always the one that was installed.
/// </para>
/// <para>
/// <b>The second was probed outside the suite and held.</b> `parley-terminal` is started by bare
/// name, and Windows' documented search order for <c>CreateProcess</c> includes the calling
/// process's current directory — so an operator running `parley terminal` inside a cloned repository
/// containing a `parley-terminal.exe` would be the hijack. It was tried: a working stand-in was planted in the
/// working directory, proved to run when invoked directly, and the tool on PATH answered anyway,
/// with <c>NoDefaultCurrentDirectoryInExePath</c> removed from the calling process as well as the
/// child. .NET's <c>Process.Start</c> resolves through PATH. Recorded rather than defended against,
/// because a defence added on a reasoned risk that the probe says is not there is a code path
/// nothing exercises.
/// </para>
/// <para>
/// Whether an awkward argument survives the hand-off is checked in <c>Parley.Terminal.Tests</c>, against
/// the real terminal. A first draft checked it here against <c>cmd</c>, and could not: the shell's
/// own quoting rules decide what <c>if exist</c> sees, so the probe was measuring <c>cmd</c> rather
/// than the wrapper.
/// </para>
/// </remarks>
[Integration]
public sealed class ForwardingSafetyTests
{
    [Fact]
    public async Task No_shell_is_involved_so_metacharacters_are_inert()
    {
        // The argument is the whole point: it is passed to a child that is asked to echo it. If a
        // shell were between them, the semicolon would end a command and the rest would run.
        var marker = Path.Combine(
            Path.GetTempPath(), $"parley-forward-{Guid.NewGuid():N}.probe");

        var hostile = OperatingSystem.IsWindows()
            ? $"x & echo owned > \"{marker}\""
            : $"x ; echo owned > '{marker}'";

        var complained = new StringWriter();

        var (tool, prefix) = OperatingSystem.IsWindows()
            ? ("cmd", (string[])["/c", "exit", "0"])
            : ("sh", ["-c", "exit 0"]);

        _ = await Forwarding.RunAsync(
            tool, "Nothing.To.Install", "parley probe", [.. prefix, hostile], complained);

        Assert.False(
            File.Exists(marker),
            "an argument reached a shell, which means the wrapper is not passing arguments as "
            + "arguments.");
    }

    [Fact]
    public async Task A_chatty_child_does_not_deadlock_the_wrapper()
    {
        // The classic wrapper bug: redirect a stream, never read it, and the child blocks on a full
        // pipe while the parent waits for a child that will never exit. This wrapper redirects
        // nothing — a terminal surface needs the real console — so the child writes to the same
        // place `parley` was writing to and there is no pipe to fill. Asserted by making a child
        // that writes far more than a pipe buffer holds.
        var complained = new StringWriter();

        var (tool, arguments) = OperatingSystem.IsWindows()
            ? ("cmd", (string[])["/c", "for /L %i in (1,1,4000) do @echo the quick brown fox jumps over the lazy dog"])
            : ("sh", ["-c", "i=0; while [ $i -lt 4000 ]; do echo the quick brown fox jumps over the lazy dog; i=$((i+1)); done"]);

        var running = Forwarding.RunAsync(
            tool, "Nothing.To.Install", "parley probe", arguments, complained);

        var finished = await Task.WhenAny(
            running, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.True(
            ReferenceEquals(finished, running),
            "the wrapper did not return within thirty seconds, which is what a full pipe looks "
            + "like from here.");

        Assert.Equal(0, await running);
    }

    [Fact]
    public async Task A_tool_that_cannot_be_started_is_reported_rather_than_thrown()
    {
        // Every platform throws something different for "not on PATH", and an unhandled one would
        // reach a person as a stack trace instead of a sentence naming the package to install.
        var complained = new StringWriter();

        var code = await Forwarding.RunAsync(
            "parley-definitely-not-a-real-tool-name",
            "Parley.Nothing",
            "parley nothing",
            ["--whatever", "value"],
            complained);

        Assert.Equal(69, code);
        Assert.Contains("install.ps1 -Only parley-definitely-not-a-real-tool-name", complained.ToString(), StringComparison.Ordinal);
    }
}
