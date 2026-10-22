namespace Parley.Setup;

/// <summary>
/// What a repository picker is showing, and what each keystroke does to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolling is opt-in, and this is what makes that cheap.</b> Scanning a directory finds
/// every checkout under it, and most of them are usually not meant to be agents. Every row starts
/// unticked, so a repository is enrolled only by somebody choosing it.
/// </para>
/// <para>
/// <b>A state machine rather than a read loop</b> for the same reason
/// <see cref="EnrollmentPlan"/> is a pure function: a test cannot drive a terminal, and the
/// defects in a picker are all in what a keystroke does to the selection rather than in reading
/// the keystroke. Everything here is decided from a key and a state; <see cref="ConsolePicker"/>
/// is the twenty lines that own a console.
/// </para>
/// </remarks>
public sealed record RepositorySelection
{
    private RepositorySelection(
        IReadOnlyList<SelectableRepository> repositories,
        int cursor,
        SelectionStatus status)
    {
        Repositories = repositories;
        Cursor = cursor;
        Status = status;
    }

    /// <summary>The repositories on offer, in the order they are shown.</summary>
    public IReadOnlyList<SelectableRepository> Repositories { get; }

    /// <summary>Which row the cursor is on.</summary>
    public int Cursor { get; }

    /// <summary>Whether the person is still choosing, has confirmed, or has cancelled.</summary>
    public SelectionStatus Status { get; }

    /// <summary>How many repositories are currently ticked.</summary>
    public int SelectedCount => Repositories.Count(r => r.Selected);

    /// <summary>
    /// Starts a picker over a list of repositories.
    /// </summary>
    /// <param name="repositories">What was found, with whatever is already ticked.</param>
    /// <returns>The initial state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="repositories"/> is null.</exception>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    public static RepositorySelection Over(IReadOnlyList<SelectableRepository> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);

        if (repositories.Count == 0)
        {
            throw new ArgumentException("A picker needs something to pick from.", nameof(repositories));
        }

        return new RepositorySelection(repositories, 0, SelectionStatus.Choosing);
    }

    /// <summary>
    /// Applies one keystroke.
    /// </summary>
    /// <param name="key">The key pressed.</param>
    /// <returns>The state afterwards, which may be the same one.</returns>
    /// <remarks>
    /// Unrecognised keys change nothing rather than cancelling or confirming. A picker that
    /// treats an unexpected key as agreement is one that enrolls repositories because somebody
    /// leaned on the keyboard, and the whole point of this screen is that enrolling is deliberate.
    /// </remarks>
    public RepositorySelection Press(ConsoleKey key) => key switch
    {
        ConsoleKey.UpArrow or ConsoleKey.K => Move(-1),
        ConsoleKey.DownArrow or ConsoleKey.J => Move(+1),
        ConsoleKey.Home => With(cursor: 0),
        ConsoleKey.End => With(cursor: Repositories.Count - 1),
        ConsoleKey.Spacebar => Toggle(Cursor),
        ConsoleKey.A => SetAll(true),
        ConsoleKey.N => SetAll(false),
        ConsoleKey.Enter => With(status: SelectionStatus.Confirmed),
        ConsoleKey.Escape or ConsoleKey.Q => With(status: SelectionStatus.Cancelled),
        _ => this,
    };

    /// <summary>
    /// Ticks or unticks one row.
    /// </summary>
    /// <param name="index">The row to change.</param>
    /// <returns>The state afterwards.</returns>
    public RepositorySelection Toggle(int index)
    {
        if (index < 0 || index >= Repositories.Count)
        {
            return this;
        }

        var next = Repositories.ToArray();
        next[index] = next[index] with { Selected = !next[index].Selected };

        return new RepositorySelection(next, Cursor, Status);
    }

    /// <summary>
    /// Renders the picker as lines of text.
    /// </summary>
    /// <param name="width">How wide the terminal is, used to keep rows from wrapping.</param>
    /// <returns>One string per line, cursor row first-marked.</returns>
    /// <remarks>
    /// Returned rather than printed so the layout is testable. A row that wraps turns the list
    /// into a scrolling mess the moment a path is long, which is the ordinary case.
    /// </remarks>
    public IReadOnlyList<string> Render(int width = 100)
    {
        var lines = new List<string>
        {
            "Which repositories should become Parley agents?",
            string.Empty,
        };

        for (var i = 0; i < Repositories.Count; i++)
        {
            var repository = Repositories[i];
            var row = $"{(i == Cursor ? ">" : " ")} [{(repository.Selected ? "x" : " ")}] {repository.Name}";

            if (repository.Note is { Length: > 0 } note)
            {
                row += $"  ({note})";
            }

            lines.Add(row);
        }

        lines.Add(string.Empty);
        lines.Add($"{SelectedCount} of {Repositories.Count} selected.");
        lines.Add("↑↓ move   space toggle   a all   n none   enter confirm   esc cancel");

        // Every line, not just the repository rows. The picker redraws by moving the cursor up
        // by the number of lines it wrote, so a line that wraps occupies two rows and the next
        // frame lands one row lower than the last — which walks the list down the screen on
        // every keystroke, as a header longer than a narrow terminal would.
        return [.. lines.Select(l => l.Length > width ? l[..Math.Max(0, width - 1)] + "…" : l)];
    }

    private RepositorySelection Move(int by)
    {
        // Clamped rather than wrapped. A list that jumps from the bottom to the top is how
        // somebody ticks the first repository while believing they ticked the last.
        var cursor = Math.Clamp(Cursor + by, 0, Repositories.Count - 1);

        return With(cursor: cursor);
    }

    private RepositorySelection SetAll(bool selected) =>
        new([.. Repositories.Select(r => r with { Selected = selected })], Cursor, Status);

    private RepositorySelection With(int? cursor = null, SelectionStatus? status = null) =>
        new(Repositories, cursor ?? Cursor, status ?? Status);
}

/// <summary>One repository offered for enrollment.</summary>
/// <param name="Name">The directory name, which is what the person recognises.</param>
/// <param name="Workspace">The full path.</param>
/// <param name="Note">Why it is worth a second look, or empty.</param>
/// <param name="Selected">Whether it is currently ticked.</param>
public sealed record SelectableRepository(string Name, string Workspace, string Note, bool Selected);

/// <summary>Where a picker has got to.</summary>
public enum SelectionStatus
{
    /// <summary>Still being chosen from.</summary>
    Choosing,

    /// <summary>The person accepted the selection.</summary>
    Confirmed,

    /// <summary>The person backed out, and nothing should be written.</summary>
    Cancelled,
}
