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
        // A child listed before its parent, so the longest match and the last listed one differ (lens 3 run 101).
        "50 30 8:65 / /home/someone/data rw,relatime shared:11 - ext4 /dev/sde1 rw",
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw",
        "30 22 8:17 / /home rw,relatime shared:2 - ext4 /dev/sdb1 rw",
        "41 22 8:33 / /media/someone/My\\040Disk rw,nosuid shared:9 - vfat /dev/sdc1 rw",
        "42 22 8:49 / /mnt/backup rw,relatime shared:10 - ext4 /dev/sdd1 rw",
    ];

    [Theory]
    [InlineData("/home/someone/src/orders", "/home")]
    [InlineData("/home/someone/data/orders", "/home/someone/data")]
    [InlineData("/home", "/home")]
    [InlineData("/homework/orders", "/")]
    [InlineData("/media/someone/My Disk/src", "/media/someone/My Disk")]
    [InlineData("/mnt/backup/src/orders", "/mnt/backup")]
    [InlineData("/srv/work", "/")]
    public void A_paths_mount_point_is_the_longest_listed_mount_it_lies_under(string path, string expected)
    {
        Assert.Equal(expected, NodeCommand.MountPointOf(path, MountInfo));
    }

    [Theory]
    [InlineData("/Users/someone/src/orders", "/")]
    [InlineData("/volumes/usb/src/orders", "/Volumes/usb")]
    [InlineData("/Users/someone/Work/orders", "/Users/someone/Work")]
    [InlineData("/users/SOMEONE/work/orders", "/Users/someone/Work")]
    public void On_macOS_a_path_is_matched_to_its_volume_ignoring_case_and_wherever_it_is_mounted(string path, string expected)
    {
        // Lens 2 run 92: APFS ignores case, so a workspace typed /volumes/... is on the disk at /Volumes/...; and a disk
        // mounted at a folder the person chose (~/Work) is its own volume, not the home disk.
        string[] roots = ["/", "/System/Volumes/Data", "/Volumes/usb", "/Users/someone/Work"];

        Assert.Equal(expected, NodeCommand.LongestUnder(path, roots, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/Users/someone/src", true)]
    [InlineData("/users/SOMEONE/src", true)]
    [InlineData("/Volumes/usb/src", false)]
    [InlineData("/volumes/usb/src", false)]
    [InlineData("/volumes/stale/src", false)]
    [InlineData("/Volumes/stale/src", false)]
    [InlineData("/Users/someone/Work/src", false)]
    [InlineData("/users/someone/WORK/src", false)]
    public void The_macOS_rule_tells_the_home_volume_from_any_other_on_every_platform(string folder, bool expected)
    {
        // The macOS branch never runs in CI, which runs only unit tests on macOS (lens 3 run 102); the rule it applies does,
        // here, with the mounted roots passed in: /Volumes ignoring case, and a disk mounted at a folder of the person's own.
        string[] roots = ["/", "/System/Volumes/Data", "/Volumes/usb", "/Users/someone/Work"];

        Assert.Equal(expected, NodeCommand.OnTheSameMacVolume(folder, "/Users/someone", roots));
    }
}

/// <summary>
/// That this machine's own home folder, mount table and disks are told apart as the rule needs (SPEC.md §14).
/// </summary>
/// <remarks>
/// Integration rather than unit: these read the process's environment and, on Linux, its mount table. Linux and Windows hold
/// both directions here; macOS's rule is held as a unit test, with its roots passed in, because CI runs only unit tests on
/// macOS (lens 3 runs 100 and 102).
/// </remarks>
[Integration]
public sealed class ThisMachinesHomeDiskTests
{
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
    [InlineData("/proc")]
    [InlineData("/sys")]
    public void A_separate_mount_is_not_the_home_disk_on_Linux(string folder)
    {
        // Each is its own mount on every Linux system, and nobody's home folder is under one.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux mount table.");

        Assert.False(NodeCommand.OnTheHomeVolume(folder));
    }

    [Fact]
    public void Another_disk_in_Volumes_is_not_the_home_disk_on_macOS()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS volumes.");

        Assert.False(NodeCommand.OnTheHomeVolume("/Volumes/Backup"));
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
