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
    internal const string NoLog = "No log where this project's broker usually writes one";

    /// <summary>What is said above the lines.</summary>
    internal const string Following = "Following ";

    private readonly ObservableCollection<string> _lines = [];
    private readonly ListView _list;
    private readonly CancellationTokenSource _stopping = new();

    private Task _watching = Task.CompletedTask;

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
    internal IReadOnlyList<string> Lines => [.. _lines];

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
        if (disposing)
        {
            _stopping.Cancel();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Raised when the person presses Escape.</summary>
    internal event EventHandler? Closing;

    /// <summary>Adds a line and keeps the view on the newest.</summary>
    /// <param name="line">What the broker wrote.</param>
    /// <remarks>
    /// The follow loop runs off the main loop, so the list is appended to from another thread. The
    /// collection is the list's source and the toolkit reads it when it draws, which is why the
    /// append is locked and the redraw is left to the next draw rather than forced from here.
    /// </remarks>
    private void Append(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }

        _list.SelectedItem = _lines.Count - 1;
    }
}
