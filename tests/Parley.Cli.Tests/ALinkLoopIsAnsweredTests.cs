using System.Diagnostics;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>Two links that lead to each other are answered, never followed for ever.</summary>
[Integration]
public sealed class ALinkLoopIsAnsweredTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-link-loop-").FullName;

    private string First => Path.Combine(_root, "a");

    private string Second => Path.Combine(_root, "b");

    public void Dispose()
    {
        // The junctions by exact name and alone, never a recursive delete through one.
        foreach (var link in new[] { First, Second })
        {
            try
            {
                if (new DirectoryInfo(link).LinkTarget is not null)
                {
                    Directory.Delete(link);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        try
        {
            Directory.Delete(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Two_junctions_leading_to_each_other_are_answered()
    {
        // Without the bound on links followed the walk followed a to b to a until the stack ran out, and the process died
        // with no word said (review, lens 3 round 31).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the walk on handles, and junctions, are Windows'");

        Junction(First, Second);
        Junction(Second, First);

        Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Folder(First));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
