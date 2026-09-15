using System.Diagnostics;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// <c>parley-mcp --version</c> answers without a launch configuration, as the other three tools do.
/// </summary>
/// <remarks>
/// The installer asks every tool it installed which version it is, from a shell that sets nothing. This one answered
/// only with <c>PARLEY_AGENT</c> set, so it exited 78 having printed its complaint to standard error, and the
/// installer's check read the empty standard output as a match. Found while verifying a real installation.
/// </remarks>
public sealed class VersionTests
{
    [Fact]
    public async Task It_names_its_build_and_protocol_with_no_parley_variable_set()
    {
        // From the adapter's own build directory, never the copy beside these tests. That copy is missing the hosting
        // assemblies a project reference does not bring along, and dies loading them before it reads an argument --
        // the same trap CLAUDE.md records for the terminal's copy beside its tests.
        var here = new DirectoryInfo(Path.GetDirectoryName(typeof(VersionTests).Assembly.Location)!);
        var root = here;

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var directory = Path.Combine(root.FullName, "src", "Parley.Mcp", "bin", here.Parent!.Name, here.Name);
        var apphost = Path.Combine(directory, OperatingSystem.IsWindows() ? "parley-mcp.exe" : "parley-mcp");

        Assert.True(File.Exists(apphost) || File.Exists(Path.Combine(directory, "parley-mcp.dll")), $"no parley-mcp built in {directory}");

        var start = File.Exists(apphost)
            ? new ProcessStartInfo(apphost)
            : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(directory, "parley-mcp.dll") } };

        start.ArgumentList.Add("--version");
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;

        foreach (var variable in start.Environment.Keys.Where(k => k.StartsWith("PARLEY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(variable);
        }

        using var process = Process.Start(start)!;

        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.True(process.WaitForExit(30_000), "parley-mcp --version did not exit");

        var said = await output;
        var complained = await error;

        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {complained}");
        Assert.Matches(@"^parley-mcp \d+\.\d+\.\d+, speaking Parley protocol \d+\.\d+$", said.Trim());
    }
}
