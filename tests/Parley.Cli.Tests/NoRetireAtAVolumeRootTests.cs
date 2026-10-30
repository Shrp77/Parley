using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a workspace at the root of a drive or of a removable disk's mount is never reported gone (SPEC.md §14, the owner's
/// call of 2026-10-30).
/// </summary>
/// <remarks>
/// The folder above such a workspace belongs to whatever volume answers there now: another disk that took the drive's
/// letter (lens 2 run 90), or a desktop's mount folder holding a second disk (lens 1 run 88). Its being there and in use
/// says nothing about whether the repository was deleted.
/// </remarks>
[Unit]
public sealed class NoRetireAtAVolumeRootTests
{
    [Fact]
    public void A_drives_root_is_a_volume_root_and_a_folder_in_it_is_not()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.True(NodeCommand.AtAVolumeRoot(root));
        Assert.False(NodeCommand.AtAVolumeRoot(Path.Combine(root, "work")));
    }

    [Theory]
    [InlineData("/Volumes", true)]
    [InlineData("/mnt", true)]
    [InlineData("/media", true)]
    [InlineData("/media/someone", true)]
    [InlineData("/run/media", true)]
    [InlineData("/run/media/someone", true)]
    [InlineData("/home/someone", false)]
    [InlineData("/media/someone/DISK", false)]
    [InlineData("/srv/work", false)]
    public void The_folders_removable_disks_are_mounted_in_are_volume_roots(string folder, bool expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix mount folders are absolute only on Unix.");

        Assert.Equal(expected, NodeCommand.AtAVolumeRoot(folder));
    }
}
