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
        Parley.Core.RetentionPolicyEnvironment.MaximumAgeVariable
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

    /// <summary>Runs the tool to completion.</summary>
    /// <param name="environment">Variables to set for this run, after the tool's own are cleared.</param>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code and what it wrote to standard error.</returns>
    public static (int ExitCode, string Error) Run(
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

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the tool never exited");

        return (process.ExitCode, error);
    }

    /// <summary>Runs the tool with no configuration in its environment.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>Its exit code and what it wrote to standard error.</returns>
    public static (int ExitCode, string Error) Run(params string[] arguments) =>
        Run([], arguments);
}
