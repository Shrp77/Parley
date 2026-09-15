using Parley.Cli.Tests;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Removing a file from the terminal: described, kept by default, and removed only when that is chosen.
/// </summary>
/// <remarks>
/// <c>SurfaceTests</c> holds the call to <see cref="FilesRemoving"/> and its door to that file's text; this drives the
/// door, and checks what is left in the location rather than what was said.
/// </remarks>
public sealed class FilesRemovingTests : IAsyncDisposable
{
    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-files-removing-");

    public async ValueTask DisposeAsync()
    {
        await _files.DisposeAsync();

        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Keeping_is_the_answer_under_the_cursor_and_removes_nothing()
    {
        await _files.PlaceAsync(FileLocationFixture.Billing, "notes.md", "net of tax", "pricing");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var removing = await OpenRemovalAsync(driving, shell);

        Assert.Contains("whichever agent placed them", removing.Described, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", removing.Described, StringComparison.Ordinal);
        Assert.Equal("Keep them", removing.Chosen);

        driving.Enter();
        await shell.Settled;

        Assert.Equal("Nothing was removed.", shell.Said);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task Choosing_to_remove_removes_a_file_an_agent_placed()
    {
        await _files.PlaceAsync(FileLocationFixture.Billing, "notes.md", "net of tax", "pricing");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var removing = await OpenRemovalAsync(driving, shell);

        driving.Row(removing.Choices, "Remove them");
        driving.Enter();
        await shell.Settled;

        Assert.Contains("Removed 1 file(s), freeing 10 B.", shell.Said, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    private static async Task<ChoiceDialog> OpenRemovalAsync(Driving.Session driving, Shell shell)
    {
        var list = await FilesOnScreen.ListAsync(driving, shell);

        driving.Row(list.Choices, "pricing/notes.md");
        driving.Enter();

        var acting = (ChoiceDialog)driving.Find("notes.md");

        driving.Row(acting.Choices, "Remove it");
        driving.Enter();

        return (ChoiceDialog)driving.Find(FilesRemoving.Title);
    }
}
