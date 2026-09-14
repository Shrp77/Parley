using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Top;

/// <summary>
/// Walking the machine's folders to find the repository being added.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner, who was typing a path for every agent.</b> Adding nineteen
/// repositories meant typing nineteen absolute paths correctly, into a field that could only say
/// afterwards that one of them was not a directory.
/// </para>
/// <para>
/// <b>Built from this surface's own list rather than the toolkit's file dialog.</b>
/// <see cref="Terminal.Gui.Views.OpenDialog"/> exists and browses a filesystem perfectly well, and
/// two things are worth more than the code it would save. It runs itself modally, which this
/// surface deliberately does not do — every dialog here is a session the caller drives, because
/// that is what makes a key pressable by a test. And it browses files, where what is being looked
/// for is a repository: the one thing this shows that a file browser cannot is which of these
/// folders holds a <c>.git</c>, which is exactly the question somebody adding an agent is asking.
/// </para>
/// <para>
/// <b>It does not replace the field.</b> What is chosen here fills the form's first field in, and
/// that field stays typed into and pasted into — somebody at the end of an SSH connection who
/// knows the path should not have to walk to it.
/// </para>
/// </remarks>
internal sealed class Browsing : Dialog
{
    /// <summary>What the dialog is called.</summary>
    internal const string Name = "Find the repository";

    /// <summary>The row that takes the folder currently open.</summary>
    internal const string Use = "[ add this repository ]";

    /// <summary>The row that goes up one.</summary>
    internal const string Up = "[ up one folder ]";

    /// <summary>What marks a folder that is a repository.</summary>
    /// <remarks>
    /// <b>A prefix inside the indent, and it was a suffix.</b> A folder name is somebody else's
    /// text — on most filesystems it may hold nearly anything — so a name ending in <c>— a
    /// repository</c> read as one, and the row said something about the folder that was not true.
    /// The same structural answer the transcript reached in 0.18.6: what a row means is decided by
    /// where it sits, not by what it happens to end with.
    /// </remarks>
    internal const string Repository = "* ";

    /// <summary>What a folder row that is not a repository is indented with instead.</summary>
    internal const string Plain = "  ";

    /// <summary>What every folder row starts with, and no action row does.</summary>
    /// <remarks>
    /// <b>Only an action sits at column zero.</b> <see cref="Use"/> and <see cref="Up"/> are this
    /// dialog's own words; every other row is a name off the filesystem. Indenting the names is
    /// what stops a folder called <c>[ add this repository ]</c> from reading as the row that adds
    /// one — the rule <c>ColumnZeroTests</c> holds the transcript to, in the other place this
    /// surface draws somebody else's text in a list.
    /// </remarks>
    internal const string Indent = "  ";

    /// <summary>What is said when a folder cannot be listed.</summary>
    internal const string Unreadable = "This folder cannot be read from here.";

    private readonly GuardedList _list = Lists.Filling();
    private readonly Label _where;
    private readonly Label _said;
    private readonly List<Step> _steps = [];

    /// <summary>One row, and what pressing it does.</summary>
    /// <param name="Row">What it reads.</param>
    /// <param name="Goes">Where it goes, or null when it takes the folder that is open.</param>
    private sealed record Step(string Row, string? Goes);

    internal Browsing(string opening)
    {
        Title = Name;
        Width = Dim.Percent(80);
        Height = Dim.Percent(80);

        _where = new Plain { X = 1, Y = 0, Width = Dim.Fill(2), Height = 1 };
        _said = new Plain { X = 1, Y = 1, Width = Dim.Fill(2), Height = 1, Text = string.Empty };

        _list.X = 1;
        _list.Y = 2;
        _list.Width = Dim.Fill(2);
        _list.Height = Dim.Fill(1);

        _list.Accepting += (_, args) =>
        {
            args.Handled = true;
            Take();
        };

        Add(_where, _said, _list);

        // **A folder that cannot be opened leaves this with nowhere to go**, which the review gate
        // found by pointing it at one that does not exist: no path, no rows, one sentence, and no
        // row to press to get anywhere else. The home directory is the answer for the same reason
        // it is the answer on a first run, and the machine's root after that — somewhere always
        // opens, and being somewhere is what makes the rest of the dialog work.
        foreach (var folder in new[] { opening, Opening(null), Path.GetPathRoot(opening) })
        {
            if (folder is { Length: > 0 })
            {
                Show(folder);
            }

            if (At.Length > 0)
            {
                return;
            }
        }
    }

    /// <summary>Raised with the repository chosen, or null when nothing was.</summary>
    internal event EventHandler<string?>? Done;

    /// <summary>The folder currently open.</summary>
    internal string At { get; private set; } = string.Empty;

    /// <summary>The list, so a test can press a row.</summary>
    internal GuardedList List => _list;

    /// <summary>What it last had to say about a folder.</summary>
    internal string Said => _said.Text;

    /// <summary>Where to start looking.</summary>
    /// <param name="last">The last repository added in this session, or null when none was.</param>
    /// <returns>The folder to open.</returns>
    /// <remarks>
    /// <b>The parent of the last one, because repositories sit beside each other.</b> Somebody
    /// adding a second agent is almost always adding the repository next to the first, and starting
    /// them back at their home directory each time is the walk this exists to remove. Home is the
    /// answer for the first one, since nothing yet says where they keep their work.
    /// </remarks>
    internal static string Opening(string? last)
    {
        if (last is { Length: > 0 } && Directory.Exists(last))
        {
            var above = Path.GetDirectoryName(Path.GetFullPath(last));

            if (above is { Length: > 0 } && Directory.Exists(above))
            {
                return above;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A machine with no home directory is not a machine anybody is enrolling from, but the
        // fallback costs one expression and the alternative is an empty dialog.
        return home is { Length: > 0 } && Directory.Exists(home) ? home : Directory.GetCurrentDirectory();
    }

    /// <summary>Whether a folder is a repository.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether it holds a .git.</returns>
    /// <remarks>
    /// The same test <see cref="Adding.WorkspaceProblem"/> makes, and deliberately: a folder this
    /// offers and that refuses would be a screen arguing with itself. A worktree holds a
    /// <c>.git</c> file rather than a directory, so both count.
    /// </remarks>
    internal static bool IsRepository(string folder)
    {
        var git = Path.Combine(folder, ".git");

        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>Opens a folder, as if a row for it had been pressed.</summary>
    /// <param name="folder">The folder.</param>
    /// <remarks>
    /// The seam <see cref="Choose"/> is. What it reaches that a keypress cannot is the folder that
    /// will not be read — a permission a test cannot arrange portably, and the case where getting
    /// it wrong leaves somebody in an empty list with nothing said.
    /// </remarks>
    internal void Walk(string folder) => Show(folder);

    /// <summary>Opens a folder and lists what is in it.</summary>
    /// <param name="folder">The folder.</param>
    private void Show(string folder)
    {
        var full = Path.GetFullPath(folder);

        string[] inside;

        try
        {
            inside = Directory.GetDirectories(full);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            // Said and stayed where it was. Walking into a folder that cannot be read and leaving
            // the person in an empty list is a screen that looks broken.
            _said.Text = Unreadable;

            return;
        }

        At = full;
        _where.Text = Safe.Text(full);
        _said.Text = string.Empty;
        _steps.Clear();

        if (IsRepository(full))
        {
            _steps.Add(new Step(Use, null));
        }

        if (Path.GetDirectoryName(full) is { Length: > 0 } above)
        {
            _steps.Add(new Step(Up, above));
        }

        foreach (var child in inside.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Hidden(child))
            {
                continue;
            }

            var name = Path.GetFileName(child);

            _steps.Add(new Step(
                Indent + (IsRepository(child) ? Repository : Plain) + Safe.Text(name), child));
        }

        _list.Replace([.. _steps.Select(step => step.Row)], _steps.Count > 0 ? 0 : null);
    }

    /// <summary>Whether a folder is one to leave out of the list.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether to skip it.</returns>
    /// <remarks>
    /// A dot folder and a hidden one, which on a developer's machine is most of what is in a home
    /// directory and none of what is being looked for. <c>.git</c> itself is the clearest case:
    /// offering to walk into the thing that makes the folder above it a repository is offering a
    /// wrong answer.
    /// </remarks>
    private static bool Hidden(string folder)
    {
        var name = Path.GetFileName(folder);

        if (name.StartsWith('.'))
        {
            return true;
        }

        try
        {
            return File.GetAttributes(folder).HasFlag(FileAttributes.Hidden);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            // Shown rather than hidden. A folder whose attributes cannot be read is one this knows
            // nothing about, and leaving it out would be deciding on no evidence.
            return false;
        }
    }

    /// <summary>Answers with a folder, as if it had been walked to and chosen.</summary>
    /// <param name="folder">The repository.</param>
    /// <remarks>
    /// The seam <see cref="FormDialog.Finish"/> is, and for the same reason: a test that already
    /// knows the path should not have to walk a real filesystem from a home directory to reach it,
    /// and the walk has tests of its own that press every key it offers.
    /// </remarks>
    internal void Choose(string folder) => Done?.Invoke(this, folder);

    /// <summary>Acts on whichever row the cursor is on.</summary>
    private void Take()
    {
        if (_list.SelectedItem is not { } at || at < 0 || at >= _steps.Count)
        {
            return;
        }

        if (_steps[at].Goes is { } folder)
        {
            Show(folder);

            return;
        }

        Done?.Invoke(this, At);
    }

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key == Key.Esc)
        {
            Done?.Invoke(this, null);

            return true;
        }

        return base.OnKeyDown(key);
    }
}
