using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node tells the disk holding the person's home folder from any other, which is the only disk a missing workspace
/// retires its agent on (SPEC.md §14, the owner's call of 2026-10-30).
/// </summary>
/// <remarks>
/// Another disk can answer at the same path — a swapped removable drive, a disk with the same label, a backup laid out the
/// same way — and a folder there that exists and is in use says nothing about whether it is the same disk (lens 1 run 89).
/// </remarks>
[Unit]
public sealed class RetireOnlyOnTheHomeDiskTests
{
    private static readonly string[] MountInfo =
    [
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw",
        "30 22 8:17 / /home rw,relatime shared:2 - ext4 /dev/sdb1 rw",
        "41 22 8:33 / /media/someone/My\\040Disk rw,nosuid shared:9 - vfat /dev/sdc1 rw",
        "42 22 8:49 / /mnt/backup rw,relatime shared:10 - ext4 /dev/sdd1 rw",
    ];

    [Theory]
    [InlineData("/home/someone/src/orders", "/home")]
    [InlineData("/home", "/home")]
    [InlineData("/homework/orders", "/")]
    [InlineData("/media/someone/My Disk/src", "/media/someone/My Disk")]
    [InlineData("/mnt/backup/src/orders", "/mnt/backup")]
    [InlineData("/srv/work", "/")]
    public void A_paths_mount_point_is_the_longest_listed_mount_it_lies_under(string path, string expected)
    {
        Assert.Equal(expected, NodeCommand.MountPointOf(path, MountInfo));
    }

    [Fact]
    public void The_home_folder_and_a_folder_inside_it_are_on_the_home_volume()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.SkipWhen(string.IsNullOrEmpty(home), "This process has no home folder.");

        Assert.True(NodeCommand.OnTheHomeVolume(home));
        Assert.True(NodeCommand.OnTheHomeVolume(Path.Combine(home, "src", "orders")));
        Assert.True(NodeCommand.MayRetireUnder(Path.Combine(home, "src")));
    }

    [Theory]
    [InlineData("/media/someone")]
    [InlineData("/run/media/someone")]
    [InlineData("/mnt")]
    public void A_folder_removable_disks_are_mounted_in_never_retires_even_on_the_home_disk(string folder)
    {
        // On Linux these folders usually live on the home disk, so only the mount-folder rule refuses them (lens 1 run 88).
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux mount folders.");
        Assert.SkipUnless(NodeCommand.OnTheHomeVolume(folder), "This machine's home is on another disk than these folders.");

        Assert.False(NodeCommand.MayRetireUnder(folder));
    }
}
