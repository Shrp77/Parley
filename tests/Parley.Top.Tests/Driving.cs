using System.Drawing;
using System.Text.RegularExpressions;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// Driving the surface from a test: an application with no terminal under it, keys and clicks
/// pushed into it, and the screen read back as text.
/// </summary>
/// <remarks>
/// <para>
/// One copy, for the reason <c>Loopback</c> is one copy: three test files each carried the same
/// three helpers within a day of one another.
/// </para>
/// <para>
/// <b>Views are found by title and rows by their text, never by coordinates.</b> The surface lays
/// its four regions out side by side above a threshold width and stacks them below it, so a test
/// that counted rows or columns would pass in one layout and fail in the other while the surface
/// was correct in both. It is the rule 004's helpers were written under too —
/// a menu entry was chosen by name against the list the surface built, never by a counted number
/// of arrow keys — and it survived the toolkit changing underneath it.
/// </para>
/// </remarks>
internal static class Driving
{
    /// <summary>
    /// How wide a driven screen is unless a test says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Well above the layout threshold, so a test that does not care about layout gets the side by
    /// side arrangement without asking for it.
    /// </para>
    /// <para>
    /// <b>A width of its own rather than the threshold plus a margin, which is what it was.</b>
    /// Derived, it moved when the threshold moved — and the threshold came back down to eighty,
    /// which left every test that reads a phrase off the screen driving a hundred and twenty
    /// columns, where a pane holding forty per cent of them clips an agent's standing mid-word.
    /// Three tests failed and not one of them was about layout. What a test of wording needs is
    /// room; what a test of layout needs is <see cref="Parley.Top.Shell.Threshold"/> named
    /// explicitly, which every one of them already does.
    /// </para>
    /// <para>
    /// Two hundred and forty, which is the number the old expression happened to produce while the
    /// threshold was two hundred. Chosen so that lowering the threshold changes the geometry of no
    /// test that was not asking about geometry — a dialog is a percentage of the screen, so a
    /// narrower default rewraps a sentence and fails a test about the words in it.
    /// </para>
    /// </remarks>
    internal const int DefaultWidth = 240;

    /// <summary>How tall a driven screen is unless a test says otherwise.</summary>
    internal const int DefaultHeight = 40;

    /// <summary>
    /// One application at a time, across every test class in this assembly.
    /// </summary>
    /// <remarks>
    /// xUnit runs test classes in parallel and Terminal.Gui carries process-global state that a
    /// second concurrent application would share — the driver registry, the configuration
    /// manager, and <c>Application.ForceDriver</c>. None of it is touched here, but "none of it is
    /// touched" is a claim about today's code and this is the guard that survives tomorrow's. It
    /// is the same reason <c>TopCommand.DriveAsync</c> takes its application rather than reaching
    /// for a static one, and the same trap <c>SqliteConnection.ClearAllPools</c> was banned for:
    /// the call looks local and is not.
    /// </remarks>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Matches an escape sequence, so the screen can be read as plain text.</summary>
    private static readonly Regex Escapes = new(
        "\\[[0-9;?]*[ -/]*[@-~]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Opens an application with no terminal under it.</summary>
    /// <param name="width">How wide the screen is.</param>
    /// <param name="height">How tall the screen is.</param>
    /// <returns>The session. Disposing it ends the application and releases the next test.</returns>
    /// <remarks>
    /// See <see cref="Session"/> for why the driver is the ANSI one and why the size is set here
    /// rather than reported by the terminal.
    /// </remarks>
    internal static Session Open(int width = DefaultWidth, int height = DefaultHeight)
    {
        OneAtATime.Wait(TimeSpan.FromMinutes(2));

        try
        {
            return new Session(width, height, OneAtATime);
        }
        catch
        {
            OneAtATime.Release();
            throw;
        }
    }

    /// <summary>An application with no terminal under it, and the ways a test drives it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The ANSI driver, initialised and then never pumped.</b> Terminal.Gui 2.4.17 offers no
    /// headless driver and no way to supply one: <c>DriverRegistry.Register</c> takes a descriptor
    /// carrying a factory, and <c>ApplicationImpl.CreateDriver</c> looks the name up in that
    /// registry and then switches on the string against <c>windows</c>, <c>dotnet</c> and
    /// <c>ansi</c> — so a registered fourth name passes the registry check and fails the switch
    /// with <i>Unknown driver name</i>, and its factory is never called. <c>ApplicationImpl</c>
    /// itself, whose constructor takes a component factory and is documented for testing, is
    /// internal to the assembly.
    /// </para>
    /// <para>
    /// What is left is enough, and it is quieter than it sounds. <c>Init</c> creates the driver
    /// and its main loop coordinator but does not run the loop, and every terminal write and
    /// every console read happens in the loop — measured, not assumed: a session that draws,
    /// takes a key and ends writes zero bytes to stdout and leaves stdin unread, on a machine
    /// where the console is not redirected at all. Drawing is a layout-and-draw pass into the
    /// driver's own cell buffer, which is what <see cref="Text"/> reads back.
    /// </para>
    /// <para>
    /// <b>The size is set rather than asked for.</b> The ANSI driver learns the terminal's size
    /// by sending a cursor-position query and waiting for the answer, and nothing answers here, so
    /// the screen stays zero by zero and every view lays out to nothing. Setting it is also what
    /// lets a test ask for forty columns and get the stacked layout without a terminal to resize.
    /// </para>
    /// </remarks>
    internal sealed class Session : IDisposable
    {
        private readonly SemaphoreSlim _release;
        private readonly List<SessionToken> _tokens = [];
        private bool _closed;

        internal Session(int width, int height, SemaphoreSlim release)
        {
            _release = release;

            Time = new VirtualTimeProvider();
            App = Application.Create(Time);
            App.Init("ansi");

            Resize(width, height);
        }

        /// <summary>The application under test.</summary>
        internal IApplication App { get; }

        /// <summary>
        /// The clock the application runs on.
        /// </summary>
        /// <remarks>
        /// Virtual so a test can advance the twenty minutes the seat's idle rule waits for, and
        /// the interval the log tail ticks on, rather than sleeping them.
        /// </remarks>
        internal VirtualTimeProvider Time { get; }

        /// <summary>Changes the size of the screen and lays out again.</summary>
        /// <param name="width">The new width.</param>
        /// <param name="height">The new height.</param>
        internal void Resize(int width, int height)
        {
            App.Driver?.SetScreenSize(width, height);
            App.Screen = new Rectangle(0, 0, width, height);
            App.LayoutAndDraw(true);
        }

        /// <summary>Every address the surface asked a browser to open, in order.</summary>
        /// <remarks>
        /// What a test asserts on instead of watching for a window. Empty until something presses
        /// <c>Shell.BrowserKey</c> or takes the console action.
        /// </remarks>
        internal List<string> Opened { get; } = [];

        /// <summary>Begins a session on a view and draws it.</summary>
        /// <param name="runnable">The view to run.</param>
        /// <remarks>
        /// <para>
        /// <c>Begin</c> rather than <c>Run</c>: <c>Run</c> pumps the main loop until something
        /// stops it, which is a test that hangs rather than one that fails. <c>Begin</c> puts the
        /// view on the session stack and returns, leaving the test to drive every step.
        /// </para>
        /// <para>
        /// <b>A driven surface is given a launcher that opens nothing, and that is a fix rather
        /// than a convenience.</b> <c>Shell.BrowserKey</c> reaches a real <c>Process.Start</c>, so
        /// two tests in <c>NavigatingTests</c> opened a tab in the developer's browser on every
        /// run of this suite — reported by the owner, in those words, while the terminal was being
        /// worked on and the suite was being run in a loop. <c>Launch</c> already carries the same
        /// defect being fixed once for a direct caller; that fix closed the route it was looking at
        /// and this is the other one.
        /// </para>
        /// <para>
        /// It is done here rather than in each test so that the guarantee holds for tests nobody
        /// has written yet: three files currently step around the console action by name, with a
        /// comment saying it starts a process, and a rule that depends on every future author
        /// reading those comments is not a rule.
        /// </para>
        /// </remarks>
        internal void Show(View runnable)
        {
            ArgumentNullException.ThrowIfNull(runnable);

            if (runnable is Parley.Top.Shell shell)
            {
                shell.Opener = (said, address) =>
                {
                    Opened.Add(address);
                    said($"Opened {address}");

                    return true;
                };
            }

            var token = App.Begin((IRunnable)runnable);

            Assert.True(token is not null, "the application refused to begin a session on the view");

            _tokens.Add(token!);
            App.LayoutAndDraw(true);
        }

        /// <summary>Presses a key and redraws.</summary>
        /// <param name="key">The key to press.</param>
        internal void Press(Key key)
        {
            App.InjectKey(key);
            App.LayoutAndDraw(true);
        }

        /// <summary>Moves focus to the next region.</summary>
        internal void Tab() => Press(Key.Tab);

        /// <summary>Opens, takes, or accepts what is in front of the person.</summary>
        internal void Enter() => Press(Key.Enter);

        /// <summary>Closes a dialog, or goes back one level.</summary>
        internal void Escape() => Press(Key.Esc);

        /// <summary>Types text, one key at a time.</summary>
        /// <param name="text">What to type.</param>
        internal void Type(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            foreach (var character in text)
            {
                App.InjectKey(new Key(character));
            }

            App.LayoutAndDraw(true);
        }

        /// <summary>Clicks the middle of a view.</summary>
        /// <param name="view">The view to click.</param>
        /// <remarks>
        /// <para>
        /// The test says which view; the frame says where it currently is. That is the only place
        /// a coordinate appears, and it is derived rather than written down, so a region that
        /// moves when the layout stacks does not move a test.
        /// </para>
        /// <para>
        /// <b>The point is hit-tested rather than delivered.</b> The click goes to whatever is
        /// under that position, which is not always the view asked for — a dialog over a region,
        /// or a view that has been covered, takes it instead, and the test fails as a person
        /// clicking there would. Delivering it straight to the named view would pass in exactly
        /// the case the surface is broken.
        /// </para>
        /// <para>
        /// It does not go through the driver's mouse pipeline, because that is pumped by the main
        /// loop and the main loop is what this harness does not run. What is exercised is the hit
        /// testing and the view's own handling; what is not is the parsing of an escape sequence
        /// into a mouse event, which belongs to the toolkit and is tested there.
        /// </para>
        /// </remarks>
        internal void Click(View view) => Click(view, MouseFlags.LeftButtonClicked);

        /// <summary>Double-clicks the middle of a view.</summary>
        /// <param name="view">The view to double-click.</param>
        /// <remarks>
        /// What opens a row in a list, where a single click only selects it. Both go through
        /// <see cref="Click(View, MouseFlags)"/> so a change to the hit testing cannot apply to
        /// one and not the other.
        /// </remarks>
        internal void DoubleClick(View view) => Click(view, MouseFlags.LeftButtonDoubleClicked);

        /// <summary>Clicks the row a list is currently on, wherever it sits.</summary>
        /// <param name="list">The list.</param>
        /// <param name="flags">Which click.</param>
        /// <remarks>
        /// <b>The selected row rather than the middle of the view</b>, because those are the same
        /// point only when the list happens to be as tall as its contents. 006 gave the
        /// conversations pane the full height of a project view, at which point the middle of the
        /// frame is empty space well below the rows -- so a click there selected nothing and read
        /// as the mouse route being broken rather than as the test aiming at the wrong place.
        /// </remarks>
        internal void ClickRow(ListView list, MouseFlags flags)
        {
            ArgumentNullException.ThrowIfNull(list);

            var frame = list.FrameToScreen();
            var row = Math.Max(0, list.SelectedItem ?? 0);
            var at = new Point(frame.X + 1, frame.Y + row);

            Deliver(list, at, flags);
        }

        private void Click(View view, MouseFlags flags)
        {
            ArgumentNullException.ThrowIfNull(view);

            var frame = view.FrameToScreen();
            var at = new Point(frame.X + (frame.Width / 2), frame.Y + (frame.Height / 2));

            Deliver(view, at, flags);
        }

        /// <summary>Hit-tests a point and delivers a click to whatever is under it.</summary>
        /// <param name="view">The view aimed at, used only when nothing is under the point.</param>
        /// <param name="at">Where on screen.</param>
        /// <param name="flags">Which click.</param>
        private void Deliver(View view, Point at, MouseFlags flags)
        {
            var under = App.TopRunnableView?.GetViewsUnderLocation(in at, ViewportSettingsFlags.None);
            var target = Deepest(under) ?? view;
            var inside = target.ScreenToViewport(in at);

            target.NewMouseEvent(new Mouse
            {
                Flags = flags,
                ScreenPosition = at,
                Position = inside,
                View = target,
            });

            App.LayoutAndDraw(true);
        }

        /// <summary>Moves the highlight in a list to the row carrying a piece of text.</summary>
        /// <param name="list">The list to move within.</param>
        /// <param name="text">Text the wanted row contains.</param>
        /// <remarks>
        /// By text and never by a counted number of arrow keys, which is the rule
        /// <c>Driving.Pick</c> was written under and for the same reason: a list that gains a row
        /// moves the highlight, and a test that counted would go somewhere else and still pass.
        /// </remarks>
        internal void Row(ListView list, string text)
        {
            ArgumentNullException.ThrowIfNull(list);
            ArgumentNullException.ThrowIfNull(text);

            var source = list.Source;
            var rows = source?.Count ?? 0;

            for (var at = 0; at < rows; at++)
            {
                if (source!.ToList()[at]?.ToString() is { } row
                    && row.Contains(text, StringComparison.Ordinal))
                {
                    list.SelectedItem = at;
                    App.LayoutAndDraw(true);

                    return;
                }
            }

            Assert.Fail($"No row containing '{text}' is in the list");
        }

        /// <summary>The screen as plain text, with every attribute stripped.</summary>
        /// <returns>The rendered screen.</returns>
        /// <remarks>
        /// SC-006: a test that strips all colour from the screen must lose no information, so
        /// this is what every assertion about what a person can see reads.
        /// </remarks>
        internal string Text() => Escapes.Replace(App.ToString() ?? string.Empty, string.Empty);

        /// <summary>Whether a view with this title is on the screen at all.</summary>
        /// <param name="title">The title to look for.</param>
        /// <returns><see langword="true"/> when one is.</returns>
        /// <remarks>
        /// For asserting that something did <i>not</i> open. <see cref="Find"/> fails when it finds
        /// nothing, which is right for a test that needs the view and wrong for one whose whole
        /// claim is that there is no view — that reads as catching the assertion library's own
        /// exception, which passes for any reason a search might throw.
        /// </remarks>
        internal bool Has(string title) => Search(App.TopRunnableView, title) is not null;

        /// <summary>Finds a view by its title, anywhere in the tree.</summary>
        /// <param name="title">The title to look for.</param>
        /// <returns>The view.</returns>
        internal View Find(string title)
        {
            var found = Search(App.TopRunnableView, title);

            Assert.True(found is not null, $"No view titled '{title}' is on the screen");

            return found!;
        }

        /// <summary>Ends every session and disposes the application.</summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            try
            {
                for (var at = _tokens.Count - 1; at >= 0; at--)
                {
                    App.End(_tokens[at]);
                }

                App.Dispose();
            }
            finally
            {
                _release.Release();
            }
        }

        /// <summary>
        /// The deepest ordinary view under a point, ignoring the decoration around it.
        /// </summary>
        /// <param name="under">Everything the hit test found, outermost first.</param>
        /// <returns>The view a person would say they clicked, or null if there is none.</returns>
        /// <remarks>
        /// Hit testing returns a view's margin, border, padding and shadow alongside the view
        /// itself, and the last of them is usually the shadow — which is drawn outside the view
        /// and handles nothing. Taking it would make every click land on decoration.
        /// </remarks>
        private static View? Deepest(List<View?>? under)
        {
            if (under is null)
            {
                return null;
            }

            for (var at = under.Count - 1; at >= 0; at--)
            {
                var candidate = under[at];

                if (candidate is null || candidate is AdornmentView || string.Equals(
                        candidate.GetType().Name,
                        "ShadowView",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private static View? Search(View? view, string title)
        {
            if (view is null)
            {
                return null;
            }

            if (string.Equals(view.Title, title, StringComparison.Ordinal))
            {
                return view;
            }

            foreach (var child in view.SubViews)
            {
                if (Search(child, title) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

}
