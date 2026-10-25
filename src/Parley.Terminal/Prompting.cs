using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

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
/// <b>Validation runs as the person types.</b> A dialog that only checked on submission would refuse
/// after the form had closed and the words were gone.
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
    /// <param name="correct">
    /// What the typed text becomes, for a field whose rule can be applied rather than only
    /// reported. Null leaves whatever is typed alone, which is what the dialogs that take a name
    /// typed back need: correcting there would type half of the confirmation for the person.
    /// </param>
    /// <param name="corrected">
    /// What to say when <paramref name="correct"/> changed something. Required whenever a
    /// correction is given, for the reason <see cref="FormField.Corrected"/> gives.
    /// </param>
    /// <returns>The dialog, not yet open.</returns>
    internal static TextDialog Text(
        string title,
        string question,
        string proposal = "",
        Func<string, string?>? problem = null,
        Func<string, string>? correct = null,
        string? corrected = null) =>
        new(title, question, proposal, problem, correct, corrected);

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
    /// <b>One copy</b>, shared by <see cref="FormDialog"/> and <see cref="ChoiceDialog"/>: two dialogs
    /// measuring text two ways would disagree about what fits.
    /// </para>
    /// </remarks>
    internal static int Rows(string text)
    {
        // **The breaks the text already has, and then the ones wrapping will add.** `ChoiceDialog`'s
        // descriptions are written with line breaks in them (the add confirmation names two file paths on
        // lines of their own), so counting characters alone would under-measure.
        var lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n');

        return Math.Max(1, lines.Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 60.0))));
    }

    /// <summary>Opens a dialog and returns the session it is running in.</summary>
    /// <param name="app">The application to open it in.</param>
    /// <param name="dialog">The dialog.</param>
    /// <returns>The session, to be ended when the dialog is done with, or null if it refused to open.</returns>
    internal static SessionToken? Open(IApplication app, View dialog)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dialog);

        // **On the thread that draws, when there is a loop.** Most dialogs are opened after a read, from the work
        // queue's thread-pool thread, while the loop is drawing; the toolkit's draw is not safe to enter twice at
        // once (it throws out of its line canvas).
        SessionToken? opened = null;

        OnTheLoop(() => opened = OpenHere(app, dialog));

        return opened;
    }

    /// <summary>Runs something on the thread that draws when there is a loop, and where it is called when not.</summary>
    /// <param name="act">What to run.</param>
    /// <remarks>
    /// Waits for it, so what the caller does next sees it done: a box emptied and then filled is filled. Invoke runs
    /// inline when this is already the loop's thread, so the waiting only ever waits for the loop to get to it. The
    /// harness never sets <see cref="Marshal"/>, because it never pumps a loop: work posted to one would not run.
    /// </remarks>
    internal static void OnTheLoop(Action act)
    {
        ArgumentNullException.ThrowIfNull(act);

        if (Marshal is not { } onTheLoop)
        {
            act();

            return;
        }

        using var done = new ManualResetEventSlim();

        onTheLoop(() =>
        {
            try
            {
                act();
            }
            finally
            {
                done.Set();
            }
        });

        // Bounded, so a loop that has already ended cannot hold a pool thread for ever.
        _ = done.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Runs something on the thread the toolkit draws on, or null to run it where it is called.</summary>
    /// <remarks>
    /// Set once, by the program, to the loop's own <c>Invoke</c>; never by a test. Static because every dialog goes through
    /// <see cref="Open"/> and a seam threaded through all of them would be a parameter on each.
    /// </remarks>
    internal static Action<Action>? Marshal { get; set; }

    private static SessionToken? OpenHere(IApplication app, View dialog)
    {

        // A dialog does not rename the terminal. The toolkit writes a modal runnable's title to the terminal's own,
        // which would replace the build the shell names there.
        dialog.Border.Settings &= ~BorderSettings.TerminalTitle;

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
    private readonly Func<string, string>? _correct;
    private readonly string _corrected;
    private readonly TextField _field;
    private readonly Label _why;
    private readonly Button _ok;

    /// <summary>What the last keystroke's correction did, until one that corrects nothing.</summary>
    private string _note = string.Empty;

    internal TextDialog(
        string title,
        string question,
        string proposal,
        Func<string, string?>? problem,
        Func<string, string>? correct = null,
        string? corrected = null)
    {
        _problem = problem;
        _correct = correct;
        _corrected = corrected ?? string.Empty;

        if (correct is not null && string.IsNullOrEmpty(corrected))
        {
            throw new ArgumentException(
                "A correction has to say what it did. See FormField.Corrected.", nameof(corrected));
        }

        Title = title;
        Width = Dim.Percent(70);

        // **Tall enough for the question, because the question is the point.** The longest thing this
        // dialog asks is the destroy warning, which is four lines, and it is the description standing
        // between somebody and a store being deleted. Nothing truncates, and where something does not fit
        // the width the surface says so; `ChoiceDialog` gives its label a width and a height for the same reason.
        Height = Dim.Percent(60);

        var asked = new Plain
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
        _field = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(2),

            // Corrected before it is shown, like the forms do it, so a proposal this dialog would
            // refuse is never the thing Enter accepts.
            Text = correct is not null ? correct(proposal) : proposal,
        };
        _why = new Plain { X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Text = string.Empty };
        _ok = new Button { Title = Prompting.Confirm, X = 1, Y = Pos.AnchorEnd(2), IsDefault = true };

        var cancel = new Button
        {
            Title = Prompting.Cancel,
            X = Pos.Right(_ok) + 1,
            Y = Pos.AnchorEnd(2),
        };

        _field.TextChanged += (_, _) => { Fix(); Check(); };
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
    /// <remarks>
    /// The one line under the field, which carries a problem when there is one and otherwise
    /// whatever the last correction did. A problem outranks a note for the reason
    /// <see cref="FormDialog"/> gives: "this name is already taken" matters more than "the capital
    /// was lowered".
    /// </remarks>
    internal string Problem => _why.Text;

    /// <summary>Whether what has been typed would be refused.</summary>
    /// <remarks>
    /// Asked of the button rather than of <see cref="Problem"/>, because a correction's note shares that
    /// line. Reading the label to decide whether to accept would refuse every name a correction had just tidied.
    /// </remarks>
    internal bool Wrong => !_ok.Enabled;

    /// <summary>Answers as if the person had pressed the button.</summary>
    /// <param name="taken">Whether they went ahead.</param>
    internal void Finish(bool taken) => Done?.Invoke(this, taken && !Wrong ? _field.Text : null);

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
        var problem = _problem?.Invoke(_field.Text) ?? string.Empty;

        _why.Text = problem.Length > 0 ? problem : _note;
        _ok.Enabled = problem.Length == 0;
    }

    /// <summary>Applies this dialog's own rule to what has just been typed, and says so.</summary>
    /// <remarks>
    /// <see cref="FormDialog.Fix"/> in one field's worth, and re-entrant by the same construction:
    /// setting <c>Text</c> raises <c>TextChanged</c> again, and a corrected value corrects to
    /// itself, so the second pass changes nothing and stops. The note stays while the corrected
    /// text does and goes when the field is emptied, because everything after the character that
    /// was corrected is usually already acceptable.
    /// </remarks>
    private void Fix()
    {
        if (_correct is not { } correct)
        {
            return;
        }

        var typed = _field.Text;
        var corrected = correct(typed);

        if (string.Equals(typed, corrected, StringComparison.Ordinal))
        {
            if (corrected.Length == 0)
            {
                _note = string.Empty;
            }

            return;
        }

        _note = _corrected;
        _field.Text = corrected;
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

        var wants = Prompting.Rows(description);

        // **As tall as it needs and never taller than the screen.** A dialog asking a question with no
        // visible answers is worse than one that is cramped.
        //
        // `Dim.Func` rather than a number, because the screen is not known while this is being
        // built: it is evaluated at layout time, by which point the dialog is running in
        // something.
        Height = Dim.Func(
            view => Math.Min(wants + choices.Count + Edges, Room(view)), this);

        var said = new Plain
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),

            // **Its own words, not what is left after the choices**, so a picker with many rows still draws
            // its description. A choice sits behind a description of it, and a description nobody can read
            // is the same as none.
            //
            // Capped at what is left once the choices have their rows, so a description too long
            // for the screen loses its tail rather than taking the answers with it. Both halves
            // readable is the ideal; only one of them can be guaranteed.
            // `view` here is the dialog, so its own viewport is the content area the label and the
            // list share, not the screen.
            Height = Dim.Func(view => Math.Max(1, Math.Min(wants, Content(view) - 1)), this),
        };

        said.TextFormatter.WordWrap = true;

        _choices = new ListView
        {
            X = 1,
            Y = Pos.Bottom(said),
            Width = Dim.Fill(2),

            // **Whatever is left, rather than one row per choice.** A list told it is taller than the dialog
            // believes every row is visible, so it never scrolls and the highlight — the only mark saying
            // which row a key is about to act on — walks off the bottom edge. Filling the space makes the
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

    /// <summary>The rows a choice dialog needs besides its description and its choices.</summary>
    /// <remarks>
    /// Named apart from <see cref="FormDialog"/>'s, which counts a different set of rows for a
    /// different dialog. Two constants sharing a name in one file, meaning two numbers, is how a
    /// reader comes to believe one of them is the other.
    /// </remarks>
    private const int Edges = 3;

    /// <summary>How tall this dialog is allowed to be.</summary>
    /// <param name="view">The dialog.</param>
    /// <returns>The screen's height, or something generous when it is not running yet.</returns>
    private static int Room(View? view) => view?.App?.Screen.Height ?? int.MaxValue;

    /// <summary>How many rows the description and the choices have between them.</summary>
    /// <param name="view">The dialog.</param>
    /// <returns>Its content area's height, or something generous before it has one.</returns>
    private static int Content(View? view) =>
        view?.Viewport.Height is > 0 and var rows ? rows : int.MaxValue;

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
/// be applied should be: a field that will not accept a capital in the first place cannot fail the way a
/// sentence under it can. But the message still exists for what correcting cannot fix: a name already
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
/// Several fields rather than several dialogs in a row. A person answering three prompts in
/// sequence cannot revise the first after writing the third — which is exactly when they realise
/// it was wrong.
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

        // **Every label is given the rows its own words need**, because these labels are sentences. Measured
        // per field rather than assumed, because the height of the dialog is the sum of them.
        _tall.AddRange(fields.Select(field => Prompting.Rows(field.Label)));

        foreach (var field in fields)
        {
            var asked = new Plain { Text = field.Label, X = 1, Width = Dim.Fill(2) };

            asked.TextFormatter.WordWrap = true;

            var entry = Entry.For(field);
            var why = new Plain { X = 1, Width = Dim.Fill(2), Text = string.Empty };

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
        var wanted = _entries.Select(entry => entry.Rows).ToList();
        var deep = Trimmed(wanted);
        var y = 0;

        for (var at = 0; at < _entries.Count; at++)
        {
            var tall = _tall[at];

            _asked[at].Y = y;
            _asked[at].Height = tall;

            _entries[at].View.Y = y + tall;
            _entries[at].View.Height = deep[at];

            _problems[at].Y = y + tall + deep[at];

            y += tall + deep[at] + 1;
        }

        _ok.Y = y + 1;
        _cancel.Y = y + 1;

        Height = y + Around;

        // **What each field wanted, not what it was given.** A form trimmed to fit the screen would
        // otherwise look changed on every keystroke, and re-lay itself for nothing.
        _laid.Clear();
        _laid.AddRange(wanted);

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

    /// <summary>The screen height this form last laid itself out for.</summary>
    private int _room;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The moment the screen's height becomes knowable.</b> <see cref="Trimmed"/> cannot trim in the
    /// constructor because the form is not running in anything yet, so the form is laid out again here, for
    /// the size it is given.
    /// </para>
    /// <para>
    /// Keyed on the height rather than on a "have I done this" flag, so a terminal resized under
    /// an open form is laid out for the size it has now.
    /// </para>
    /// </remarks>
    protected override void OnFrameChanged(in Rectangle frame)
    {
        base.OnFrameChanged(in frame);

        if (App?.Screen.Height is { } room && room != _room)
        {
            _room = room;

            Lay();
        }
    }

    /// <summary>The rows a form needs besides its fields: its border, its buttons and the gap.</summary>
    private const int Around = 6;

    /// <summary>
    /// How deep each field may actually be, given how tall the screen is.
    /// </summary>
    /// <param name="wanted">What each field asked for.</param>
    /// <returns>What each field gets.</returns>
    /// <remarks>
    /// <para>
    /// <b>A form taller than the screen is centred and clipped at the top</b>, so its first label and field
    /// would be off the screen entirely, and a field somebody cannot see is one they cannot tell is there.
    /// So this takes rows back: the deepest long field first, down to one row each, until the form fits.
    /// </para>
    /// <para>
    /// Nothing is trimmed when the screen is not known — the form is laid out once in its
    /// constructor, before it is running in anything, and guessing a screen size there would be
    /// worse than laying out for the size it will be given a moment later.
    /// </para>
    /// </remarks>
    private List<int> Trimmed(IReadOnlyList<int> wanted)
    {
        var deep = wanted.ToList();

        if (App?.Screen.Height is not { } room)
        {
            return deep;
        }

        var needed = _tall.Zip(deep, (label, field) => label + field + 1).Sum() + Around;

        while (needed > room)
        {
            var deepest = -1;

            for (var at = 0; at < deep.Count; at++)
            {
                if (deep[at] > 1 && (deepest < 0 || deep[at] > deep[deepest]))
                {
                    deepest = at;
                }
            }

            if (deepest < 0)
            {
                // Every field is down to one row and it still does not fit. Nothing here can help:
                // the labels alone are over the height, which is a screen too small for the form
                // rather than a form laid out wrongly.
                break;
            }

            deep[deepest]--;
            needed--;
        }

        return deep;
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
            // emptied.** Typing `OrderBook` corrects on the `O` and then every remaining letter is
            // already acceptable, so clearing the note on the first keystroke that needed no correction
            // would leave nobody able to read it. What it explains — that the field holds something other
            // than what was typed — is true for as long as that text is there.
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
