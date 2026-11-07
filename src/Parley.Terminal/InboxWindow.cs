using System.Collections.ObjectModel;
using System.Globalization;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The terminal's Inbox: what waits on the person, what is in flight, and what closed within the window (spec 036).
/// </summary>
/// <remarks>
/// <para>
/// <b>The console's three views, from the same derivation</b> (<see cref="Inbox"/>), so the two surfaces list the same
/// exchanges in the same views with the same words. Here they are three sections of one list rather than three pages.
/// </para>
/// <para>
/// Enter on a row opens its conversation at its last message; the follow-up command asks for words and sends the row's
/// agent a new Request in the row's conversation. Every line is passed through <see cref="Safe.Text"/>: a message is
/// somebody else's words.
/// </para>
/// </remarks>
internal sealed class InboxWindow : TableDialog<InboxWindow>, IKeyTable<InboxWindow>
{
    /// <summary>What the dialog is called.</summary>
    internal const string Heading = "Inbox";

    /// <summary>The follow-up command's key, with Alt.</summary>
    private const char FollowUpKey = 'u';

    /// <summary>The follow-up command's name.</summary>
    internal const string FollowUpTitle = "Follow up: a new request to this agent";

    private readonly ObservableCollection<string> _lines = [];

    private readonly List<InboxRow?> _rows = [];

    private readonly ListView _list = Lists.Filling();

    internal InboxWindow(string project, InboxReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        HotKeySpecifier = Plain.NoHotKey;
        Title = $"{Heading}: {Safe.Text(project)}";
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        foreach (var (line, row) in Lines(reading))
        {
            _lines.Add(line);
            _rows.Add(row);
        }

        _list.SetSource(_lines);
        _list.Accepting += (_, args) =>
        {
            args.Handled = true;

            if (Selected is { } row)
            {
                Chosen?.Invoke(this, row);
            }
        };

        Add(_list);
    }

    /// <summary>Raised with the row Enter was pressed on.</summary>
    internal event EventHandler<InboxRow>? Chosen;

    /// <summary>Raised with the row a follow-up is asked for on.</summary>
    internal event EventHandler<InboxRow>? FollowingUp;

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>The row the cursor is on, or null on a heading or a blank line.</summary>
    internal InboxRow? Selected => _list.SelectedItem is int at && at >= 0 && at < _rows.Count ? _rows[at] : null;

    /// <summary>Puts the cursor on a line, as a person moving to it would.</summary>
    /// <param name="line">The line.</param>
    internal void Select(int line) => _list.SelectedItem = line;

    /// <summary>The lines for a reading: a heading for each view, then a line for each of its rows.</summary>
    /// <param name="reading">The three views.</param>
    /// <returns>Each line, made inert, with the row it is for or null for a heading or a blank line.</returns>
    internal static IReadOnlyList<(string Line, InboxRow? Row)> Lines(InboxReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var lines = new List<(string, InboxRow?)>();

        foreach (var view in new[] { Parley.Client.Seating.InboxView.WaitingOnYou, Parley.Client.Seating.InboxView.InFlight, Parley.Client.Seating.InboxView.Closed })
        {
            var rows = reading.In(view);
            var heading = view switch
            {
                Parley.Client.Seating.InboxView.WaitingOnYou => "Waiting on you",
                Parley.Client.Seating.InboxView.InFlight => "In flight",
                _ => $"Closed in the last {reading.Days} {(reading.Days == 1 ? "day" : "days")}",
            };

            if (lines.Count > 0)
            {
                lines.Add((string.Empty, null));
            }

            lines.Add(($"{heading} ({rows.Count})", null));

            if (rows.Count == 0)
            {
                lines.Add(("  Nothing.", null));
            }

            lines.AddRange(rows.Select(row => (Safe.Text(Line(row)), (InboxRow?)row)));
        }

        return lines;
    }

    private static string Line(InboxRow row)
    {
        var where = row.Standing is { } standing
            ? standing.Chip + (standing.FlagWords is { } flag ? $", {flag}" : string.Empty)
            : row.Ended is { } ended ? ended.Words : "owes you an answer";

        var said = row.Last is { } last ? Preview(last.PayloadJson) : "its words were removed by retention";
        var when = row.MovedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

        return $"  exchange #{row.Exchange} {row.Agent.Value} in {row.Conversation}, {when}: {where}. {said}";
    }

    private static string Preview(string json)
    {
        var payload = Payload.Read(json);
        var text = payload.Said is { Length: > 0 } said ? said
            : payload.Points.Count > 0 ? string.Join(" / ", payload.Points.Select(point => point.Text))
            : payload.Raw ?? string.Empty;

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length > 100 ? text[..100] + "…" : text;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Enter on a row opens its conversation, taken by the list itself. The follow-up is offered only on a row it can go
    /// to: never the facilitator, which is never given work. Escape leaves through the toolkit.
    /// </remarks>
    static CommandManager<InboxWindow> IKeyTable<InboxWindow>.BuildTable() => new(
        [new(KeyContext.InboxDialog, [])],
        [
            new(
                KeyContext.InboxDialog,
                FollowUpKey,
                FollowUpTitle,
                KeyCommand.Doing<InboxWindow>(dialog =>
                {
                    if (dialog.Selected is { FollowUp: true } row)
                    {
                        dialog.FollowingUp?.Invoke(dialog, row);
                    }
                }),
                dialog => dialog.Selected is { FollowUp: true }),
        ],
        [
            new(KeyContext.InboxDialog, Key.Enter, "open its conversation"),
            .. ReservedKey.Moving(KeyContext.InboxDialog),
            .. ReservedKey.TakenBy(KeyContext.InboxDialog, () => Lists.Filling(), "the rows"),
        ]);

    /// <summary>Shows the three views.</summary>
    /// <param name="app">The application.</param>
    /// <param name="project">The project they are of.</param>
    /// <param name="reading">The three views.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (InboxWindow View, SessionToken? Token) Open(IApplication app, string project, InboxReading reading)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new InboxWindow(project, reading);

        return (view, Prompting.Open(app, view));
    }
}
