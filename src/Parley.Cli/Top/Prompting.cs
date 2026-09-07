using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// The two dialogs every screen here is built from: one that asks for a line of text, and one
/// that offers a choice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opened as sessions rather than run.</b> <c>Run</c> pumps the main loop until the dialog
/// closes, which is convenient in production and impossible in a test — the test is the thing that
/// would have to press the key that ends the loop. <c>Begin</c> puts the dialog on the session
/// stack and returns, so keys reach it, the caller drives it, and the same code path serves both.
/// </para>
/// <para>
/// <b>Validation runs as the person types.</b> 004 shipped forms whose every proposal was accepted
/// by Enter and whose every answer was checked while it was being written, and that is what use
/// added to it after its plan. A dialog that only checked on submission would be a step backwards
/// wearing a new toolkit.
/// </para>
/// </remarks>
internal static class Prompting
{
    /// <summary>What a dialog says on the button that goes ahead.</summary>
    internal const string Confirm = "OK";

    /// <summary>What a dialog says on the button that does not.</summary>
    internal const string Cancel = "Cancel";

    /// <summary>
    /// Asks for one line of text, checked as it is typed.
    /// </summary>
    /// <param name="title">What the dialog is called.</param>
    /// <param name="question">What is being asked for.</param>
    /// <param name="proposal">What the field starts with, which Enter accepts.</param>
    /// <param name="problem">
    /// What is wrong with what has been typed, or null when nothing is. Called on every keystroke.
    /// </param>
    /// <returns>The dialog, not yet open.</returns>
    internal static TextDialog Text(
        string title,
        string question,
        string proposal = "",
        Func<string, string?>? problem = null) =>
        new(title, question, proposal, problem);

    /// <summary>Offers a choice, with the safe answer first.</summary>
    /// <param name="title">What the dialog is called.</param>
    /// <param name="description">What is being decided, and what each choice would do.</param>
    /// <param name="choices">The choices, safest first.</param>
    /// <returns>The dialog, not yet open.</returns>
    /// <remarks>
    /// The first choice is the one Enter takes and is always the one that changes nothing. Every
    /// destructive action on this surface is built from this plus a typed name, and an ordering
    /// that put the destructive answer under the cursor would make the description decorative.
    /// </remarks>
    internal static ChoiceDialog Choose(
        string title,
        string description,
        IReadOnlyList<string> choices) =>
        new(title, description, choices);

    /// <summary>Opens a dialog and returns the session it is running in.</summary>
    /// <param name="app">The application to open it in.</param>
    /// <param name="dialog">The dialog.</param>
    /// <returns>The session, to be ended when the dialog is done with, or null if it refused to open.</returns>
    internal static SessionToken? Open(IApplication app, View dialog)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dialog);

        var token = app.Begin((IRunnable)dialog);

        app.LayoutAndDraw(true);

        return token;
    }

    /// <summary>Closes a dialog.</summary>
    /// <param name="app">The application it is open in.</param>
    /// <param name="token">The session it is running in.</param>
    internal static void Close(IApplication app, SessionToken? token)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (token is not null)
        {
            app.End(token);
            app.LayoutAndDraw(true);
        }
    }
}

/// <summary>A dialog asking for one line of text, checked as it is typed.</summary>
internal sealed class TextDialog : Dialog
{
    private readonly Func<string, string?>? _problem;
    private readonly TextField _field;
    private readonly Label _why;
    private readonly Button _ok;

    internal TextDialog(string title, string question, string proposal, Func<string, string?>? problem)
    {
        _problem = problem;

        Title = title;
        Width = Dim.Percent(70);

        // **Tall enough for the question, because the question is the point.** This was nine rows
        // with the label at Y=0 and the field at Y=1, so whatever was asked got exactly one line
        // however long it was — and the longest thing this dialog asks is the destroy warning,
        // which is four lines. Two hundred and fifty characters of it, including "Every
        // conversation in it goes with the store", were simply never drawn: the owner met it
        // trying to delete a project and reported the text running off the screen.
        //
        // That is the rule this surface is built on — nothing truncates, and where something does
        // not fit the width the surface says so — broken in the worst place for it, since this is
        // the description standing between somebody and a store being deleted.
        //
        // `ChoiceDialog` next to it always gave its label a width and a height, which is why the
        // dialog *before* this one reads correctly and this one did not.
        Height = Dim.Percent(60);

        var asked = new Label
        {
            Text = question,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(5),
        };

        // Asked for outright rather than relied on. A label sized to a rectangle still draws one
        // long line unless it is told to break at words.
        asked.TextFormatter.WordWrap = true;

        // The footer sits against the bottom, so the question keeps everything above it however
        // tall the dialog ends up.
        _field = new TextField { X = 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill(2), Text = proposal };
        _why = new Label { X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Text = string.Empty };
        _ok = new Button { Title = Prompting.Confirm, X = 1, Y = Pos.AnchorEnd(2), IsDefault = true };

        var cancel = new Button
        {
            Title = Prompting.Cancel,
            X = Pos.Right(_ok) + 1,
            Y = Pos.AnchorEnd(2),
        };

        _field.TextChanged += (_, _) => Check();
        _ok.Accepting += (_, args) => { args.Handled = true; Finish(taken: true); };
        cancel.Accepting += (_, args) => { args.Handled = true; Finish(taken: false); };

        Add(asked, _field, _why, _ok, cancel);
        Check();
    }

    /// <summary>Raised when the person has answered or declined.</summary>
    internal event EventHandler<string?>? Done;

    /// <summary>What has been typed so far.</summary>
    internal string Typed => _field.Text;

    /// <summary>What is wrong with what has been typed, as shown.</summary>
    internal string Problem => _why.Text;

    /// <summary>Answers as if the person had pressed the button.</summary>
    /// <param name="taken">Whether they went ahead.</param>
    internal void Finish(bool taken) => Done?.Invoke(this, taken && _why.Text.Length == 0 ? _field.Text : null);

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key == Key.Esc)
        {
            Finish(taken: false);

            return true;
        }

        return base.OnKeyDown(key);
    }

    /// <summary>Re-checks what has been typed and enables or refuses the button.</summary>
    private void Check()
    {
        _why.Text = _problem?.Invoke(_field.Text) ?? string.Empty;
        _ok.Enabled = _why.Text.Length == 0;
    }
}

/// <summary>A dialog offering a choice, with the safe answer first.</summary>
internal sealed class ChoiceDialog : Dialog
{
    private readonly ListView _choices;
    private readonly string _described;

    internal ChoiceDialog(string title, string description, IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        Title = title;
        Width = Dim.Percent(70);
        Height = Dim.Percent(60);

        var said = new Label
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(choices.Count + 2),
        };

        _choices = new ListView
        {
            X = 1,
            Y = Pos.Bottom(said),
            Width = Dim.Fill(2),
            Height = choices.Count,
        };

        _choices.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>([.. choices]));
        _choices.SelectedItem = 0;
        _choices.Accepting += (_, args) =>
        {
            args.Handled = true;
            Done?.Invoke(this, Chosen);
        };

        _described = description;

        Add(said, _choices);
    }

    /// <summary>Raised when the person has chosen, or declined to.</summary>
    internal event EventHandler<string?>? Done;

    /// <summary>What is being decided, as the dialog states it.</summary>
    /// <remarks>
    /// Read by tests that assert a description was given before a choice was offered, which is
    /// most of what "behind a description" means on this surface.
    /// </remarks>
    internal string Described => _described;

    /// <summary>Whichever choice the person is on.</summary>
    internal string? Chosen =>
        _choices.SelectedItem is { } at && _choices.Source is { } source && at >= 0 && at < source.Count
            ? source.ToList()[at]?.ToString()
            : null;

    /// <summary>The list, so a test can pick a choice by its text.</summary>
    internal ListView Choices => _choices;

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

/// <summary>One field of a form, and what is wrong with what is in it.</summary>
/// <param name="Label">What is being asked for.</param>
/// <param name="Proposal">What the field starts with, which Enter accepts.</param>
/// <param name="Problem">What is wrong with what has been typed, or null when nothing is.</param>
internal sealed record FormField(
    string Label,
    string Proposal = "",
    Func<string, string?>? Problem = null);

/// <summary>A dialog collecting several lines at once, each checked as it is typed.</summary>
/// <remarks>
/// <para>
/// Several fields rather than several dialogs in a row. Rule 8.1 asks for an intent, a question
/// and its consequences together, and a person answering three prompts in sequence cannot revise
/// the first after writing the third — which is exactly when they realise the intent was wrong.
/// </para>
/// <para>
/// The button that sends is refused while any field is wrong, and every field says what is wrong
/// with it under itself rather than in one shared line. A single error line names the first
/// problem and hides the rest, so a form with two mistakes takes two attempts to learn about both.
/// </para>
/// </remarks>
internal sealed class FormDialog : Dialog
{
    private readonly IReadOnlyList<FormField> _fields;
    private readonly List<TextField> _entries = [];
    private readonly List<Label> _problems = [];
    private readonly Button _ok;

    internal FormDialog(string title, IReadOnlyList<FormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _fields = fields;

        Title = title;
        Width = Dim.Percent(80);
        Height = (fields.Count * 3) + 6;

        var y = 0;

        foreach (var field in fields)
        {
            var asked = new Label { Text = field.Label, X = 1, Y = y };
            var entry = new TextField { X = 1, Y = y + 1, Width = Dim.Fill(2), Text = field.Proposal };
            var why = new Label { X = 1, Y = y + 2, Width = Dim.Fill(2), Text = string.Empty };

            entry.TextChanged += (_, _) => Check();

            _entries.Add(entry);
            _problems.Add(why);

            Add(asked, entry, why);

            y += 3;
        }

        _ok = new Button { Title = Prompting.Confirm, X = 1, Y = y + 1, IsDefault = true };

        var cancel = new Button { Title = Prompting.Cancel, X = Pos.Right(_ok) + 1, Y = y + 1 };

        _ok.Accepting += (_, args) => { args.Handled = true; Finish(taken: true); };
        cancel.Accepting += (_, args) => { args.Handled = true; Finish(taken: false); };

        Add(_ok, cancel);
        Check();
    }

    /// <summary>Raised when the person has filled it in or declined to.</summary>
    internal event EventHandler<IReadOnlyList<string>?>? Done;

    /// <summary>What is in each field, in the order they were asked for.</summary>
    internal IReadOnlyList<string> Values => [.. _entries.Select(entry => entry.Text)];

    /// <summary>What is wrong with each field, in the same order.</summary>
    internal IReadOnlyList<string> Problems => [.. _problems.Select(why => why.Text)];

    /// <summary>Puts text in one field, as if it had been typed.</summary>
    /// <param name="at">Which field.</param>
    /// <param name="text">What to put in it.</param>
    internal void Fill(int at, string text)
    {
        _entries[at].Text = text;
        Check();
    }

    /// <summary>Answers as if the person had pressed the button.</summary>
    /// <param name="taken">Whether they went ahead.</param>
    internal void Finish(bool taken) =>
        Done?.Invoke(this, taken && _ok.Enabled ? Values : null);

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key == Key.Esc)
        {
            Finish(taken: false);

            return true;
        }

        return base.OnKeyDown(key);
    }

    /// <summary>Re-checks every field and enables or refuses the button.</summary>
    private void Check()
    {
        var wrong = false;

        for (var at = 0; at < _fields.Count; at++)
        {
            _problems[at].Text = _fields[at].Problem?.Invoke(_entries[at].Text) ?? string.Empty;
            wrong |= _problems[at].Text.Length > 0;
        }

        _ok.Enabled = !wrong;
    }
}
