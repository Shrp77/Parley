using System.Diagnostics;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>A file or folder this account is denied opening is still there, never read as absent.</summary>
/// <remarks>
/// The access is denied with an ACL deny entry for this account, which the owner of the file can always take away again,
/// so each test restores it in <c>finally</c>. Windows only: elsewhere the lookups are ordinary ones and never answer
/// <see cref="LocalOpenStatus.Unreadable"/> for a folder.
/// </remarks>
[Integration]
public sealed class WhatThisAccountMayNotOpenIsStillThereTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-denied-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    [Fact]
    public async Task A_launch_configuration_this_account_may_not_read_is_said_to_be_there()
    {
        // Without the guard ReadText answered no text for it, and an enrol plan read that as no file and said *create* for
        // one that exists (review, lens 3 round 31).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an ACL deny entry is Windows'");
        var launch = Path.Combine(_root, ".mcp.json");

        await File.WriteAllTextAsync(launch, "{}", TestContext.Current.CancellationToken);

        Deny(launch, "R");

        try
        {
            Assert.Throws<IOException>(() => LocalOpen.ReadText(launch, json: true));
        }
        finally
        {
            Allow(launch);
        }

        Assert.Equal("{}", LocalOpen.ReadText(launch, json: true).Text);
    }

    [Fact]
    public void A_git_folder_this_account_may_not_open_is_still_there()
    {
        // Without the guard Entry answered Unreadable for it, and a folder whose .git nobody may open stopped being a
        // repository, where Directory.Exists said it was one (review, lens 3 round 31).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an ACL deny entry is Windows'");
        var git = Directory.CreateDirectory(Path.Combine(_root, "orders", ".git")).FullName;

        Deny(git, "S");

        try
        {
            Assert.Equal(LocalOpenStatus.Opened, LocalOpen.Entry(git));
        }
        finally
        {
            Allow(git);
        }
    }

    [Fact]
    public void A_folder_this_account_may_not_open_is_still_a_folder()
    {
        // Without the guard FolderThere said no for it, and `enroll scan` called a root it may not open "not a directory"
        // (review, lens 3 round 31).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an ACL deny entry is Windows'");
        var root = Directory.CreateDirectory(Path.Combine(_root, "repositories")).FullName;

        Deny(root, "S");

        try
        {
            Assert.True(LocalOpen.FolderThere(root));
        }
        finally
        {
            Allow(root);
        }
    }

    private static string Account => $@"{Environment.UserDomainName}\{Environment.UserName}";

    private static void Deny(string path, string rights) => Icacls($"\"{path}\" /deny \"{Account}:({rights})\"");

    private static void Allow(string path) => Icacls($"\"{path}\" /remove:d \"{Account}\"");

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
