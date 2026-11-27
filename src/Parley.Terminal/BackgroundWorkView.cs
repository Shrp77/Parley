using System.Collections.ObjectModel;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;

namespace Parley.Terminal;

/// <summary>Every background task an agent has running, by name, read-only (SPEC.md §11.6).</summary>
/// <remarks>
/// <para>
/// <b>The terminal's half of the agent page's list</b>, from the registry already read: each task the count says is running,
/// by the agent's own words where it gave them, else the runner's, and a plain stand-in for one its node did not name, with
/// its description and where its link goes. The words are the console's (<see cref="TaskWords"/>), so both surfaces name a
/// task the same way.
/// </para>
/// <para>
/// A list that scrolls, which Page Up and Page Down move through a screen at a time, so any number of tasks can be read;
/// the title says how many. A task's words reached here neutralised twice, by the broker and by the client reading them
/// (<see cref="BackgroundTaskText"/>), which takes out what reorders or hides a line; every line is passed through
/// <see cref="Safe.Text"/> as well, as all text from elsewhere is. A link is said, never followed: a terminal opens
/// nothing.
/// </para>
/// </remarks>
internal sealed class BackgroundWorkView : TableDialog<BackgroundWorkView>, IKeyTable<BackgroundWorkView>
{
    /// <summary>What the dialog is called, before its count.</summary>
    internal const string Heading = "Background work";

    private readonly ObservableCollection<string> _lines = [];

    internal BackgroundWorkView(string agent, TurnStanding turns)
    {
        HotKeySpecifier = Plain.NoHotKey;
        Title = Safe.Text($"{Heading} of {agent}: {(turns.BackgroundTasks == 1 ? "1 task" : $"{turns.BackgroundTasks} tasks")}");
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        foreach (var line in Lines(turns))
        {
            _lines.Add(line);
        }

        var list = Lists.Filling();

        list.SetSource(_lines);

        Add(list);
    }

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>The lines for an agent's background work: each task, numbered, then its description and link indented.</summary>
    /// <param name="turns">What its node last said.</param>
    /// <returns>The lines, each made inert.</returns>
    internal static IReadOnlyList<string> Lines(TurnStanding turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var lines = new List<string>();
        var every = TaskWords.Every(turns.Named, turns.BackgroundTasks);

        for (var i = 0; i < every.Count; i++)
        {
            var task = every[i];
            var kind = TaskWords.Kind(task);

            lines.Add(Safe.Text($"{i + 1}. {TaskWords.Name(task)}{(kind.Length > 0 ? $" ({kind})" : string.Empty)}"));

            if (task is { Description.Length: > 0 })
            {
                lines.Add("   " + Safe.Text(task.Description));
            }

            if (task?.Link is { } link)
            {
                lines.Add("   " + Safe.Text(TaskWords.Copyable(link)));
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing is running in the background.");
        }

        return lines;
    }

    /// <inheritdoc/>
    /// <remarks>No command of its own: read-only. Escape leaves through the toolkit; the lines take their own keys first.</remarks>
    static CommandManager<BackgroundWorkView> IKeyTable<BackgroundWorkView>.BuildTable() => new(
        [new(KeyContext.BackgroundWorkDialog, [])],
        [],
        [
            .. ReservedKey.Moving(KeyContext.BackgroundWorkDialog),
            .. ReservedKey.TakenBy(KeyContext.BackgroundWorkDialog, () => Lists.Filling(), "the lines"),
        ]);

    /// <summary>Shows an agent's background work.</summary>
    /// <param name="app">The application.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="turns">What its node last said.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (BackgroundWorkView View, SessionToken? Token) Open(IApplication app, string agent, TurnStanding turns)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new BackgroundWorkView(agent, turns);

        return (view, Prompting.Open(app, view));
    }
}
