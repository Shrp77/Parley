using System.Globalization;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The terminal's Files dialog: every file in a project, read whole across pages, with who placed how much
/// (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// Against a real broker with files placed over the wire, driven through the key the status line offers — the route a
/// person takes, rather than a method only a test could call.
/// </remarks>
public sealed class FilesDialogTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-files-dialog-");

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
    public async Task Each_file_is_an_indented_row_and_the_description_says_how_much_and_who_placed_it()
    {
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");
        await _files.PlaceAsync(Billing, "rates.txt", "twenty percent", "pricing");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var list = await FilesOnScreen.ListAsync(driving, shell);

        Assert.Contains("2 file(s), 24 B", list.Described, StringComparison.Ordinal);
        Assert.Contains("Placed so far:", list.Described, StringComparison.Ordinal);
        Assert.Contains(Billing + ": 2 placement(s), 24 B", list.Described, StringComparison.Ordinal);
        Assert.Contains(Parley.Client.Rendering.LocalTime.Note(), list.Described, StringComparison.Ordinal);

        var rows = FilesOnScreen.Rows(list);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.StartsWith("  ", row, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("pricing/notes.md", StringComparison.Ordinal)
                                     && row.Contains(ContentHash.Of("net of tax"u8).Short, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_location_larger_than_a_page_is_listed_whole()
    {
        for (var i = 0; i < 250; i++)
        {
            await _files.PlaceAsync(Billing, "f" + i.ToString("000", CultureInfo.InvariantCulture) + ".md", "x", "pricing");
        }

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var list = await FilesOnScreen.ListAsync(driving, shell);

        Assert.Equal(250, FilesOnScreen.Rows(list).Count);
        Assert.Contains("250 file(s)", list.Described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_location_says_so_and_offers_nothing_to_remove()
    {
        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var list = await FilesOnScreen.ListAsync(driving, shell);

        Assert.Contains("The file location is empty.", list.Described, StringComparison.Ordinal);
        Assert.DoesNotContain(FilesOnScreen.Choices(list), c => c.StartsWith("Remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conversation_holding_only_files_is_listed_its_files_are_offered_and_its_transcript_opens()
    {
        // A placement by an agent holding no request starts a conversation with no message in it. The conversation list
        // hid every conversation without a message until files existed, so this is the one a person would not find.
        await _files.PlaceAsync(Billing, "notes.md", "net of tax", "pricing");

        using var driving = Driving.Open();
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await shell.GoAsync(ViewPlace.Into(ProjectName.Create(FilesOnScreen.Project)));
        await shell.Settled;

        Assert.Contains(shell.Conversations.Conversations, c => c.Id == "pricing");

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

        var list = (ChoiceDialog)driving.Find(Files.ConversationTitle);
        Assert.Contains(FilesOnScreen.Rows(list), row => row.Contains("pricing/notes.md", StringComparison.Ordinal));

        driving.Escape();

        var said = shell.Said;

        Assert.True(shell.Conversations.OpenSelected(), "the conversation could not be opened");
        await shell.Settled;

        Assert.DoesNotContain("xception", shell.Said ?? string.Empty, StringComparison.Ordinal);
        Assert.True(shell.Said == said || !(shell.Said ?? string.Empty).Contains("could not", StringComparison.OrdinalIgnoreCase), shell.Said);
    }
}

/// <summary>
/// Opening the terminal on a project whose broker holds files, and reaching its Files dialog.
/// </summary>
internal static class FilesOnScreen
{
    /// <summary>The project's name.</summary>
    internal const string Project = "alpha";

    /// <summary>Opens the surface on a manifest naming one project served by the fixture's broker.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="files">The broker and its files.</param>
    /// <param name="directory">Where the manifest goes.</param>
    /// <returns>The surface, with its projects read.</returns>
    internal static async Task<Shell> OpenAsync(Driving.Session driving, FileLocationFixture files, string directory)
    {
        var manifest = directory;

        ProjectManifest.Write(
            manifest,
            [new ProjectSettings(ProjectName.Create(Project), Path.Combine(directory, "alpha.db"), files.Broker.Address, null)]);

        var shell = TerminalCommand.Build(BrokerDirectory.FromManifest(manifest), manifestPath: manifest);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }

    /// <summary>Takes the Files action on the projects pane and returns the dialog it opens.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="shell">The surface.</param>
    /// <returns>The dialog.</returns>
    /// <remarks>
    /// <para>
    /// The action the status line offers, run and then waited on, rather than its letter pressed. <c>ActionsKeyTests</c>
    /// already holds a letter to its action.
    /// </para>
    /// <para>
    /// <b>Pressing it raced.</b> The list opens once the broker has answered, from the surface's work queue, and
    /// <see cref="Driving.Session.Press"/> redraws the screen straight after the key on the test's own thread: two
    /// draws at once, and a list drawn while the other laid it out threw out of range in two runs of nine.
    /// <c>ShowHealth</c> opens its dialog the same way and no test presses its key either; that the queue opens dialogs
    /// off the draw thread at all is recorded in the review ledger, not settled here.
    /// </para>
    /// </remarks>
    internal static async Task<ChoiceDialog> ListAsync(Driving.Session driving, Shell shell)
    {
        shell.Focus(0);

        await shell.Actions().Single(a => a.Label == Files.ProjectTitle).Run();
        await shell.Settled;

        return (ChoiceDialog)driving.Find(Files.ProjectTitle);
    }

    /// <summary>Every choice the dialog offers, as drawn.</summary>
    /// <param name="dialog">The dialog.</param>
    /// <returns>The choices.</returns>
    internal static List<string> Choices(ChoiceDialog dialog) =>
        dialog.Choices.Source is { } source
            ? [.. source.ToList().Cast<object?>().Select(c => c?.ToString() ?? string.Empty)]
            : [];

    /// <summary>The choices that are files.</summary>
    /// <param name="dialog">The dialog.</param>
    /// <returns>The rows.</returns>
    internal static List<string> Rows(ChoiceDialog dialog) =>
        [.. Choices(dialog).Where(c => c.Contains('/', StringComparison.Ordinal))];

    /// <summary>How many files the location holds, asked of the broker.</summary>
    /// <param name="files">The broker.</param>
    /// <returns>The count.</returns>
    internal static Task<int> CountAsync(FileLocationFixture files) => files.CountAsync();
}
