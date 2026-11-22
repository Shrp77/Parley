using Parley.Cli.Tests;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// A conversation's Media in the terminal, and its placements drawn in its transcript (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Against a real broker with files placed over the wire, reached through the action the status line offers.
/// </remarks>
[Integration]
[Slow]
public sealed class MediaTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-media-");

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

    private static async Task<Shell> IntoTheConversationAsync(Driving.Session driving, FileLocationFixture files, string directory)
    {
        var shell = await FilesOnScreen.OpenAsync(driving, files, directory);

        await shell.GoAsync(ViewPlace.Into(ProjectName.Create(FilesOnScreen.Project)));
        await shell.Settled;

        driving.Row(shell.Conversations.List, "pricing");

        return shell;
    }

    [Fact]
    public async Task Media_lists_images_before_other_files_newest_first_says_it_cannot_draw_them_and_opens_one()
    {
        await _files.PlaceAsync(Billing, "older.png", "first", "pricing");
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        // Real leading bytes: an image whose first bytes are not its format is never launched (0.58.1).
        await _files.PlaceAsync(Billing, "newer.gif", "GIF89a-second", "pricing");

        using var driving = Driving.Open();
        using var shell = await IntoTheConversationAsync(driving, _files, _directory.FullName);

        var pane = Enumerable.Range(0, 4).FirstOrDefault(
            at =>
            {
                shell.Focus(at);
                return shell.Actions().Any(a => a.Label == Media.Title);
            },
            -1);

        Assert.True(pane >= 0, "no pane of the project view offers a conversation's media");

        await shell.Actions().Single(a => a.Label == Media.Title).Run();
        await shell.Settled;

        var list = (ChoiceDialog)driving.Find(Media.Title);
        var rows = FilesOnScreen.Choices(list).Where(c => c.StartsWith("  [", StringComparison.Ordinal)).ToList();

        Assert.Contains(Media.CannotDraw, list.Described, StringComparison.Ordinal);
        Assert.Contains("2 image(s) and 1 other file(s)", list.Described, StringComparison.Ordinal);
        Assert.Equal(3, rows.Count);
        Assert.Contains("[image] newer.gif", rows[0], StringComparison.Ordinal);
        Assert.Contains("[image] older.png", rows[1], StringComparison.Ordinal);
        Assert.Contains("[text] notes.md", rows[2], StringComparison.Ordinal);

        driving.Row(list.Choices, "[image] newer.gif");
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var path = Assert.Single(driving.Launched);
        Assert.Equal("GIF89a-second", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_conversation_with_exactly_one_image_says_the_terminal_cannot_draw_it()
    {
        await _files.PlaceAsync(Billing, "only.gif", "GIF89a-only", "pricing");

        using var driving = Driving.Open();
        using var shell = await IntoTheConversationAsync(driving, _files, _directory.FullName);

        var pane = Enumerable.Range(0, 4).FirstOrDefault(
            at =>
            {
                shell.Focus(at);
                return shell.Actions().Any(a => a.Label == Media.Title);
            },
            -1);

        Assert.True(pane >= 0, "no pane of the project view offers a conversation's media");

        await shell.Actions().Single(a => a.Label == Media.Title).Run();
        await shell.Settled;

        var list = (ChoiceDialog)driving.Find(Media.Title);

        Assert.Contains("1 image(s) and 0 other file(s)", list.Described, StringComparison.Ordinal);
        Assert.Contains(Media.CannotDraw, list.Described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_the_second_item_opens_the_second_item()
    {
        // Without the guard every choice opened the first item listed, whichever the person picked (review, lens 3 round 34).
        // Both real images, so either may be launched (0.58.1); the older is listed second.
        await _files.PlaceAsync(Billing, "older.gif", "GIF89a-first", "pricing");
        await _files.PlaceAsync(Billing, "newer.gif", "GIF89a-second", "pricing");

        using var driving = Driving.Open();
        using var shell = await IntoTheConversationAsync(driving, _files, _directory.FullName);

        var pane = Enumerable.Range(0, 4).FirstOrDefault(
            at =>
            {
                shell.Focus(at);
                return shell.Actions().Any(a => a.Label == Media.Title);
            },
            -1);

        Assert.True(pane >= 0, "no pane of the project view offers a conversation's media");

        await shell.Actions().Single(a => a.Label == Media.Title).Run();
        await shell.Settled;

        var list = (ChoiceDialog)driving.Find(Media.Title);
        var rows = FilesOnScreen.Choices(list).Where(c => c.StartsWith("  [", StringComparison.Ordinal)).ToList();

        Assert.Contains("[image] newer.gif", rows[0], StringComparison.Ordinal);
        Assert.Contains("[image] older.gif", rows[1], StringComparison.Ordinal);

        driving.Row(list.Choices, "[image] older.gif");
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var path = Assert.Single(driving.Launched);
        Assert.Equal("GIF89a-first", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Choosing_Close_or_pressing_Escape_opens_nothing_and_leaves_the_list()
    {
        // Close is the first choice and is no file, and Escape chooses nothing: neither may be read as a row of the list.
        await _files.PlaceAsync(Billing, "only.gif", "GIF89a-only", "pricing");

        using var driving = Driving.Open();
        using var shell = await IntoTheConversationAsync(driving, _files, _directory.FullName);

        var pane = Enumerable.Range(0, 4).FirstOrDefault(
            at =>
            {
                shell.Focus(at);
                return shell.Actions().Any(a => a.Label == Media.Title);
            },
            -1);

        Assert.True(pane >= 0, "no pane of the project view offers a conversation's media");

        await shell.Actions().Single(a => a.Label == Media.Title).Run();
        await shell.Settled;

        var list = (ChoiceDialog)driving.Find(Media.Title);

        Assert.Equal("Close", FilesOnScreen.Choices(list)[0]);

        driving.Row(list.Choices, "Close");
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.False(driving.Has(Media.Title), "the list is still open after Close");
        Assert.Empty(driving.Launched);

        await shell.Actions().Single(a => a.Label == Media.Title).Run();
        await shell.Settled;

        _ = driving.Find(Media.Title);

        await driving.PressThenSettleAsync(Key.Esc, () => shell.Settled);

        Assert.False(driving.Has(Media.Title), "the list is still open after Escape");
        Assert.Empty(driving.Launched);
        Assert.DoesNotContain("xception", shell.Said ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_with_no_files_says_so_and_offers_only_Close()
    {
        Assert.Equal("There are no files in this conversation.", Media.Describe("pricing", [], []));

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var said = new List<string>();

        // The list as the surface opens it, for a conversation the broker holds nothing in.
        await Media.OpenAsync(
            driving.App, (Parley.Client.BrokerClient)_files.Client, "nothing-placed", shell.FileLauncher, shell.Queued, words =>
            {
                said.Add(words);

                return Task.CompletedTask;
            });
        await shell.Settled;

        var list = (ChoiceDialog)driving.Find(Media.Title);

        Assert.Equal("There are no files in this conversation.", list.Described);
        Assert.Equal(["Close"], FilesOnScreen.Choices(list));

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.False(driving.Has(Media.Title), "the list is still open after Close");
        Assert.Empty(driving.Launched);
        Assert.Empty(said);
    }

    [Fact]
    public async Task The_transcript_draws_each_placement_as_a_line_of_its_own()
    {
        await _files.PlaceAsync(Billing, "mockup.png", "an image", "pricing");

        using var driving = Driving.Open();
        using var shell = await IntoTheConversationAsync(driving, _files, _directory.FullName);

        Assert.True(shell.Conversations.OpenSelected(), "the conversation could not be opened");
        await shell.Settled;

        var line = Assert.Single(shell.Transcript.Drawn, l => l.Contains("placed mockup.png", StringComparison.Ordinal));

        Assert.Contains(Billing + " placed mockup.png (image, 8 B) at ", line, StringComparison.Ordinal);
        Assert.StartsWith(" ", line, StringComparison.Ordinal);
    }
}
