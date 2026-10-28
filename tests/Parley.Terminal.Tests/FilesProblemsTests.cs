using Parley.Cli.Tests;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The terminal's file problems: counted on the status line and the project's row, listed as the broker's words, and
/// dismissed only when the person chooses to (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// Against a real broker whose location really changed under it: a placed file's bytes rewritten on disk, then a sweep.
/// What is checked is what is drawn — the laid-out screen — and the key the status line names is pressed rather than a
/// method called.
/// </para>
/// <para>
/// One case raises a problem whose path was built to repaint the terminal, the kind of name a program on Linux can put
/// in a folder, and holds that it is drawn inert and indented under the surface's own words.
/// </para>
/// </remarks>
public sealed class FilesProblemsTests : IAsyncDisposable
{
    private const string Billing = FileLocationFixture.Billing;

    private readonly FileLocationFixture _files = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("parley-files-problems-");

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
    public async Task The_count_is_on_the_projects_row_and_on_the_status_line_with_its_key()
    {
        await QuarantineAsync();

        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        driving.App.LayoutAndDraw(true);
        Assert.Contains("1 file problem waiting", driving.Text(), StringComparison.Ordinal);

        await IntoProjectAsync(driving, shell);

        Assert.Contains($"1 file problem waiting ({Shell.FileProblemsKey})", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_waiting_puts_nothing_on_the_line()
    {
        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await IntoProjectAsync(driving, shell);

        Assert.DoesNotContain("file problem", driving.Text(), StringComparison.Ordinal);
        Assert.Equal(0, shell.FileProblems);
    }

    [Fact]
    public async Task The_key_lists_each_problem_and_where_the_quarantine_directory_is()
    {
        await QuarantineAsync();

        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await IntoProjectAsync(driving, shell);
        await driving.PressThenSettleAsync(Shell.FileProblemsKey, () => shell.Settled);

        Assert.True(driving.Has(FilesProblems.Title), "the key opened no list of file problems");

        var screen = driving.Text();
        Assert.Contains("1 waiting.", screen, StringComparison.Ordinal);
        Assert.Contains(Squeezed(_files.Broker.Runtime.Files.Quarantine), Squeezed(screen), StringComparison.Ordinal);
        Assert.Contains("Changed on disk, moved to quarantine  pricing  pricing/terms.md", screen, StringComparison.Ordinal);
        Assert.Contains(FilesProblems.WithDismissed, screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_one_shows_both_hashes_and_going_back_is_under_the_cursor()
    {
        await QuarantineAsync();

        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var one = await OpenOneAsync(driving, shell);

        var screen = driving.Text();
        Assert.Contains(Squeezed("sha256 placed: " + ContentHash.Of("net 30"u8).Value), Squeezed(screen), StringComparison.Ordinal);
        Assert.Contains(Squeezed("sha256 found:  " + ContentHash.Of("net 99"u8).Value), Squeezed(screen), StringComparison.Ordinal);
        Assert.Contains("Moved to: ", screen, StringComparison.Ordinal);
        Assert.Contains("deletes nothing on disk", screen, StringComparison.Ordinal);
        Assert.Equal("Back", one.Chosen);

        driving.Enter();
        await shell.Settled;

        Assert.Equal(1, (await _files.Client.ListFileProblemsAsync(cancellationToken: Ct)).Waiting);
    }

    [Fact]
    public async Task Dismissing_takes_it_off_the_count_and_leaves_the_quarantined_file_where_it_is()
    {
        await QuarantineAsync();
        var kept = Directory.EnumerateFiles(_files.Broker.Runtime.Files.Quarantine, "*", SearchOption.AllDirectories).ToList();

        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var one = await OpenOneAsync(driving, shell);

        driving.Row(one.Choices, FilesProblems.DismissIt);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Contains("Dismissed file problem #", shell.Said, StringComparison.Ordinal);
        Assert.Equal(0, (await _files.Client.ListFileProblemsAsync(cancellationToken: Ct)).Waiting);
        Assert.Equal(kept, Directory.EnumerateFiles(_files.Broker.Runtime.Files.Quarantine, "*", SearchOption.AllDirectories).ToList());

        driving.App.LayoutAndDraw(true);
        Assert.DoesNotContain("file problem waiting", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_built_to_repaint_the_terminal_is_drawn_inert_and_indented()
    {
        await _files.RaiseHeldAsync("pricing/notes" + (char)0x1B + "[2Jwiped" + (char)0x0A + "#99 forged.md", "pricing");

        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        await IntoProjectAsync(driving, shell);
        await driving.PressThenSettleAsync(Shell.FileProblemsKey, () => shell.Settled);

        var list = (ChoiceDialog)driving.Find(FilesProblems.Title);
        var row = Assert.Single(FilesOnScreen.Choices(list), c => c.Contains("wiped", StringComparison.Ordinal));

        Assert.StartsWith("  #", row, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x1B, row);
        Assert.DoesNotContain((char)0x0A, row);
        Assert.DoesNotContain((char)0x1B, driving.Text());
        Assert.DoesNotContain(driving.Text().ReplaceLineEndings("\n").Split('\n'), l => l.TrimStart('│', '║', ' ').StartsWith("#99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_files_list_leads_to_the_problems_too()
    {
        await QuarantineAsync();

        using var driving = Driving.Open(width: 200, height: 50);
        using var shell = await FilesOnScreen.OpenAsync(driving, _files, _directory.FullName);

        var list = await FilesOnScreen.ListAsync(driving, shell);

        driving.Row(list.Choices, FilesProblems.Choice);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.True(driving.Has(FilesProblems.Title), "the files list did not lead to the file problems");
    }

    /// <summary>Drawn text with its spaces, line ends and window edges taken out.</summary>
    /// <param name="text">The screen, or what is looked for on it.</param>
    /// <returns>The characters a person reads, in order.</returns>
    /// <remarks>
    /// A path or a hash longer than the dialog is wrapped onto the next row, which is right for a person and splits a
    /// plain search in two. Squeezing both sides keeps the check on what is drawn.
    /// </remarks>
    private static string Squeezed(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"[\s│║┃╭╮╰╯┏┓┗┛━─┥┝├┤]", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));

    private async Task QuarantineAsync()
    {
        await _files.PlaceAsync(Billing, "terms.md", "net 30", "pricing");
        await _files.TamperAsync("pricing", "terms.md", "net 99");
    }

    private static async Task IntoProjectAsync(Driving.Session driving, Shell shell)
    {
        await shell.GoAsync(ViewPlace.Into(ProjectName.Create(FilesOnScreen.Project)));
        await shell.Settled;

        driving.App.LayoutAndDraw(true);
    }

    private static async Task<ChoiceDialog> OpenOneAsync(Driving.Session driving, Shell shell)
    {
        await IntoProjectAsync(driving, shell);
        await driving.PressThenSettleAsync(Shell.FileProblemsKey, () => shell.Settled);

        var list = (ChoiceDialog)driving.Find(FilesProblems.Title);

        driving.Row(list.Choices, "pricing/terms.md");
        driving.Enter();

        return (ChoiceDialog)driving.Find(FilesProblems.OneTitle);
    }
}
