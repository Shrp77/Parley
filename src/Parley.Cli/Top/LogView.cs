using System.Collections.ObjectModel;
using Parley.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// The broker's log, on screen, updating as it is written.
/// </summary>
/// <remarks>
/// <para>
/// The following itself is <see cref="LogTail"/>'s and is unchanged — where the file is, starting
/// at the end, reopening when the file at that path stops being the file it is holding. This is
/// the part that draws.
/// </para>
/// <para>
/// <b>It no longer stops on <c>Console.ReadLine</c>.</b> The Spectre version read a key on a
/// worker thread so the follow loop kept drawing, which worked and could not be driven past its
/// first line by a test — recorded as a low finding on 2026-09-03. Escape closes this, through the
/// same key handling as every other dialog, and a test can drive it.
/// </para>
/// </remarks>
internal sealed class LogView : Dialog
{
    /// <summary>What is said when no log can be found where one is expected.</summary>
    private const string NoLog = "No log where this project's broker usually writes one";

    /// <summary>What is said above the lines.</summary>
    internal const string Following = "Following ";

    private readonly ObservableCollection<string> _lines = [];
    private readonly List<string> _pending = [];
    private readonly ListView _list;
    private readonly CancellationTokenSource _stopping = new();

    private Task _watching = Task.CompletedTask;
    private bool _closed;

    internal LogView(string title)
    {
        Title = title;
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        _list = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _list.SetSource(_lines);

        Add(_list);
    }

    /// <summary>Every line shown so far.</summary>
    /// <remarks>
    /// Drained first, so a caller that has just drawn sees what the follower read. Reading the
    /// bound collection alone would report a view one draw behind the file.
    /// </remarks>
    internal IReadOnlyList<string> Lines
    {
        get
        {
            Drain();

            return [.. _lines];
        }
    }

    /// <summary>Opens the log of a project and starts following it.</summary>
    /// <param name="app">The application the view opens in.</param>
    /// <param name="project">Whose log to follow.</param>
    /// <param name="installed">The autostart entry, which says where a manifest-serving broker logs.</param>
    /// <param name="filter">Only lines containing this, or everything when blank.</param>
    /// <param name="said">Told what to say when there is no log to follow.</param>
    /// <returns>The view and its session, or null when there was nothing to follow.</returns>
    internal static (LogView View, SessionToken? Token)? Open(
        IApplication app,
        ProjectSettings project,
        InstalledEntry? installed,
        string filter,
        Action<string> said)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(said);

        var path = LogTail.For(project, installed);

        if (path is null || !File.Exists(path))
        {
            said(
                $"{NoLog} ({path ?? "nowhere derivable"}). A broker started with --log elsewhere "
                + "writes elsewhere, and the manifest does not record where.");

            return null;
        }

        var view = new LogView(Following + path);
        var token = Prompting.Open(app, view);

        view.Start(path, filter);

        return (view, token);
    }

    /// <summary>Begins following a file.</summary>
    /// <param name="path">The file to follow.</param>
    /// <param name="filter">Only lines containing this, or everything when blank.</param>
    internal void Start(string path, string filter) =>
        _watching = LogTail.FollowAsync(Append, path, filter, _stopping.Token);

    /// <summary>Stops following and waits for the reader to let the file go.</summary>
    /// <returns>When it has stopped.</returns>
    /// <remarks>
    /// Awaited rather than abandoned. A follow left running holds the log open while the token it
    /// watches is disposed underneath it, and the loop then faults on a disposed source with
    /// nobody observing it — which is how two probes in this repository hung for ten minutes
    /// instead of failing.
    /// </remarks>
    internal async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _watching.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The ordinary way out.
        }
    }

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key == Key.Esc)
        {
            Closing?.Invoke(this, EventArgs.Empty);

            return true;
        }

        return base.OnKeyDown(key);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Guarded for the reason Shell's is: the application disposes the view it was running and
        // the caller may hold it too, and an unguarded Cancel on a disposed source throws while
        // somebody is closing a log.
        if (disposing && !_closed)
        {
            _closed = true;

            _stopping.Cancel();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Raised when the person presses Escape.</summary>
    internal event EventHandler? Closing;

    /// <summary>
    /// Takes a line the follower read, to be shown on the next draw.
    /// </summary>
    /// <param name="line">What the broker wrote.</param>
    /// <remarks>
    /// <b>It does not touch the view, and that is the whole of it.</b> The follower runs off the
    /// main loop and the toolkit draws on it; a source collection mutated during a draw throws or
    /// is read half-written, and either way the failure lands in the draw rather than anywhere
    /// near its cause. An earlier version added straight to the bound collection and set the
    /// selection from the follower's thread, which is that bug written twice.
    /// </remarks>
    internal void Append(string line)
    {
        lock (_pending)
        {
            _pending.Add(line);
        }
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        Drain();

        return base.OnDrawingContent(context);
    }

    /// <summary>Moves whatever the follower has read into the view, on the drawing thread.</summary>
    private void Drain()
    {
        List<string> taken;

        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            taken = [.. _pending];
            _pending.Clear();
        }

        foreach (var line in taken)
        {
            _lines.Add(line);
        }

        // Kept on the newest, which is what a person following a log is watching for.
        _list.SelectedItem = _lines.Count - 1;
    }
}
