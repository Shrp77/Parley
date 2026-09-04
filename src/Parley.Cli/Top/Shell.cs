using Parley.Client;
using Parley.Core;
using Parley.Headless;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// The whole surface: four regions, a status line, and the reads that fill them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything at once, rather than one question at a time.</b> 004's terminal was a sequence
/// of prompts and read as a wizard; what a person looking at a deployment wants is the projects,
/// the agents and a conversation in front of them together, and to act on what is in front of
/// them. That is the whole reason this feature exists.
/// </para>
/// <para>
/// <b>Side by side above the threshold, tabbed below it.</b> The same four view objects either
/// way, so a test that found a region by its title finds it in both — and the 004 defect this
/// guards against was found by a width test rather than by looking: a recipient column was
/// silently dropped at forty columns and nothing said so.
/// </para>
/// <para>
/// <b>The reads are the caller's to trigger.</b> Nothing here polls. A surface that refreshed on
/// a timer would move somebody's selection under their hand, and the one thing that must survive
/// a redraw is where the person is — which is why each region keeps its selection by name rather
/// than by position.
/// </para>
/// </remarks>
internal sealed class Shell : Window
{
    /// <summary>The width at which the four regions stop fitting side by side.</summary>
    /// <remarks>
    /// Eighty columns, and a test drives seventy-nine and eighty rather than trusting it. The
    /// number is a constant rather than a calculation because a layout that rearranged itself at
    /// a width nobody could name would be untestable at its own boundary.
    /// </remarks>
    internal const int Threshold = 80;

    /// <summary>What the status line always says, before the region's name.</summary>
    internal const string FocusPrefix = "Focus: ";

    private readonly BrokerDirectory _directory;
    private readonly Label _status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private readonly List<View> _regions;
    private readonly CancellationTokenSource _reading = new();

    private bool _stacked;
    private Task _work = Task.CompletedTask;

    internal Shell(BrokerDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _directory = directory;

        Title = "parley";
        BorderStyle = LineStyle.None;

        Projects = new ProjectsRegion();
        Agents = new AgentsRegion();
        Conversations = new ConversationsRegion();
        Transcript = new TranscriptRegion();

        _regions = [Projects, Agents, Conversations, Transcript];

        Projects.Chose += (_, project) => Queue(token => SelectAsync(project, token));
        Conversations.Opened += (_, conversation) => Queue(token =>
            Projects.Selected is { } project
                ? OpenAsync(project, conversation.Id, token)
                : Task.CompletedTask);

        Add(_status);
        Lay(Threshold);
        Focus(0);
    }

    /// <summary>The projects region.</summary>
    internal ProjectsRegion Projects { get; }

    /// <summary>The agents region.</summary>
    internal AgentsRegion Agents { get; }

    /// <summary>The conversations region.</summary>
    internal ConversationsRegion Conversations { get; }

    /// <summary>The transcript region.</summary>
    internal TranscriptRegion Transcript { get; }

    /// <summary>Which region has focus.</summary>
    internal int FocusedRegion { get; private set; }

    /// <summary>Whether the regions are stacked as tabs rather than laid side by side.</summary>
    internal bool Stacked => _stacked;

    /// <summary>What the status line says.</summary>
    internal string Status => _status.Text;

    /// <summary>
    /// Every read a keystroke started, so a caller can wait for the screen to be true.
    /// </summary>
    /// <remarks>
    /// A key press is synchronous and the read it causes is not. Production does not care — the
    /// region redraws when the read returns — but a test that asserted on the screen immediately
    /// after pressing Enter would be asserting on the screen before the answer arrived, and would
    /// pass or fail by timing. Awaiting this is what makes a driven journey deterministic.
    /// </remarks>
    internal Task Settled => _work;

    /// <summary>How long a read started by a keystroke may take before it is abandoned.</summary>
    /// <remarks>
    /// Only relevant to the token this hands its own reads. A broker that never answers is already
    /// contained by <see cref="BrokerDirectory.ReadAsync{T}"/>; this exists so a surface being torn
    /// down does not wait on one.
    /// </remarks>
    internal CancellationToken Reading => _reading.Token;

    /// <summary>Arranges the regions for a width.</summary>
    /// <param name="width">How wide the screen is.</param>
    /// <remarks>
    /// The four views are reparented rather than rebuilt, so a selection, a scroll position and a
    /// test's reference to a region all survive a resize. Rebuilding them would lose the person's
    /// place every time somebody dragged a window edge.
    /// </remarks>
    internal void Lay(int width)
    {
        var stacked = width < Threshold;

        if (stacked == _stacked && SubViews.Count > 1)
        {
            return;
        }

        _stacked = stacked;

        foreach (var region in _regions)
        {
            region.SuperView?.Remove(region);
        }

        if (stacked)
        {
            // One at a time, each filling the width, rather than pages of a tab control.
            //
            // **The toolkit's tab strip was tried first and is what the specification forbids.**
            // Four titles across the top of a forty-column terminal do not fit, and what the strip
            // does about that is cut them: `Conversations` became `Conversat`, silently. Running
            // the strip down the side writes each title one letter per row, which is worse. So the
            // region fills the space, its own frame carries its title in full, and the status line
            // says which of the four it is and how to reach the rest.
            foreach (var region in _regions)
            {
                region.X = 0;
                region.Y = 0;
                region.Width = Dim.Fill();
                region.Height = Dim.Fill(1);
            }

            Add(Projects, Agents, Conversations, Transcript);
        }
        else
        {
            // Projects on the left, agents over conversations in the middle, the transcript on
            // the right. The transcript is widest because it is the only region holding prose.
            Projects.X = 0;
            Projects.Y = 0;
            Projects.Width = Dim.Percent(25);
            Projects.Height = Dim.Fill(1);

            Agents.X = Pos.Right(Projects);
            Agents.Y = 0;
            Agents.Width = Dim.Percent(35);
            Agents.Height = Dim.Percent(50);

            Conversations.X = Pos.Right(Projects);
            Conversations.Y = Pos.Bottom(Agents);
            Conversations.Width = Dim.Percent(35);
            Conversations.Height = Dim.Fill(1);

            Transcript.X = Pos.Right(Agents);
            Transcript.Y = 0;
            Transcript.Width = Dim.Fill();
            Transcript.Height = Dim.Fill(1);

            Add(Projects, Agents, Conversations, Transcript);
        }

        Focus(FocusedRegion);
    }

    /// <summary>Moves focus to a region and says so.</summary>
    /// <param name="at">Which region, counted from the projects.</param>
    internal void Focus(int at)
    {
        FocusedRegion = ((at % _regions.Count) + _regions.Count) % _regions.Count;

        var region = _regions[FocusedRegion];

        if (_stacked)
        {
            foreach (var other in _regions)
            {
                other.Visible = ReferenceEquals(other, region);
            }
        }

        region.SetFocus();

        // Stacked, the line says which of how many as well as which. A person who has pulled their
        // window in can otherwise see no evidence that the surface has four regions at all, which
        // reads as three of them having been lost to the resize. Naming the other three outright
        // does not fit in forty columns, and a status line clipped mid-word would be the same
        // silent truncation this layout exists to avoid.
        _status.Text = _stacked
            ? $"{FocusPrefix}{NameOf(region)} ({FocusedRegion + 1} of {_regions.Count}, Tab)"
            : FocusPrefix + NameOf(region);
    }

    /// <summary>Moves focus to the next region, or the previous one.</summary>
    /// <param name="forwards">Whether to go forwards.</param>
    internal void Move(bool forwards) => Focus(FocusedRegion + (forwards ? 1 : -1));

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Taken here rather than left to the toolkit's own navigation, which walks every focusable
        // view including the list inside a region. What a person means by Tab on this surface is
        // "the next region", and a Tab that stepped into a list and then out of it again would
        // visit eight places to reach four.
        if (key == Key.Tab)
        {
            Move(forwards: true);

            return true;
        }

        if (key == Key.Tab.WithShift)
        {
            Move(forwards: false);

            return true;
        }

        return base.OnKeyDown(key);
    }

    /// <summary>Reads the manifest and every broker, and draws the projects.</summary>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When the projects region has been drawn.</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var projects = _directory.Projects;
        var health = await HealthAsync(projects, cancellationToken).ConfigureAwait(false);

        Projects.Show(projects, health, _directory.ManifestProblem);

        if (Projects.Selected is { } selected)
        {
            await SelectAsync(selected, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one project and draws its agents and conversations.</summary>
    /// <param name="project">The project to read.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When both regions have been drawn.</returns>
    /// <remarks>
    /// The two reads are contained separately. A registry that answers and a conversation list
    /// that does not is a real state — the broker restarting between two calls — and a region
    /// that showed nothing because its neighbour failed would be hiding what it does know.
    /// </remarks>
    internal async Task SelectAsync(ProjectSettings project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var client = new BrokerClient(_directory.ClientFor(project));

        var registry = await BrokerDirectory.ReadAsync(async () =>
            await client.RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        var conversations = await BrokerDirectory.ReadAsync(async () =>
            await client.ConversationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        Agents.Show(registry.Value, Catalogue(project), registry.Problem);
        Conversations.Show(conversations.Value?.Items, conversations.Problem);
        Transcript.Clear();
    }

    /// <summary>Reads one conversation and draws it.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When the transcript region has been drawn.</returns>
    internal async Task OpenAsync(
        ProjectSettings project,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_directory.ClientFor(project))
                .ExportAsync(conversationId, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        Transcript.Show(conversationId, project.Name.Value, read.Value, read.Problem);
    }

    /// <summary>
    /// Reads the project's catalogue, which is the one fact about a deployment the broker never
    /// sees.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <returns>What this machine could start for it, or an empty catalogue.</returns>
    /// <remarks>
    /// Read fresh on every draw rather than held. Somebody may edit it by hand while this is open,
    /// and an agents region showing a catalogue from a minute ago would report an agent that is no
    /// longer startable as though it were.
    /// </remarks>
    private static NodeCatalogue Catalogue(ProjectSettings project) =>
        project.NodeConfig is { Length: > 0 } path
            && NodeCatalogue.TryReadFrom(path, out var catalogue, out _)
            ? catalogue
            : NodeCatalogue.Empty;

    /// <summary>Asks every project's broker what it speaks, all at once.</summary>
    /// <param name="projects">The projects to ask.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>What each said, by project name.</returns>
    private async Task<IReadOnlyDictionary<string, ProjectHealth>> HealthAsync(
        IReadOnlyList<ProjectSettings> projects,
        CancellationToken cancellationToken)
    {
        var asked = projects.Select(async project =>
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(_directory.ClientFor(project))
                    .ImplementsAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            return (project.Name.Value, Health: new ProjectHealth(read.Value, read.Problem));
        });

        var answers = await Task.WhenAll(asked).ConfigureAwait(false);

        return answers.ToDictionary(a => a.Value, a => a.Health, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Cancelled before the base disposes the views, so a read still in flight cannot draw
            // into a region that has gone. The seat book's streams are not this object's to end;
            // TopCommand owns those and ends them when the process does.
            _reading.Cancel();
            _reading.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>A region's title, which is also what the status line calls it.</summary>
    /// <param name="region">The region.</param>
    /// <returns>Its title.</returns>
    private static string NameOf(View region) => region.Title;

    /// <summary>Runs a read after whatever is already running, and remembers it.</summary>
    /// <param name="read">The read to run.</param>
    /// <remarks>
    /// <para>
    /// Chained rather than run at once, and that is the fix rather than the tidy version. Two
    /// reads racing means the slower one draws last: hold an arrow key down the projects list and
    /// the agents region ends up showing whichever project's broker happened to answer last, which
    /// is not the one the highlight is on. Ordering them means the last key pressed is the last
    /// thing drawn.
    /// </para>
    /// <para>
    /// A read that faults is swallowed here on purpose. Every broker read is already contained by
    /// <see cref="BrokerDirectory.ReadAsync{T}"/>, so what could reach this is a defect rather
    /// than an unreachable broker — and taking the surface down over one region's redraw is the
    /// failure a person can do least about.
    /// </para>
    /// </remarks>
    private void Queue(Func<CancellationToken, Task> read) =>
        _work = _work.ContinueWith(
            async _ =>
            {
                try
                {
                    await read(_reading.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The surface is going away.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
}
