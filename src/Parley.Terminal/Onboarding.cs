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
    /// <remarks>
    /// It was "Enrolment", which names the act without saying what it does. The owner went looking for a way to
    /// scan a folder for agents and did not recognise it by that name. The words are the owner's.
    /// </remarks>
    internal const string Title = "Enroll agents from folders";

    /// <summary>What the window listing the repositories is called.</summary>
    /// <param name="project">The project they would join, or null when none is named.</param>
    /// <returns>The caption.</returns>
    /// <remarks>
    /// The project first, the owner's call of 2026-09-27: ticking a repository makes it an agent of one
    /// particular project, and the caption is the one thing on that window certain to be read.
    /// </remarks>
    internal static string Caption(ProjectSettings? project) =>
        project is { } named ? $"{named.Name.Value}: {Title}" : Title;

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
    /// <param name="saying">Told, per agent, what is happening to it while the enrolment runs, or null.</param>
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
        Func<string, Task> done,
        Action<string, string>? saying = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(remember);

        // **A picker rather than a field**, which is the whole reason this belongs here. Enrolling
        // nineteen repositories used to mean typing an absolute path that could only be reported
        // wrong afterwards; the same argument `Adding` already records for adding one.
        var browsing = Browsing.ForFolderOfRepositories(Browsing.Opening(lastBrowsed, startedIn));
        var token = Prompting.Open(app, browsing);

        browsing.Done += (_, directory) =>
        {
            Prompting.Close(app, token);

            if (directory is null)
            {
                _ = done(string.Empty);

                return;
            }

            Offer(app, directory, project, deployment, manifestPath, remember, queue, done, saying);
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
        Action<string> remember,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        Action<string, string>? saying)
    {
        var found = EnrollCommand.Find(directory);

        if (found.Count == 0)
        {
            _ = done($"No git repositories directly under {Safe.Text(directory)}.");

            return;
        }

        // **A repository inside the folder is remembered, not the folder.** What is remembered is shared with
        // adding one repository, and the next picker opens beside it -- at its parent. Remembering the folder
        // itself put the next picker one level above where the repositories are, for both flows.
        remember(found[0].Workspace);

        // **Nothing starts ticked.** `--all` on the command line is for scripts; a person looking at
        // a list of thirty has not yet said yes to any of them, and a pre-ticked list is a default
        // that enrols whatever they did not notice.
        var rows = found
            .Select(entry => new Tickable(entry.Agent, Note(entry), Ticked: false, Owns(entry)))
            .ToList();

        var ticking = new TickDialog(
            Caption(project),
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

            Confirm(app, wanted, project, deployment, manifestPath, queue, done, saying);
        };
    }

    /// <summary>What is worth knowing about a repository before ticking it.</summary>
    /// <remarks>
    /// <para>
    /// Read off the row the command produced rather than worked out here, so the note and what the
    /// command will do cannot disagree. **That sentence was aspirational until 2026-09-26**: this
    /// read <c>Owns</c> and nothing else, so the command's own picker said "worktree of another
    /// checkout" and this surface said nothing. The evidence a scan gathers is the repository's own
    /// text, so it is neutralised: <see cref="Safe"/> exists because a control character in somebody
    /// else's file can otherwise draw whatever it likes on this screen.
    /// </para>
    /// <para>
    /// Internal rather than private so that a test can press on the composition itself. A test that
    /// built the row from <see cref="EnrollmentEntry.Note"/> and read the screen would pass whatever
    /// this method did — which is how the defect survived: the value was available and this composed
    /// something else.
    /// </para>
    /// </remarks>
    internal static string Note(EnrollmentEntry entry)
    {
        // **The row's own note first, and this surface had none of them.** The remark above claimed
        // the note was read off the row so that it and the command could not disagree; it read
        // `Owns` and nothing else, while the command's own picker said "worktree of another
        // checkout", "already an agent" or "has an .mcp.json". So a worktree — the same repository
        // at another path, whose `.mcp.json` is a tracked file — was called out on the command line
        // and offered silently here, which is the surface people enrol from.
        //
        // Not neutralised: it is one of three fixed phrases this program chooses. The owns line is its
        // own column now, in `Owns`, so this column holds only those phrases and lines up.
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Note;
    }

    /// <summary>What a repository says it owns, for its own column, or empty.</summary>
    /// <remarks>
    /// Neutralised, because it is the repository's own text: <see cref="Safe"/> exists because a control
    /// character in somebody else's file can otherwise draw whatever it likes on this screen. It was
    /// joined onto the note, which put it at a different column on every row; the grid gives it its own.
    /// </remarks>
    internal static string Owns(EnrollmentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Owns is { Length: > 0 } owns ? Safe.Text(owns) : string.Empty;
    }

    /// <summary>States what enrolling costs, then does it.</summary>
    private static void Confirm(
        IApplication app,
        IReadOnlyList<EnrollmentEntry> wanted,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        string? manifestPath,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        Action<string, string>? saying)
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

            // **Every agent chosen is shown as starting the moment the person agrees**, not when the work
            // reaches the front of the queue. What follows can take the better part of a minute -- each agent
            // is started and waited for in turn -- and the owner's report of 2026-09-27 was exactly that:
            // enrolled four, and nothing happened for a long time. Shown on each agent's row, on the owner's
            // call of 2026-09-28, rather than as a sentence on the message line.
            foreach (var entry in wanted)
            {
                saying?.Invoke(entry.Agent, Waiting);
            }

            queue(cancellationToken =>
                RunAsync(wanted, project, deployment, manifestPath, done, saying, cancellationToken));
        };
    }

    /// <summary>What an agent's row says between the person agreeing and its turn to be started.</summary>
    internal const string Waiting = "queued";

    /// <summary>Runs the enrolment and says, in one line, what came of it.</summary>
    /// <remarks>
    /// <para>
    /// <b>It reprinted everything the command wrote for a shell, and that was the defect.</b> The catalogue
    /// path, the consent paragraph already agreed to on the screen before, and a line for every file in every
    /// repository, joined into one paragraph and cut off. The owner's call of 2026-09-27: say what happened.
    /// Which agents joined, and whether each came up; the agents pane beside it shows the rest.
    /// </para>
    /// <para>
    /// A failure still says why, in the command's own words: that is the one thing a person needs when it
    /// did not work, and a summary of it would take away which rule was broken.
    /// </para>
    /// </remarks>
    private static async Task RunAsync(
        IReadOnlyList<EnrollmentEntry> wanted,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        string? manifestPath,
        Func<string, Task> done,
        Action<string, string>? saying,
        CancellationToken cancellationToken)
    {
        var said = new StringWriter();
        var complained = new StringWriter();

        // The node catalogue is resolved the way every other command resolves it, so a machine
        // configured by environment or by project gets the same answer here as from a shell.
        var nodeConfig = ProjectResolution.NodeConfig(
            null, Environment.GetEnvironmentVariable, manifestPath);

        // The command says which agent it is starting and what it is waiting on, as it happens.
        var outcome = new EnrolmentOutcome(saying);

        var code = await EnrollCommand
            .EnrolAsync(
                wanted,
                describe: false,
                nodeConfig,
                said,
                complained,
                project,
                deployment,
                manifestPath,
                cancellationToken,
                outcome)
            .ConfigureAwait(false);

        await done(Summary(code, outcome, complained.ToString())).ConfigureAwait(false);
    }

    /// <summary>What the message line says when an enrolment is over: nothing, unless it failed.</summary>
    /// <param name="code">The command's exit code.</param>
    /// <param name="outcome">What it recorded.</param>
    /// <param name="complained">What it wrote as errors.</param>
    /// <returns>Why nothing was enrolled, or empty when something was.</returns>
    /// <remarks>
    /// <para>
    /// <b>A success says nothing here, on the owner's call of 2026-09-28.</b> Each agent's row showed what was
    /// happening to it while it happened, and the list drawn afterwards shows where each one landed -- running,
    /// or never run yet. A sentence under all of that restating it is the clutter the owner asked to be rid of.
    /// </para>
    /// <para>
    /// A failure still says why, in the command's own words: nothing is on any row to say it, and a summary of
    /// it would take away which rule was broken. Joined through <see cref="Reports.Sentences"/>, never with a
    /// line break: the message line makes every control character visible on purpose.
    /// </para>
    /// </remarks>
    internal static string Summary(int code, EnrolmentOutcome outcome, string complained)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(complained);

        if (code == 0 && outcome.EnrolledAgents.Count > 0)
        {
            return string.Empty;
        }

        var why = complained
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .ToList();

        return Reports.Sentences(why.Count > 0 ? [.. why, "Nothing was enrolled."] : ["Nothing was enrolled."]);
    }
}
