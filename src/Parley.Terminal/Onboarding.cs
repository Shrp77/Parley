using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Enrolling a folder of repositories, from inside the surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>It calls <see cref="EnrollCommand"/> rather than reimplementing enrolment</b>, so what is
/// written is what the command writes. What this surface supplies is the choosing.
/// </para>
/// <para>
/// <b>This was three steps and is one</b> (FR-040). It asked for a directory and a manifest path in
/// a text field, wrote a file, asked for that path again to plan, and again to apply — on a
/// full-screen surface with a folder picker and a list on it. The file it round-tripped through
/// existed so a person could type what each repository owns, and that question is asked of the
/// agent now rather than filled in from memory, so nothing is lost by removing it.
/// </para>
/// <para>
/// <b>Enrolling is the consent, and the door says so before anything is written</b> (FR-043,
/// FR-044). A catalogued repository can be started by a peer's request with nobody watching, which
/// spends the owner's tokens — so the count is named and the sentence is plain. The alternative,
/// a per-repository opt-in defaulting to off, is what produced nineteen repositories that were
/// enrolled, addressable and unstartable: an agent that cannot be started cannot answer.
/// </para>
/// </remarks>
internal static class Onboarding
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Enrolment";

    /// <summary>What the confirmation offers.</summary>
    internal const string Apply = "Enrol them";

    /// <summary>Opens the enrolment flow.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="project">The project being enrolled into, or null when none is named.</param>
    /// <param name="deployment">Every project this machine describes.</param>
    /// <param name="manifestPath">Where this machine's projects live.</param>
    /// <param name="startedIn">Where the tool was started, so the picker opens somewhere useful.</param>
    /// <param name="lastBrowsed">The folder last browsed to, or null.</param>
    /// <param name="remember">Told where the person browsed, so the next run starts there.</param>
    /// <param name="queue">Runs the command, so the caller can wait for it.</param>
    /// <param name="done">Told what the command said.</param>
    /// <returns>When the first dialog is open.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal static Task OpenAsync(
        IApplication app,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        string? manifestPath,
        string? startedIn,
        string? lastBrowsed,
        Action<string> remember,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(remember);

        // **A picker rather than a field**, which is the whole reason this belongs here. Enrolling
        // nineteen repositories used to mean typing an absolute path that could only be reported
        // wrong afterwards; the same argument `Adding` already records for adding one.
        var browsing = new Browsing(Browsing.Opening(lastBrowsed, startedIn));
        var token = Prompting.Open(app, browsing);

        browsing.Done += (_, directory) =>
        {
            Prompting.Close(app, token);

            if (directory is null)
            {
                _ = done(string.Empty);

                return;
            }

            remember(directory);
            Offer(app, directory, project, deployment, manifestPath, queue, done);
        };

        return Task.CompletedTask;
    }

    /// <summary>Shows what was found and takes the ones to enrol.</summary>
    private static void Offer(
        IApplication app,
        string directory,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        string? manifestPath,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var found = EnrollCommand.Find(directory);

        if (found.Count == 0)
        {
            _ = done($"No git repositories directly under {Safe.Text(directory)}.");

            return;
        }

        // **Nothing starts ticked.** `--all` on the command line is for scripts; a person looking at
        // a list of thirty has not yet said yes to any of them, and a pre-ticked list is a default
        // that enrols whatever they did not notice.
        var rows = found
            .Select(entry => new Tickable(entry.Agent, Note(entry), Ticked: false))
            .ToList();

        var ticking = new TickDialog(
            Title,
            $"{found.Count} repositories under {Safe.Text(directory)}. Tick the ones that should "
            + "become agents"
            + (project is { } named ? $" of {named.Name.Value}" : string.Empty)
            + ".",
            rows);

        var token = Prompting.Open(app, ticking);

        ticking.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen is null)
            {
                _ = done(string.Empty);

                return;
            }

            var wanted = chosen
                .Select((row, index) => (row, index))
                .Where(pair => pair.row.Ticked)
                .Select(pair => found[pair.index] with { Enroll = true })
                .ToArray();

            if (wanted.Length == 0)
            {
                _ = done("Nothing was ticked, so nothing was enrolled.");

                return;
            }

            Confirm(app, wanted, project, deployment, manifestPath, queue, done);
        };
    }

    /// <summary>What is worth knowing about a repository before ticking it.</summary>
    /// <remarks>
    /// Read off the row the command produced rather than worked out here, so the note and what the
    /// command will do cannot disagree. The evidence a scan gathers is the repository's own text,
    /// so it is neutralised: <see cref="Safe"/> exists because a control character in somebody
    /// else's file can otherwise draw whatever it likes on this screen.
    /// </remarks>
    private static string Note(EnrollmentEntry entry) =>
        entry.Owns is { Length: > 0 } owns
            ? Safe.Text($"owns: {owns}")
            : string.Empty;

    /// <summary>States what enrolling costs, then does it.</summary>
    private static void Confirm(
        IApplication app,
        IReadOnlyList<EnrollmentEntry> wanted,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        string? manifestPath,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        // FR-044: said before anything is configured, and it names the number. "Some repositories"
        // is a sentence somebody agrees to without weighing; a count is one they can.
        //
        // **The sentence moved to `EnrolmentConsent` when `parley enroll` gained it too.** This
        // surface had it and the command line said only how many repositories it had found, so one
        // act carried its consequence on one surface and not the other — and the surface without it
        // is the one a script reaches. Two spellings would drift, and the half that drifts is the
        // half nobody is reading at the time.
        var asking = Prompting.Choose(
            Apply,
            EnrolmentConsent.Says(wanted.Count, project?.Name),
            ["Not now", Apply]);

        var token = Prompting.Open(app, asking);

        asking.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Apply)
            {
                _ = done(string.Empty);

                return;
            }

            queue(cancellationToken =>
                RunAsync(wanted, project, deployment, manifestPath, done, cancellationToken));
        };
    }

    /// <summary>Runs the enrolment and reports what it said, in its own words.</summary>
    private static async Task RunAsync(
        IReadOnlyList<EnrollmentEntry> wanted,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        string? manifestPath,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        var said = new StringWriter();
        var complained = new StringWriter();

        // The node catalogue is resolved the way every other command resolves it, so a machine
        // configured by environment or by project gets the same answer here as from a shell.
        var nodeConfig = ProjectResolution.NodeConfig(
            null, Environment.GetEnvironmentVariable, manifestPath);

        _ = await EnrollCommand
            .EnrolAsync(
                wanted,
                describe: false,
                nodeConfig,
                said,
                complained,
                project,
                deployment,
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);

        var lines = (said.ToString() + complained)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .ToList();

        // **Joined through `Reports.Sentences`, never `string.Join` with a line break.** The message
        // line makes every control character visible on purpose, so a report assembled with
        // `Environment.NewLine` draws as a row of replacement characters — which is what five
        // screens did until 0.28.1, and what a test now reads these sources for.
        await done(Reports.Sentences(lines)).ConfigureAwait(false);
    }
}
