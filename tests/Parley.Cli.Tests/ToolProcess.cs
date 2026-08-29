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
    /// Cleared rather than left alone so that whoever runs the suite cannot decide what a test
    /// reads. It is a list that has to grow with the tool: a variable added here and forgotten
    /// is one whose value leaks in from the developer's shell, which fails on one machine and
    /// nowhere else.
    /// </remarks>
    private static readonly string[] Configuration =
    [
        Parley.Core.DeploymentEnvironment.DatabaseVariable,
        Parley.Core.RetentionPolicyEnvironment.IdleVariable,
        Parley.Core.RetentionPolicyEnvironment.MaximumAgeVariable,

        // Added with the manifest, and the last of them is the one that would have hurt. A
        // developer's own PARLEY_CONFIG points at their real projects.json, so a test asserting
        // "no manifest, so nothing changes" would have read a manifest describing several
        // projects and watched the tool refuse — on that machine only, for a reason nothing in
        // the test mentions.
        Parley.Core.BrokerEndpoint.Variable,
        Parley.Headless.NodeCatalogue.PathVariable,
        Parley.Configuration.ProjectManifest.PathVariable
    ];

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
        (string Name, string Value)[] environment, params string[] arguments)
    {
        var (fileName, argument) = Locate();

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
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
}
