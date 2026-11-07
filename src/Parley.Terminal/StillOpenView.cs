using System.Collections.ObjectModel;
using Parley.Client.Seating;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;

namespace Parley.Terminal;

/// <summary>
/// The agents asked which of their questions to the person are still open, and where each answer stands.
/// </summary>
/// <remarks>
/// <para>
/// <b>The terminal's half of the console's list</b> (owner, 2026-11-06), from the same round and the same standing
/// (<see cref="StillOpen"/>), so the two surfaces ask the same agents and say the same about each. Read-only: what an
/// agent asks back is answered where it was asked, in its conversation.
/// </para>
/// <para>
/// Every line is passed through <see cref="Safe.Text"/>: a reason a send was refused is a broker's words.
/// </para>
/// </remarks>
internal sealed class StillOpenView : TableDialog<StillOpenView>, IKeyTable<StillOpenView>
{
    /// <summary>What the dialog is called.</summary>
    internal const string Heading = "Asked which are still open";

    private readonly ObservableCollection<string> _lines = [];

    internal StillOpenView(string said, IReadOnlyList<StillOpenStanding> rows)
    {
        HotKeySpecifier = Plain.NoHotKey;
        Title = Heading;
        Width = Dim.Percent(90);
        Height = Dim.Percent(70);

        foreach (var line in Lines(said, rows))
        {
            _lines.Add(line);
        }

        var list = Lists.Filling();

        list.SetSource(_lines);

        Add(list);
    }

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>The lines for a round: what was said about it, then one line for each agent asked.</summary>
    /// <param name="said">What the round came to.</param>
    /// <param name="rows">Where each ask stands.</param>
    /// <returns>The lines, each made inert.</returns>
    internal static IReadOnlyList<string> Lines(string said, IReadOnlyList<StillOpenStanding> rows) =>
    [
        Safe.Text(said),
        string.Empty,
        .. rows.Count == 0
            ? ["Nobody has questions waiting on you here."]
            : rows.Select(row => Safe.Text(
                $"{row.Ask.Agent.Value} in {row.Ask.Conversation}: {row.State}. " +
                $"{row.Closed} of {row.Ask.Questions.Count} closed. {row.Detail}")),
    ];

    /// <inheritdoc/>
    /// <remarks>
    /// No command of its own: read-only, so there is nothing to do but read and leave. Escape leaves through the toolkit,
    /// which ends the session on top when no view in it takes the key. The lines take their own keys first.
    /// </remarks>
    static CommandManager<StillOpenView> IKeyTable<StillOpenView>.BuildTable() => new(
        [new(KeyContext.StillOpenDialog, [])],
        [],
        [
            .. ReservedKey.Moving(KeyContext.StillOpenDialog),
            .. ReservedKey.TakenBy(KeyContext.StillOpenDialog, () => Lists.Filling(), "the lines"),
        ]);

    /// <summary>Shows the agents asked and where each stands.</summary>
    /// <param name="app">The application.</param>
    /// <param name="said">What the round came to.</param>
    /// <param name="rows">Where each ask stands.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (StillOpenView View, SessionToken? Token) Open(
        IApplication app, string said, IReadOnlyList<StillOpenStanding> rows)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new StillOpenView(said, rows);

        return (view, Prompting.Open(app, view));
    }
}
