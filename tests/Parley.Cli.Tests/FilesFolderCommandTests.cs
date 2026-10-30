using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley files folder</c>: a conversation's folder, or the whole location, opened in the file browser on the machine
/// that holds it (SPEC.md §10.2).
/// </summary>
[Integration]
public sealed class FilesFolderCommandTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();

    public ValueTask DisposeAsync() => _files.DisposeAsync();

    [Fact]
    public async Task A_conversations_folder_is_opened_and_holds_its_files()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        var (code, _, _) = await _files.RunAsync("folder", "--conversation", "pricing");

        Assert.Equal(0, code);

        var (path, folder) = Assert.Single(_files.Launched);
        Assert.True(folder);
        Assert.True(File.Exists(Path.Combine(path, "notes.md")), path);
    }

    [Fact]
    public async Task With_no_conversation_named_the_whole_location_is_opened()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        await _files.RunAsync("folder", "--conversation", "pricing");
        var conversationFolder = Assert.Single(_files.Launched).Path;
        _files.Launched.Clear();

        var (code, _, _) = await _files.RunAsync("folder");

        Assert.Equal(0, code);

        var (path, folder) = Assert.Single(_files.Launched);
        Assert.True(folder);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(Path.GetDirectoryName(conversationFolder)!));
    }

    [Fact]
    public async Task A_conversation_with_no_files_opens_nothing()
    {
        var (code, _, error) = await _files.RunAsync("folder", "--conversation", "nothing-here");

        Assert.Equal(1, code);
        Assert.Contains("That conversation has no files.", error, StringComparison.Ordinal);
        Assert.Empty(_files.Launched);
    }

    [Fact]
    public async Task A_folder_on_another_machine_is_described_and_nothing_is_launched()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        _files.Machine = "elsewhere";

        var (_, output, _) = await _files.RunAsync("folder", "--conversation", "pricing");

        Assert.Empty(_files.Launched);
        Assert.Contains("The files are on machine " + TestMachine.Name, output, StringComparison.Ordinal);
    }
}
