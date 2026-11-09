using Parley.Client.Files;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Opening a placed file or a conversation's folder: when something is launched, and what is said instead
/// (SPEC.md §10.2, §13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here launches anything.</b> The launcher's final step is a seam, and every case records what it would
/// have opened. The rules in front of that step are what these check, because they are what stands between a file
/// another participant placed and a program running on the person's machine.
/// </para>
/// <para>
/// The files are real and temporary, because "exists here" is part of the rule: a machine name that matches while
/// the path does not exist must launch nothing.
/// </para>
/// </remarks>
[Integration]
public sealed class LocalLauncherTests : IDisposable
{
    private const string Here = "this-machine";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-launcher-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Path, bool Folder)> _launched = [];
    private readonly LocalLauncher _launcher;

    public LocalLauncherTests()
    {
        Directory.CreateDirectory(_root);
        _launcher = new LocalLauncher(
            (path, folder) =>
            {
                _launched.Add((path, folder));
                return true;
            },
            Here);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Theory]
    [InlineData("price-snapshot.md")]
    [InlineData("PriceSnapshot.cs.txt")]
    [InlineData("NOTES.MD")]
    [InlineData("shot.png")]
    [InlineData("photo.jpeg")]
    public void A_kind_of_file_that_can_be_placed_is_opened(string name)
    {
        var path = Write(name);

        var launched = _launcher.OpenFile(new FileLocation(Here, path));

        Assert.True(launched.Opened);
        Assert.Equal((path, false), Assert.Single(_launched));
    }

    [Theory]
    [InlineData("run.bat")]
    [InlineData("tool.exe")]
    [InlineData("script.ps1")]
    [InlineData("shortcut.lnk")]
    [InlineData("PriceSnapshot.cs")]
    [InlineData("drawing.svg")]
    public void Any_other_file_is_not_launched_and_its_path_is_given(string name)
    {
        // Nothing an agent places can have these names, so a file like this reached the location some other way —
        // which is exactly the file that must not be one click from running.
        var path = Write(name);

        var launched = _launcher.OpenFile(new FileLocation(Here, path));

        Assert.False(launched.Opened);
        Assert.Contains(path, launched.Said, StringComparison.Ordinal);
        Assert.Empty(_launched);
    }

    [Fact]
    public void A_file_on_another_machine_is_not_launched_and_the_machine_and_path_are_given()
    {
        var launched = _launcher.OpenFile(new FileLocation("broker-box", "/srv/parley/alpha.db.files/pricing/notes.md"));

        Assert.False(launched.Opened);
        Assert.Equal("notes.md is on machine broker-box, at /srv/parley/alpha.db.files/pricing/notes.md.", launched.Said);
        Assert.Empty(_launched);
    }

    [Fact]
    public void A_machine_name_that_matches_while_the_path_does_not_exist_here_launches_nothing()
    {
        var path = Path.Combine(_root, "absent.md");

        var launched = _launcher.OpenFile(new FileLocation(Here, path));

        Assert.False(launched.Opened);
        Assert.Empty(_launched);
    }

    [Fact]
    public void A_file_no_editor_takes_says_so_and_where_it_is()
    {
        var refusing = new LocalLauncher((_, _) => false, Here);
        var path = Write("notes.md");

        var launched = refusing.OpenFile(new FileLocation(Here, path));

        Assert.False(launched.Opened);
        Assert.Contains("No editor", launched.Said, StringComparison.Ordinal);
        Assert.Contains(path, launched.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_here_is_opened_in_the_file_browser()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "pricing")).FullName;

        var launched = _launcher.OpenFolder(new FileLocation(Here, folder));

        Assert.True(launched.Opened);
        Assert.Equal((folder, true), Assert.Single(_launched));
    }

    [Fact]
    public void A_folder_on_another_machine_is_described_rather_than_opened()
    {
        var launched = _launcher.OpenFolder(new FileLocation("broker-box", "/srv/parley/alpha.db.files/pricing"));

        Assert.False(launched.Opened);
        Assert.Equal("The files are on machine broker-box, in /srv/parley/alpha.db.files/pricing.", launched.Said);
        Assert.Empty(_launched);
    }

    [Fact]
    public void The_real_launcher_is_for_this_machine()
    {
        // Pinned so a refactor cannot quietly make every location look remote, which would fail safe and useless.
        Assert.NotNull(LocalLauncher.ForThisMachine);
        Assert.False(string.IsNullOrEmpty(MachineName.Current));
    }

    [Fact]
    public void A_copy_of_a_file_on_another_machine_is_written_under_its_hash_and_opened()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var read = Read("mockup.png", png);

        var launched = _launcher.OpenCopy(read, Copies);

        var path = Path.Combine(Copies, read.File!.Sha256.Value, "mockup.png");
        Assert.True(launched.Opened);
        Assert.Equal((path, false), Assert.Single(_launched));
        Assert.Equal(png, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_copy_named_as_an_image_whose_bytes_are_not_one_is_neither_written_nor_opened()
    {
        var read = Read("mockup.png", "<html><script>alert(1)</script></html>"u8.ToArray());

        var launched = _launcher.OpenCopy(read, Copies);

        Assert.False(launched.Opened);
        Assert.Equal("mockup.png is not a readable image, so nothing was opened.", launched.Said);
        Assert.Empty(_launched);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void A_read_with_no_bytes_opens_nothing_and_says_why()
    {
        var read = Read("big.png", [0x89]) with { Bytes = null, TooLarge = true };

        var launched = _launcher.OpenCopy(read, Copies);

        Assert.False(launched.Opened);
        Assert.Contains("too large", launched.Said, StringComparison.Ordinal);
        Assert.Empty(_launched);
    }

    [Fact]
    public void Something_else_already_under_a_copys_name_is_replaced_before_it_is_opened()
    {
        // Another program put other bytes where the copy goes. Opening it would show them under the placed file's hash.
        var read = Read("report.md", "the placed words"u8.ToArray());
        var path = Path.Combine(Copies, read.File!.Sha256.Value, "report.md");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "planted words");

        var launched = _launcher.OpenCopy(read, Copies);

        Assert.True(launched.Opened);
        Assert.Equal("the placed words", File.ReadAllText(path));
    }

    [Fact]
    public void A_copy_folder_that_is_a_link_is_never_written_through()
    {
        // Another account made the hash's folder a link to the person's repository: the copy would land there.
        var read = Read("CLAUDE.md", "an agent's words"u8.ToArray());
        var elsewhere = Path.Combine(_root, "repository");

        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Copies);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(Copies, read.File!.Sha256.Value), elsewhere);
        }
        catch (Exception refused) when (refused is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This machine does not let the test make a link.");
        }

        var launched = _launcher.OpenCopy(read, Copies);

        Assert.False(launched.Opened);
        Assert.Contains("is a link", launched.Said, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Empty(_launched);
    }

    [Fact]
    public void A_copies_folder_other_accounts_can_write_in_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            var read = Read("report.md", "words"u8.ToArray());

            Directory.CreateDirectory(Copies);
            File.SetUnixFileMode(Copies, (UnixFileMode)0b111_111_111);

            var launched = _launcher.OpenCopy(read, Copies);

            Assert.False(launched.Opened);
            Assert.Contains("other accounts", launched.Said, StringComparison.Ordinal);
            Assert.Empty(_launched);
        }
        else
        {
            Assert.Skip("Windows keeps the folder in the user's own profile; the mode bits are a Unix check.");
        }
    }

    [Fact]
    public void The_copies_folder_is_the_users_own_not_the_shared_temp_folder()
    {
        Assert.False(
            LocalLauncher.Copies.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"copies go to {LocalLauncher.Copies}, inside the temp folder every account can write in");
    }

    /// <summary>Where the copies go: a folder the launcher makes itself, as it makes the real one.</summary>
    private string Copies => Path.Combine(_root, "copies");

    private static SharedFileRead Read(string name, byte[] bytes) => new(
        new SharedFile("pricing", "pricing", SharedFileName.Create(name), bytes.Length, ContentHash.Of(bytes), null, DateTimeOffset.UnixEpoch),
        bytes,
        false,
        null);

    private string Write(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "net of tax");
        return path;
    }
}
