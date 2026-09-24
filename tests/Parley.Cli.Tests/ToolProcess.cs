using System.Diagnostics;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Runs the real <c>parley</c> binary.
/// </summary>
/// <remarks>
/// <para>
/// The tests that need this are the ones about what the program does to a file or an
/// environment, rather than what a method returns — a mistyped <c>--db</c>, an unreadable
/// retention variable, an option in the wrong place. None of those are reachable from
/// <see cref="OperatorCommands"/>, because none of them get that far.
/// </para>
/// <para>
/// Shared because there were two copies of it and they had already begun to differ: one
/// cleared <c>PARLEY_DB</c> and the other cleared the retention variables as well, so which
/// stray variable from an operator's own shell could reach a test depended on which file the
/// test happened to be in. That is the failure mode this removes, not the line count.
/// </para>
/// </remarks>
internal static class ToolProcess
{
    /// <summary>Every variable the tool reads, cleared before each run.</summary>
    /// <remarks>
    /// <para>
    /// Cleared rather than left alone so that whoever runs the suite cannot decide what a test
    /// reads. It is a list that has to grow with the tool: a variable added here and forgotten
    /// is one whose value leaks in from the developer's shell, which fails on one machine and
    /// nowhere else.
    /// </para>
    /// <para>
    /// <b>It has to shrink with the tool too, and <c>PARLEY_DB</c> is the first entry to go</b>
    /// (019, T037). The tool stopped reading it when <c>--db</c> went: a project's store is
    /// <c>parley.db</c> inside its own folder, derived from its name. Clearing it would be
    /// harmless and that is exactly the problem — this list is read as the answer to "what does
    /// the tool take from an environment", and an entry that has stopped being true makes the
    /// whole list evidence of nothing.
    /// </para>
    /// <para>
    /// What guards the variable now is a test rather than a clear:
    /// <c>ServeSelectionTests</c> sets it deliberately and asserts the store it names is not
    /// opened, which is the stronger check — it fails if somebody starts reading the variable
    /// again, where a cleared variable would simply go on being absent.
    /// </para>
    /// </remarks>
    private static readonly string[] Configuration =
    [
        Parley.Core.RetentionPolicyEnvironment.IdleVariable,
        Parley.Core.RetentionPolicyEnvironment.MaximumAgeVariable,

        // Added with the manifest, and the last of them is the one that would have hurt. A
        // developer's own PARLEY_CONFIG points at their real projects.json, so a test asserting
        // "no manifest, so nothing changes" would have read a manifest describing several
        // projects and watched the tool refuse — on that machine only, for a reason nothing in
        // the test mentions.
        Parley.Core.BrokerEndpoint.Variable,
        Parley.Configuration.NodeCatalogue.PathVariable,
        Parley.Configuration.ProjectManifest.PathVariable,

        // A terminal's project, since commands take one from it. A developer who has run `parley use` would otherwise
        // lend every test in that shell a project.
        Parley.Setup.EnrollmentPlan.ProjectVariable
    ];

    /// <summary>A directory in no repository, created once, where the tool runs unless a test says otherwise.</summary>
    /// <remarks>
    /// <b>Never the directory the suite runs in.</b> The tool takes a project from the repository it runs in, and the suite
    /// runs inside this repository — whose <c>.mcp.json</c>, on a machine that registered it as an agent, names a real
    /// project. A test would then act on whatever that entry names, on that machine only, for a reason nothing in the test
    /// mentions: the failure <see cref="Configuration"/> exists to prevent, arriving by a door it cannot see.
    /// </remarks>
    private static readonly Lazy<string> Neutral = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "parley-tool-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        return directory;
    });

    /// <summary>Why this still sets a variable the clearing loop above already touched.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not redundant with <see cref="Parley.Tests.ManifestIsolation"/>, which sets the same
    /// path in this process.</b> The child's environment is seeded from the parent's and then the
    /// loop above empties every configuration variable — including this one — so without the line
    /// in <see cref="Launch"/> the child would inherit an empty value and fall straight back to
    /// the default. The two are one value and one intent, which is why the path comes from there
    /// rather than being a second constant here.
    /// </para>
    /// <para>
    /// <b>Clearing <c>PARLEY_CONFIG</c> does the opposite of what the comment above believed.</b>
    /// <c>ProjectManifest.Locate</c> treats an empty variable as unset and falls back to the
    /// per-user default — so emptying it does not remove the manifest, it *guarantees* the tool
    /// reads the developer's real one at <c>%APPDATA%\Parley\projects.json</c>. The isolation was
    /// exactly inverted, and the whole suite passed anyway because no machine that ran it had
    /// named a project yet.
    /// </para>
    /// <para>
    /// It surfaced the day one did: eight tests failed at once, none of them about manifests.
    /// <c>parley serve</c> with no <c>--db</c> found a store through the project and got as far as
    /// binding a port the real broker already held, so a test expecting "no database" was handed
    /// an unavailable endpoint instead.
    /// </para>
    /// <para>
    /// Named rather than emptied, because a path is the only value that means "no manifest" to
    /// something that falls back when unset. The directory is deliberately never created: nothing
    /// here writes a manifest, and if something ever does, failing to find the folder is the
    /// loud version of a test quietly editing the developer's configuration.
    /// </para>
    /// </remarks>
    private static string NoManifest => Parley.Tests.ManifestIsolation.NoManifest;

    /// <summary>Locates the tool built beside the tests.</summary>
    /// <returns>The executable to run, and the argument that selects it under <c>dotnet</c>.</returns>
    private static (string FileName, string? Argument) Locate()
    {
        var directory = Path.GetDirectoryName(typeof(ToolProcess).Assembly.Location)!;
        var apphost = Path.Combine(directory, OperatingSystem.IsWindows() ? "parley.exe" : "parley");

        if (File.Exists(apphost))
        {
            return (apphost, null);
        }

        var library = Path.Combine(directory, "parley.dll");
        Assert.True(File.Exists(library), $"expected the tool beside the tests in {directory}");

        return ("dotnet", library);
    }

    /// <summary>Starts the tool without waiting for it.</summary>
    /// <param name="environment">Variables to set for this run, after the tool's own are cleared.</param>
    /// <param name="arguments">The command line.</param>
    /// <returns>The running process, whose output the caller reads.</returns>
    /// <remarks>
    /// For <c>serve</c> and <c>up</c>, which run until they are stopped. Every other command
    /// returns promptly and goes through <see cref="Run(string[])"/>, which reads both streams
    /// to the end — doing that to a process that never exits would block for ever.
    /// </remarks>
    public static Process Launch(
        (string Name, string Value)[] environment, params string[] arguments) =>
        LaunchIn(null, environment, arguments);

    /// <summary>Starts the tool in a working directory of the test's choosing, without waiting for it.</summary>
    /// <param name="directory">Where the tool runs, or null for a directory in no repository.</param>
    /// <param name="environment">Variables to set for this run, after the tool's own are cleared.</param>
    /// <param name="arguments">The command line.</param>
    /// <returns>The running process, whose output the caller reads.</returns>
    public static Process LaunchIn(
        string? directory, (string Name, string Value)[] environment, params string[] arguments)
    {
        var (fileName, argument) = Locate();

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory ?? Neutral.Value,
        };

        if (argument is not null)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var value in arguments)
        {
            start.ArgumentList.Add(value);
        }

        foreach (var name in Configuration)
        {
            start.Environment[name] = "";
        }

        // After the clearing loop, because for this one variable an empty value means "use the
        // default" and the default is the machine's own manifest. See NoManifest.
        start.Environment[Parley.Configuration.ProjectManifest.PathVariable] = NoManifest;

        // Last, so a test that wants a manifest of its own still gets one.
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        return Process.Start(start)!;
    }

    /// <summary>Runs the tool to completion, keeping both streams.</summary>
    /// <param name="environment">Variables to set for this run, after the tool's own are cleared.</param>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code and both of its streams.</returns>
    /// <remarks>
    /// Separate from <see cref="Run(ValueTuple{string, string}[], string[])"/> because most callers
    /// want the refusal and nothing else, and the parity checks want the opposite: what a command
    /// prints when it succeeds, so they can assert that a missing manifest is never mentioned. A
    /// tool that names an absent optional file on every run has made it mandatory in practice.
    /// </remarks>
    public static (int ExitCode, string Output, string Error) RunFully(
        (string Name, string Value)[] environment, params string[] arguments)
    {
        using var process = Launch(environment, arguments);

        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the tool never exited");
        Task.WaitAll([error, output], TimeSpan.FromSeconds(10));

        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>Runs the tool to completion in a working directory of the test's choosing, keeping both streams.</summary>
    /// <param name="directory">Where the tool runs.</param>
    /// <param name="environment">Variables to set for this run, after the tool's own are cleared.</param>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code and both of its streams.</returns>
    public static (int ExitCode, string Output, string Error) RunFullyIn(
        string directory, (string Name, string Value)[] environment, params string[] arguments)
    {
        using var process = LaunchIn(directory, environment, arguments);

        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the tool never exited");
        Task.WaitAll([error, output], TimeSpan.FromSeconds(10));

        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>Runs the tool to completion.</summary>
    /// <param name="environment">Variables to set for this run, after the tool's own are cleared.</param>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code and what it wrote to standard error.</returns>
    public static (int ExitCode, string Error) Run(
        (string Name, string Value)[] environment, params string[] arguments)
    {
        using var process = Launch(environment, arguments);

        // Both pipes are drained at once, and that is not tidiness. Reading one to the end while
        // the other is redirected and unread deadlocks as soon as the unread pipe fills: the
        // child blocks writing, the parent blocks reading, and the 30-second wait below is never
        // reached because nothing has exited.
        //
        // It sat here harmlessly while `--help` was 3,744 bytes and stopped being harmless at
        // 4,563, because Windows gives a pipe 4,096. The symptom was `parley --help` hanging
        // forever — a command with no I/O, no broker and no database — which sends whoever meets
        // it looking anywhere but at the reader. Adding a line to the usage text is not supposed
        // to be able to hang the suite.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the tool never exited");
        Task.WaitAll([error, output], TimeSpan.FromSeconds(10));

        return (process.ExitCode, error.Result);
    }

    /// <summary>Runs the tool with no configuration in its environment.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code and what it wrote to standard error.</returns>
    public static (int ExitCode, string Error) Run(params string[] arguments) =>
        Run([], arguments);

    /// <summary>Runs the tool and keeps what it wrote to standard output as well.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code, its output, and what it complained.</returns>
    /// <remarks>
    /// Most commands here are checked by their exit code and their complaint, which is why the
    /// overloads above drop the output. <c>parley open</c> is the exception and had to be: it
    /// writes a line per open conversation to standard output because a script reads it, and a
    /// test that could not see that would be asserting the exit code of a command whose whole
    /// value is what it printed.
    /// </remarks>
    public static (int ExitCode, string Output, string Error) Reading(params string[] arguments)
    {
        using var process = Launch([], arguments);

        // Both pipes drained at once, for the reason the overload above spells out: reading one to
        // the end while the other fills deadlocks both.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the tool never exited");
        Task.WaitAll([error, output], TimeSpan.FromSeconds(10));

        return (process.ExitCode, output.Result, error.Result);
    }
}
