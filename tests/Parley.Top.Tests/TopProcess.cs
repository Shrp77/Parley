using System.Diagnostics;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// Runs the real <c>parley-top</c> binary.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <c>ToolProcess</c>, which runs <c>parley</c>, and separate from it for a reason
/// that only appeared once the two became different tools.
/// </para>
/// <para>
/// <b>It runs the tool out of its own build directory, and must.</b> A copy of
/// <c>parley-top</c> does land beside these tests, because this project references both tools —
/// and that copy does not work. This project also references <c>Parley.Cli</c>, which brings a
/// framework reference to ASP.NET Core, so several <c>Microsoft.Extensions.Configuration</c>
/// assemblies are resolved from the shared framework and left out of the output. The terminal's
/// own runtime configuration names only the base framework, and Terminal.Gui's module initializer
/// scans every loaded assembly for configuration attributes on start-up — so the co-located copy
/// dies with a <c>TypeInitializationException</c> before reaching its first line of code.
/// </para>
/// <para>
/// That is an artifact of two tools sharing one output directory, which nothing ships: the tool's
/// own directory has every assembly it needs, and so does a published tool package. It is written
/// down because the failure looks exactly like a broken tool, and the first instinct on seeing it
/// is to go looking in the terminal.
/// </para>
/// </remarks>
internal static class TopProcess
{
    /// <summary>Runs the tool and reads both streams to the end.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>The exit code, what it wrote out, and what it complained.</returns>
    public static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The same isolation ToolProcess applies: a test must never read, and must never write,
        // the manifest belonging to whoever is running the suite.
        start.Environment["PARLEY_CONFIG"] = Parley.Tests.ManifestIsolation.NoManifest;

        using var running = Process.Start(start);

        Assert.NotNull(running);

        var output = running!.StandardOutput.ReadToEnd();
        var error = running.StandardError.ReadToEnd();

        running.WaitForExit();

        return (running.ExitCode, output, error);
    }

    /// <summary>Finds the terminal's own build output.</summary>
    /// <returns>The executable.</returns>
    /// <remarks>
    /// Located by the configuration and framework this test assembly was itself built for, rather
    /// than by a written-down path, so a Release run does not silently exercise a Debug binary.
    /// </remarks>
    private static string Executable()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = here.Name;
        var configuration = here.Parent!.Name;
        var root = here;

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var name = OperatingSystem.IsWindows() ? "parley-top.exe" : "parley-top";

        var executable = Path.Combine(
            root!.FullName, "src", "Parley.Top", "bin", configuration, framework, name);

        Assert.True(File.Exists(executable), $"expected the terminal built at {executable}");

        return executable;
    }
}
