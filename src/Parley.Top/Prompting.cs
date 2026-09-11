using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Top;

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

    /// <summary>How many rows a sentence needs when it is wrapped.</summary>
    /// <param name="text">The sentence.</param>
    /// <returns>The row count, at least one.</returns>
    /// <remarks>
    /// <para>
    /// Estimated against a width this cannot know yet — a dialog is a percentage of a screen that
    /// has not been laid out. Sixty is the narrow end of what most of an ordinary terminal gives,
    /// so the estimate errs towards more rows than are needed: a label with a spare row below it
    /// looks like spacing, and one row short is a sentence cut in half.
    /// </para>
    /// <para>
    /// <b>One copy.</b> <see cref="FormDialog"/> grew this first, for the form the owner met
    /// running off the screen; <see cref="ChoiceDialog"/> then met the same defect from the other
    /// direction, its description squeezed to nothing by the list below it. Two dialogs measuring
    /// text two ways is how they come to disagree about what fits.
    /// </para>
    /// </remarks>
    internal static int Rows(string text) =>
        Math.Max(1, (int)Math.Ceiling((text ?? string.Empty).Length / 60.0));

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

        // **Taller when there is more to show, and never taller than the screen.** A percentage
        // fixed at sixty was the whole dialog's height however many choices it held, which is what
        // put twenty agents into a box with room for fourteen of them.
        Height = Dim.Percent(choices.Count > 6 ? 85 : 60);

        var said = new Label
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),

            // **Its own words, not what is left after the choices.** This was
            // `Dim.Fill(choices.Count + 2)`, which gives the description whatever the list does not
            // want — so a picker with twenty rows in it drew no description at all. The rule this
            // surface is held to is that a choice sits behind a description of it, and a
            // description nobody can read is the same as none. `DoorsAreReadableTests` did not
            // catch it because every dialog it drives offers two or three choices.
            Height = Prompting.Rows(description),
        };

        said.TextFormatter.WordWrap = true;

        _choices = new ListView
        {
            X = 1,
            Y = Pos.Bottom(said),
            Width = Dim.Fill(2),

            // **Whatever is left, rather than one row per choice.** A list told it is twenty rows
            // tall inside an eighteen-row dialog believes every row is visible, so it never
            // scrolls: the cursor walks off the bottom edge and the highlight — the only mark
            // saying which row a key is about to act on — goes with it. Reported by the owner
            // against a project with nineteen agents. Filling the space is what makes the
            // viewport true, and the toolkit scrolls on its own once it is.
            Height = Dim.Fill(),
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
/// <param name="Correct">
/// What the typed text becomes, for a field whose rule can be applied rather than only reported.
/// Null leaves whatever is typed alone.
/// </param>
/// <param name="Corrected">
/// What to say when <see cref="Correct"/> changed something. Required by
/// <see cref="FormDialog"/> whenever a correction is given, because a field that silently rewrites
/// what somebody typed is worse than one that refuses it: they are left believing they typed
/// something they did not.
/// </param>
/// <param name="Long">
/// Whether what goes in it can run past one line. A long field is several rows deep, wraps, takes
/// a line break, and grows and shrinks as it is written into.
/// </param>
/// <remarks>
/// <b>Correcting and reporting are both here, and neither replaces the other.</b> A rule that can
/// be applied should be — the owner typed <c>TimeTracker</c> into the project name and met no
/// resistance at all, because the only thing enforcing lowercase was a sentence under a field, on
/// a row that had been clipped. A field that will not accept a capital in the first place cannot
/// have that failure. But the message still exists for what correcting cannot fix: a name already
/// taken, a path that is not writable, a first character that is a digit.
/// </remarks>
internal sealed record FormField(
    string Label,
    string Proposal = "",
    Func<string, string?>? Problem = null,
    Func<string, string>? Correct = null,
    string? Corrected = null,
    bool Long = false);

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
    /// <summary>How many rows a long field may grow to before it scrolls instead.</summary>
    /// <remarks>
    /// A bound rather than no bound: a form is several fields and a screen is one screen, so a
    /// field free to grow without limit pushes the button that sends off the bottom of it. Eight
    /// rows is a paragraph, which is what these fields are for; past that the field scrolls and
    /// the rest of the form stays where it is.
    /// </remarks>
    internal const int Deepest = 8;

    /// <summary>How many rows a long field starts at.</summary>
    /// <remarks>
    /// Three rather than one, so a field meant for a paragraph looks like one before anything is
    /// typed. A field that only grows once somebody has overflowed it teaches them it is a line.
    /// </remarks>
    internal const int Opening = 3;

    private readonly IReadOnlyList<FormField> _fields;
    private readonly List<Entry> _entries = [];
    private readonly List<Label> _asked = [];
    private readonly List<Label> _problems = [];
    private readonly List<int> _tall = [];

    /// <summary>What each field last said about correcting what was typed into it.</summary>
    /// <remarks>
    /// Kept beside the fields rather than written straight into the label, because
    /// <see cref="Check"/> rewrites every label on every keystroke and would erase it.
    /// </remarks>
    private readonly List<string> _corrections = [];
    private readonly Button _ok;
    private readonly Button _cancel;

    internal FormDialog(string title, IReadOnlyList<FormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _fields = fields;

        Title = title;
        Width = Dim.Percent(80);

        // **Every label is given the rows its own words need.** This was one row per label and a
        // height counted three rows to the field, so a label longer than the dialog was cut where
        // it ran out — and these labels are sentences: the node catalogue's is a hundred and ten
        // characters. The owner met it creating a project and reported the form running off the
        // screen, which is the same defect `TextDialog` had, in the neighbour nobody looked at
        // after fixing that one.
        //
        // Measured per field rather than assumed, because the height of the dialog is the sum of
        // them and a fixed three-per-field is what made this wrong.
        _tall.AddRange(fields.Select(field => Prompting.Rows(field.Label)));

        foreach (var field in fields)
        {
            var asked = new Label { Text = field.Label, X = 1, Width = Dim.Fill(2) };

            asked.TextFormatter.WordWrap = true;

            var entry = Entry.For(field);
            var why = new Label { X = 1, Width = Dim.Fill(2), Text = string.Empty };

            entry.Changed += () =>
            {
                if (entry.Short is { } line)
                {
                    Fix(line, field);
                }

                Check();
                Regrow();
            };

            _asked.Add(asked);
            _entries.Add(entry);
            _problems.Add(why);
            _corrections.Add(string.Empty);

            Add(asked, entry.View, why);
        }

        _ok = new Button { Title = Prompting.Confirm, X = 1, IsDefault = true };
        _cancel = new Button { Title = Prompting.Cancel, X = Pos.Right(_ok) + 1 };

        _ok.Accepting += (_, args) => { args.Handled = true; Finish(taken: true); };
        _cancel.Accepting += (_, args) => { args.Handled = true; Finish(taken: false); };

        Add(_ok, _cancel);
        Lay();
        Check();
    }

    /// <summary>
    /// Puts every row where it goes, given how deep each field currently is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Run again whenever a field changes depth</b>, which is what makes a long field grow and
    /// shrink under what is being written into it rather than staying whatever size it was built
    /// at. The positions are absolute rather than relative for the plain reason that this is the
    /// one place they are decided: a chain of <c>Pos.Bottom</c> would put the same arithmetic in
    /// the toolkit's hands and leave nothing here to read.
    /// </para>
    /// <para>
    /// A form of ordinary one-line fields lands exactly where it did before long fields existed —
    /// label, field, a row for what is wrong with it — which is what keeps every screen built on
    /// this one looking as it did.
    /// </para>
    /// </remarks>
    private void Lay()
    {
        var y = 0;

        for (var at = 0; at < _entries.Count; at++)
        {
            var tall = _tall[at];
            var deep = _entries[at].Rows;

            _asked[at].Y = y;
            _asked[at].Height = tall;

            _entries[at].View.Y = y + tall;
            _entries[at].View.Height = deep;

            _problems[at].Y = y + tall + deep;

            y += tall + deep + 1;
        }

        _ok.Y = y + 1;
        _cancel.Y = y + 1;

        Height = y + 6;

        _laid.Clear();
        _laid.AddRange(_entries.Select(entry => entry.Rows));

        SetNeedsLayout();

        // **Laid out here rather than left for the next iteration, and that is not only for the
        // tests.** A field that grows under a keystroke has to be the right size before the
        // keystroke after it, because the next thing this does is measure itself again — so a
        // deferred layout means every measurement is one keystroke behind and the field settles a
        // row short. The harness never pumps the loop at all, which is where it showed up.
        if (IsInitialized)
        {
            Layout();
        }
    }

    /// <summary>Re-lays the form when a field has changed how deep it is.</summary>
    /// <remarks>
    /// Compared before laying rather than laying on every keystroke: a form that re-arranged itself
    /// on each character would move the cursor's row under somebody typing into the field above.
    /// </remarks>
    private void Regrow()
    {
        if (_laid.Count == _entries.Count
            && _entries.Zip(_laid, (entry, was) => entry.Rows == was).All(same => same))
        {
            return;
        }

        Lay();
    }

    /// <summary>How deep each field was when the form was last laid out.</summary>
    private readonly List<int> _laid = [];

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
        Regrow();
    }

    /// <summary>The field somebody types into, so a test can drive it.</summary>
    /// <param name="at">Which field.</param>
    /// <returns>The view.</returns>
    internal View Field(int at) => _entries[at].View;

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
            var problem = _fields[at].Problem?.Invoke(_entries[at].Text) ?? string.Empty;

            // A correction's note stands until the next keystroke replaces it, so that somebody who
            // typed a capital sees why the field now holds something else. A real problem outranks
            // it: "this name is already taken" matters more than "the capital was lowered".
            _problems[at].Text = problem.Length > 0 ? problem : _corrections[at];

            wrong |= problem.Length > 0;
        }

        _ok.Enabled = !wrong;
    }

    /// <summary>
    /// Applies a field's own rule to what has just been typed, and says so when it changed it.
    /// </summary>
    /// <param name="entry">The field.</param>
    /// <param name="field">Its rule.</param>
    /// <remarks>
    /// <para>
    /// <b>Nothing is rewritten silently.</b> A field that quietly replaces what somebody typed
    /// leaves them believing they typed something else, which is worse than refusing it — so a
    /// correction always leaves its note behind, and <see cref="FormField.Corrected"/> is required
    /// whenever a correction is given.
    /// </para>
    /// <para>
    /// Re-entrant by construction: setting <c>Text</c> raises <c>TextChanged</c> again. The guard
    /// is that a corrected value corrects to itself, so the second pass changes nothing and stops.
    /// The cursor is left to the toolkit, which puts it at the end of a field whose text was just
    /// set — which is where somebody typing wants it. 2.4.17 exposes no caret on TextField to put
    /// back anywhere else, so a driven test types a whole name in one character at a time rather
    /// than trusting that.
    /// </para>
    /// </remarks>
    private void Fix(TextField entry, FormField field)
    {
        if (field.Correct is not { } correct)
        {
            return;
        }

        var typed = entry.Text;
        var corrected = correct(typed);
        var at = _fields.IndexOf(field);

        if (string.Equals(typed, corrected, StringComparison.Ordinal))
        {
            // **The note stays while the corrected text does, and goes when the field is
            // emptied.** Clearing it on the first keystroke that needed no correction was tried
            // and is wrong: typing `TimeTracker` corrects on the `T` and then every remaining
            // letter is already acceptable, so the note was gone by the second character and
            // nobody could read it. What it explains — that the field holds something other than
            // what was typed — is true for as long as that text is there.
            if (at >= 0 && corrected.Length == 0)
            {
                _corrections[at] = string.Empty;
            }

            return;
        }

        entry.Text = corrected;

        if (at >= 0)
        {
            _corrections[at] = field.Corrected ?? string.Empty;
        }
    }
}
