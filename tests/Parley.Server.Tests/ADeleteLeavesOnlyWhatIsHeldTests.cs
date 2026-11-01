using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That deleting a folder deletes everything it can when another program holds one file, and says what was left.
/// </summary>
/// <remarks>
/// Lens 2 run 131: the delete behind a destroy, a purge and the sweep stopped at the first file it could not delete, so
/// one file held open left every other file of the folder on disk, and the problem raised named only the held one.
/// Integration rather than unit, because it writes a folder: the unit bar touches no file.
/// </remarks>
[Integration]
public sealed class ADeleteLeavesOnlyWhatIsHeldTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "parley-delete-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
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
        Assert.StartsWith("1 entry was left, the rest deleted: b.md.", refused.Message, StringComparison.Ordinal);

        // Once nothing holds it, the same delete finishes.
        FileLocationStore.DeleteTree(_folder);
        Assert.False(Directory.Exists(_folder));
    }
}
