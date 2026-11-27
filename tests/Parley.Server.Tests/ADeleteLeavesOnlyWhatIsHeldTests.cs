using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That deleting a folder deletes everything it can when another program holds one file, and says what was left.
/// </summary>
/// <remarks>
/// Lens 2 run 131: the delete behind a destroy, a purge and the sweep stopped at the first file it could not delete, so
/// one file held open left every other file of the folder on disk, and the problem raised named only the held one.
/// Lens 2 run 132: a folder swapped for a link after it was stacked was followed, a folder that could not be read was
/// named twice, and "the rest deleted" was claimed when it was not checked.
/// Integration rather than unit, because it writes a folder: the unit bar touches no file.
/// </remarks>
[Integration]
public sealed class ADeleteLeavesOnlyWhatIsHeldTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "parley-delete-" + Guid.NewGuid().ToString("N"));

    private readonly string _outside = Path.Combine(Path.GetTempPath(), "parley-outside-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _denied = [];

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in _denied)
            {
                Deny(folder, undo: true);
            }
        }

        foreach (var folder in new[] { _folder, _outside })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>Takes away, or gives back, this user's right to list a folder.</summary>
    /// <remarks>A folder that cannot be read, made without another program: the owner may still change its rules.</remarks>
    [SupportedOSPlatform("windows")]
    private void Deny(string folder, bool undo = false)
    {
        var info = new DirectoryInfo(folder);
        var rules = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);

        if (undo)
        {
            rules.RemoveAccessRule(rule);
        }
        else
        {
            rules.AddAccessRule(rule);
            _denied.Add(folder);
        }

        info.SetAccessControl(rules);
    }

    [Fact]
    public void A_folder_that_cannot_be_read_is_named_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "takes the right to list a folder away with a Windows access rule");

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_folder, "locked"));
        File.WriteAllText(Path.Combine(_folder, "locked", "kept.md"), "notes");
        File.WriteAllText(Path.Combine(_folder, "a.md"), "notes");
        Deny(Path.Combine(_folder, "locked"));

        var refused = Assert.Throws<IOException>(() => FileLocationStore.DeleteTree(_folder));

        // Named for the read that failed, and not again for the delete of the folder it left full.
        Assert.StartsWith("1 entry could not be deleted: locked.", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "a.md")));
    }

    [Fact]
    public void A_folder_whose_own_listing_fails_names_only_itself()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "takes the right to list a folder away with a Windows access rule");

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "a.md"), "notes");
        Deny(_folder);

        var refused = Assert.Throws<IOException>(() => FileLocationStore.DeleteTree(_folder));

        Assert.StartsWith("1 entry could not be deleted: the folder itself.", refused.Message, StringComparison.Ordinal);

        Deny(_folder, undo: true);
        Assert.True(File.Exists(Path.Combine(_folder, "a.md")));
    }

    [Fact]
    public void A_folder_swapped_for_a_link_after_it_was_stacked_is_removed_as_a_link_and_not_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "makes a junction with mklink");

        Directory.CreateDirectory(Path.Combine(_folder, "inner"));
        File.WriteAllText(Path.Combine(_folder, "inner", "c.md"), "notes");
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "theirs.md"), "somebody else's");

        var swapped = false;

        // What another program would do between the folder being stacked and its being read: put a link in its place.
        FileLocationStore.DeleteTree(_folder, reading: path =>
        {
            if (swapped || !path.EndsWith(Path.DirectorySeparatorChar + "inner", StringComparison.Ordinal))
            {
                return;
            }

            swapped = true;

            var plain = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

            Directory.Delete(plain, recursive: true);

            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{plain}\" \"{_outside}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;

            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        });

        Assert.True(swapped);
        Assert.True(File.Exists(Path.Combine(_outside, "theirs.md")));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void One_held_file_of_three_is_left_and_named_and_the_other_two_are_gone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows lets another program hold a file against a delete");

        Directory.CreateDirectory(Path.Combine(_folder, "inner"));

        // Held in the middle of the walk, so a delete that stopped there would leave what comes after it.
        foreach (var name in new[] { "a.md", "b.md", Path.Combine("inner", "c.md") })
        {
            File.WriteAllText(Path.Combine(_folder, name), "notes");
        }

        IOException refused;

        using (new FileStream(Path.Combine(_folder, "b.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            refused = Assert.Throws<IOException>(() => FileLocationStore.DeleteTree(_folder));
        }

        Assert.False(File.Exists(Path.Combine(_folder, "a.md")));
        Assert.False(Directory.Exists(Path.Combine(_folder, "inner")));
        Assert.True(File.Exists(Path.Combine(_folder, "b.md")));

        // What is left, and only that: the folder that holds it is not a second thing reported.
        Assert.StartsWith("1 entry could not be deleted: b.md.", refused.Message, StringComparison.Ordinal);

        // Once nothing holds it, the same delete finishes.
        FileLocationStore.DeleteTree(_folder);
        Assert.False(Directory.Exists(_folder));
    }
}
