using System.Diagnostics;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>What the Windows walk answers for paths that are not what they look like.</summary>
[Integration]
public sealed class LocalOpenWalkGuardsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-local-open-walk-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_folder_named_with_a_stream_is_not_taken_for_the_folder()
    {
        // `folder::$INDEX_ALLOCATION` is the folder itself to Windows and something else to every comparison of text.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a stream name is Windows'");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "orders")).FullName;

        Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Folder(folder + "::$INDEX_ALLOCATION"));
    }

    [Fact]
    public void A_file_is_no_folder_and_a_name_under_a_file_is_not_there()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the walk on handles is Windows'");
        var file = Path.Combine(_root, "notes.md");

        File.WriteAllText(file, "x");

        Assert.Equal(LocalOpenStatus.Missing, LocalOpen.Folder(file));

        // Under a file nothing can be: not unreadable, which Entry would count as there (a repository with no .git).
        Assert.Equal(LocalOpenStatus.Missing, LocalOpen.Folder(Path.Combine(file, "inner")));
        Assert.Equal(LocalOpenStatus.Missing, LocalOpen.Entry(Path.Combine(file, ".git")));
    }

    [Fact]
    public void A_drive_root_is_no_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a drive letter is Windows'");
        var root = Path.GetPathRoot(_root)!;

        var (status, stream) = LocalOpen.File(root, FileAccess.Read, FileShare.ReadWrite);

        stream?.Dispose();
        Assert.Equal(LocalOpenStatus.Missing, status);
    }

    [Fact]
    public void A_file_another_program_holds_is_busy_not_unreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a sharing violation is Windows'");
        var file = Path.Combine(_root, "held.md");

        File.WriteAllText(file, "x");

        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (status, stream) = LocalOpen.File(file, FileAccess.Read, FileShare.ReadWrite);

        stream?.Dispose();
        Assert.Equal(LocalOpenStatus.Busy, status);
    }

    [Fact]
    public void A_letter_no_disk_has_holds_nothing_and_is_not_another_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a drive letter is Windows'");
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).ToList();

        Assert.SkipWhen(free.Count == 0, "every letter is in use here");

        Assert.Equal(LocalOpenStatus.Missing, LocalOpen.Folder($@"{free[^1]}:\orders"));
    }

    [Fact]
    public void Where_a_folder_this_account_may_not_open_leads_cannot_be_read_so_it_is_not_taken_for_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an ACL deny entry is Windows'");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "denied")).FullName;

        Icacls($"\"{folder}\" /deny \"{Account}:(S)\"");

        try
        {
            Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Reach(folder, out _));
        }
        finally
        {
            Icacls($"\"{folder}\" /remove:d \"{Account}\"");
        }
    }

    private static string Account => $@"{Environment.UserDomainName}\{Environment.UserName}";

    private static void Icacls(string arguments)
    {
        using var icacls = Process.Start(new ProcessStartInfo("icacls.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;

        var said = icacls.StandardOutput.ReadToEnd() + icacls.StandardError.ReadToEnd();

        icacls.WaitForExit();
        Assert.True(icacls.ExitCode == 0, $"icacls {arguments}: {said}");
    }
}
