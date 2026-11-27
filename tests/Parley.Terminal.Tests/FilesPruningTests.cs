using Terminal.Gui.Input;
using Parley.Cli.Tests;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Removing every file from the terminal: described, kept by default, and done only with the project's name typed
/// back.
/// </summary>
[Integration]
public sealed class FilesPruningTests : IAsyncDisposable
{
    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-files-pruning-");

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
    public async Task Keeping_is_first_and_a_wrong_name_removes_nothing()
    {
        await PlaceTwoAsync();

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var pruning = await OpenPruneAsync(driving, shell);

        Assert.Contains("2 file(s), 24 B", pruning.Described, StringComparison.Ordinal);
        Assert.Equal("Keep them", pruning.Chosen);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal("Nothing was removed.", shell.Said);

        pruning = await OpenPruneAsync(driving, shell);
        var typing = ChooseToPrune(driving, pruning);

        driving.Type("alhpa");
        typing.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("not this project's name", shell.Said, StringComparison.Ordinal);
        Assert.Equal(2, await _files.CountAsync());
    }

    [Fact]
    public async Task The_projects_name_typed_back_removes_every_file()
    {
        await PlaceTwoAsync();

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var typing = ChooseToPrune(driving, await OpenPruneAsync(driving, shell));

        driving.Type(FilesOnScreen.Project);
        typing.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("Removed 2 file(s), freeing 24 B.", shell.Said, StringComparison.Ordinal);
        Assert.Equal(0, await _files.CountAsync());
    }

    private async Task PlaceTwoAsync()
    {
        await _files.PlaceAsync(FileLocationFixture.Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(FileLocationFixture.Billing, "rates.txt", "twenty percent", "returns");
    }

    private static async Task<ChoiceDialog> OpenPruneAsync(Driving.Session driving, Shell shell)
    {
        var list = await FilesOnScreen.ListAsync(driving, shell);

        driving.Row(list.Choices, "Remove every file");
        driving.Enter();

        return (ChoiceDialog)driving.Find(FilesPruning.Title);
    }

    private static TextDialog ChooseToPrune(Driving.Session driving, ChoiceDialog pruning)
    {
        driving.Row(pruning.Choices, "cannot be undone");
        driving.Enter();

        return (TextDialog)driving.Find(FilesPruning.Title);
    }
}
