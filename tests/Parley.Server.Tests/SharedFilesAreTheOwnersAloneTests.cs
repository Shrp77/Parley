using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That on Unix the file location, its staging and a placed file are the owner's alone, as the store beside them is.
/// </summary>
/// <remarks>
/// The store and its sidecars are made owner-only so another local account cannot read the conversations; the shared files
/// are the same agents' hand-offs, and were left readable by any account that could list the home folder (lens 2 run 93).
/// </remarks>
[Integration]
public sealed class SharedFilesAreTheOwnersAloneTests : IDisposable
{
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-owners-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var path in new[] { _root, _root + "-incoming", _root + "-quarantine" })
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task The_location_its_staging_and_a_placed_file_are_readable_by_their_owner_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows keeps the folder's inherited ACL, as the store does.");
            return;
        }

        // An older broker's root, left readable by everyone, is tightened when the next one prepares it.
        _ = Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, File.GetUnixFileMode(_root) | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        using var location = new FileLocationStore(_root, _root + "-incoming");
        location.Prepare();

        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(_root) & GroupOrOther);
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(_root + "-incoming") & GroupOrOther);

        await using (var staged = location.Stage())
        {
            await staged.WriteAsync("net of tax"u8.ToArray(), Ct);
            _ = await staged.FinishAsync(Ct);
            _ = staged.CommitTo("pricing", SharedFileName.Create("notes.md"));
        }

        var placed = Path.Combine(_root, "pricing", "notes.md");
        Assert.True(File.Exists(placed));
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(placed) & GroupOrOther);
    }
}
