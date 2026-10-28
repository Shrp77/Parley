using Parley.Cli.Tests;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Placing a file from the terminal, as the person (SPEC.md §10.2 <c>place_file</c>): the conversation chosen, the path
/// asked for, and what the broker holds afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Against a real broker, through the files list a person opens. Each case checks what the location lists afterwards
/// rather than only what was said.
/// </para>
/// <para>
/// <b>A choice that queues a read is accepted, not pressed.</b> Choosing to place from the project's files reads its
/// conversations and opens the next dialog from the work queue, and a press redraws on the test's thread in the same
/// breath (CLAUDE.md, "Dialogs open on the thread that draws"); so the list is told to accept and the test waits.
/// </para>
/// </remarks>
public sealed class FilesPlacingTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-files-placing-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public async Task From_the_projects_files_the_conversation_is_chosen_and_the_file_is_placed_as_the_persons()
    {
        await _files.PlaceAsync(Billing, "rates.md", "twenty percent", "pricing");
        var path = Write("notes.md", "net of tax");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var list = await FilesOnScreen.ListAsync(driving, shell);

        Assert.Equal("Close", FilesOnScreen.Choices(list)[0]);
        Assert.Contains(FilesPlacing.Choice, driving.Text(), StringComparison.Ordinal);

        await AcceptAsync(driving, shell, list, FilesPlacing.Choice);

        var choosing = (ChoiceDialog)driving.Find(FilesPlacing.Title);

        Assert.Equal("Back", choosing.Chosen);
        Assert.Contains("Which conversation does the file go in?", driving.Text(), StringComparison.Ordinal);
        Assert.Contains("  pricing", driving.Text(), StringComparison.Ordinal);

        driving.Row(choosing.Choices, "pricing");
        driving.Enter();

        var form = (FormDialog)driving.Find(FilesPlacing.Title + " in pricing");

        Assert.Contains("path on this machine", driving.Text(), StringComparison.Ordinal);

        form.Fill(0, path);
        driving.Enter();
        await shell.Settled;

        Assert.Contains("Placed notes.md in pricing", shell.Said, StringComparison.Ordinal);

        var placed = await PlacedAsync("notes.md");
        Assert.Null(placed.PlacedBy);
        Assert.Equal(ContentHash.Of("net of tax"u8), placed.Sha256);
    }

    [Fact]
    public async Task From_a_conversations_files_the_form_opens_for_that_conversation_and_a_name_given_is_used()
    {
        await _files.PlaceAsync(Billing, "rates.md", "twenty percent", "pricing");
        var path = Write("draft.md", "net of tax");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await ConversationFilesAsync(driving, shell);

        var list = (ChoiceDialog)driving.Find(Files.ConversationTitle);

        driving.Row(list.Choices, FilesPlacing.Choice);
        driving.Enter();

        var form = (FormDialog)driving.Find(FilesPlacing.Title + " in pricing");

        form.Fill(0, "\"" + path + "\"");
        form.Fill(1, "final.md");
        driving.Enter();
        await shell.Settled;

        Assert.Contains("Placed final.md in pricing", shell.Said, StringComparison.Ordinal);
        Assert.NotNull(await PlacedAsync("final.md"));
    }

    [Fact]
    public async Task A_path_with_no_file_says_so_under_the_field_as_it_is_typed_and_nothing_is_placed()
    {
        await _files.PlaceAsync(Billing, "rates.md", "twenty percent", "pricing");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await ConversationFilesAsync(driving, shell);

        var list = (ChoiceDialog)driving.Find(Files.ConversationTitle);

        driving.Row(list.Choices, FilesPlacing.Choice);
        driving.Enter();

        var form = (FormDialog)driving.Find(FilesPlacing.Title + " in pricing");

        Assert.Same(form.Field(0), form.MostFocused);

        driving.Type(Path.Combine(_directory.FullName, "absent.md"));

        var screen = driving.Text();
        Assert.True(screen.Contains("There is no file at that path.", StringComparison.Ordinal), screen);

        driving.Enter();
        await shell.Settled;

        Assert.Equal("Nothing was placed.", shell.Said);
        Assert.Equal(1, await _files.CountAsync());
    }

    [Fact]
    public async Task Other_content_under_a_name_already_placed_is_refused_in_the_brokers_words()
    {
        await _files.PlaceAsync(Billing, "notes.md", "theirs", "pricing");
        var path = Write("notes.md", "mine");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await ConversationFilesAsync(driving, shell);

        var list = (ChoiceDialog)driving.Find(Files.ConversationTitle);

        driving.Row(list.Choices, FilesPlacing.Choice);
        driving.Enter();

        var form = (FormDialog)driving.Find(FilesPlacing.Title + " in pricing");

        form.Fill(0, path);
        driving.Enter();
        await shell.Settled;

        Assert.StartsWith("Not placed:", shell.Said, StringComparison.Ordinal);
        Assert.Equal(AgentName.Create(Billing), (await PlacedAsync("notes.md")).PlacedBy);
    }

    private static async Task AcceptAsync(Driving.Session driving, Shell shell, ChoiceDialog dialog, string row)
    {
        driving.Row(dialog.Choices, row);
        dialog.Choices.InvokeCommand(Command.Accept);
        await shell.Settled;
    }

    private static async Task ConversationFilesAsync(Driving.Session driving, Shell shell)
    {
        await shell.GoAsync(ViewPlace.Into(ProjectName.Create(FilesOnScreen.Project)));
        await shell.Settled;

        driving.Row(shell.Conversations.List, "pricing");

        var pane = Enumerable.Range(0, 4).FirstOrDefault(
            at =>
            {
                shell.Focus(at);
                return shell.Actions().Any(a => a.Label == Files.ConversationTitle);
            },
            -1);

        Assert.True(pane >= 0, "no pane of the project view offers a conversation's files");

        await shell.Actions().Single(a => a.Label == Files.ConversationTitle).Run();
        await shell.Settled;
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory.FullName, name);

        File.WriteAllText(path, text);

        return path;
    }

    private async Task<SharedFile> PlacedAsync(string name)
    {
        var listing = await _files.Client.ListSharedFilesAsync(null, FileLimits.MaximumPageSize, null, Ct);

        return Assert.Single(listing.Page.Files, f => f.Name.Value == name);
    }
}
