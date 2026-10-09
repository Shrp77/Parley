using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Making a repository on this machine an agent of the selected project.
/// </summary>
/// <remarks>
/// <para>
/// Three files, every one of which a person can read and revert: the repository's launch
/// configuration, naming the project and the role; its own instructions file, which gains the
/// section telling the agent how to use Parley; and a line in the project's catalogue so a node may
/// start it when a question arrives. All are written by <see cref="Enrolment"/>, which is the same
/// code <c>parley enroll apply</c> runs — FR-008 asks for one writer, and two would agree today and
/// differ in six months.
/// </para>
/// <para>
/// <b>The catalogue line is the one that comes and goes.</b> A repository another project already
/// starts unattended gets the other two and no catalogue entry: FR-013 admits it into as many
/// projects as it likes and lets only one of them start it, because a node starts a client with the
/// repository as its working directory and two of those is two models editing one checkout.
/// </para>
/// <para>
/// <b>It does not write the registration, and it cannot.</b> SPEC.md §12.3 makes registration the
/// adapter's own act on attach, and a surface that inserted a registry row on a repository's behalf
/// would be a caller-supplied identity by another name. What it does instead is ask the machine to
/// start the agent once and let it register itself — which is what §12.3's own closing paragraph
/// routes through the catalogue. <b>An earlier version of this paragraph said the agent was left
/// <c>catalogued, not yet registered</c> and that nothing could be done about it</b>; that was true
/// of 005 and stopped being true at 0.24.0, and it is corrected here rather than deleted because
/// the half of it that still holds — nothing here may claim to be anybody — is the reason the start
/// is a request rather than a write.
/// </para>
/// <para>
/// <b>Nothing is written until the person has seen what would be.</b> The paths are shown and
/// confirmed, because this writes into a repository that is not the one they are looking at, and
/// those files are the whole of what makes an agent an agent.
/// </para>
/// </remarks>
internal static class Adding
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Add an agent to this project";

    /// <summary>What the confirmation is called.</summary>
    /// <remarks>
    /// It said <c>Write these two files?</c> and three were written. The count is gone rather than
    /// corrected, because it is two or three depending on whether another project already starts
    /// the repository — and a title that has to be kept in step with a branch in the description
    /// is a title that will fall out of step with it.
    /// </remarks>
    internal const string ConfirmTitle = "Write these files?";

    /// <summary>The choice that goes ahead.</summary>
    internal const string Write = "Write them";

    /// <summary>The choice that does not.</summary>
    internal const string NotNow = "Not now";

    /// <summary>What is said when nothing was written.</summary>
    internal const string NothingWritten = "Nothing was written.";

    /// <summary>What is said when something was.</summary>
    internal const string Added = "Added ";

    /// <summary>Why a directory cannot be made an agent, or null when it can.</summary>
    /// <param name="workspace">What was typed.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// <para>
    /// Only the facts a directory carries. Whether the catalogue already names the role is checked
    /// against the catalogue, and whether the launch configuration belongs to another project is
    /// checked by <c>EnrollmentPlan.Describe</c> at the moment of writing — both are files that can
    /// change between a keystroke and a confirmation, and checking them here would be checking them
    /// at the wrong time.
    /// </para>
    /// <para>
    /// A worktree is said rather than refused, as the enrol command says it: a worktree is the same
    /// repository at another path, so this is almost certainly a mistake — but it is the person's
    /// directory and there may be a reading of it nobody here has thought of.
    /// </para>
    /// </remarks>
    internal static string? WorkspaceProblem(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return "Name a repository directory on this machine.";
        }

        if (!Directory.Exists(workspace))
        {
            return $"There is no directory at {workspace}.";
        }

        var git = Path.Combine(workspace, ".git");

        return Directory.Exists(git) || File.Exists(git)
            ? null
            : $"{workspace} is not a repository: it holds no .git.";
    }

    /// <summary>Why a role cannot be used, or null when it can.</summary>
    /// <param name="role">What was typed.</param>
    /// <returns>The reason, or null.</returns>
    internal static string? RoleProblem(string role) =>
        EnrollmentPlan.IsWellFormedName(role)
            ? null
            : "lowercase letters, digits and hyphens, starting with a letter";

    /// <summary>Why an ownership line cannot be used, or null when it can.</summary>
    /// <param name="owns">What was typed.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// <para>
    /// Empty is legitimate and is not a gap: SPEC.md §17 question 10 allows an agent with no line,
    /// listed with none, and askers fall back to its name. What is refused is a line the schema
    /// would refuse, which would otherwise be met at registration — a long way from here, and
    /// looking like an agent that simply never appeared.
    /// </para>
    /// <para>
    /// <b>It asks <see cref="OwnershipStatement"/> rather than measuring a length itself.</b> It
    /// used to compare against <c>OwnsLimit</c> and check nothing else, which meant a statement
    /// carrying a line separator or a direction override passed here and was refused by the
    /// database — the two halves of one rule, applied in two places, agreeing only by luck. Since
    /// 0.33.0 removed the length bound the length half refuses nothing at all, so a field still
    /// checking only that would have been a field checking nothing.
    /// </para>
    /// </remarks>
    internal static string? OwnsProblem(string owns) =>
        OwnershipStatement.IsValid((owns ?? string.Empty).Trim(), out var error) ? null : error;

    /// <summary>What would be written, in the order it would be written.</summary>
    /// <param name="workspace">The repository.</param>
    /// <param name="catalogueFile">The project's catalogue.</param>
    /// <param name="project">The project it would join.</param>
    /// <returns>The paths, and what each would gain.</returns>
    /// <remarks>
    /// <b>No catalogue line when another project already starts this repository</b>, and that is
    /// the whole of what FR-013 costs. A repository may answer in several projects and may be
    /// started unattended by only one, because a node starts an agent with its workspace as the
    /// working directory and two of them is two models editing one checkout. So the second project
    /// gets the launch entry and the instructions section, and nothing in its catalogue.
    /// </remarks>
    internal static string Describe(
        string workspace, string catalogueFile, string project) =>
        $"{Safe.Text(Enrolment.LaunchPath(workspace))}{Environment.NewLine}"
        + $"    a parley server naming this repository, and the project '{Safe.Text(project)}'"
        + $"{Environment.NewLine}{Safe.Text(Path.Combine(workspace, Instructions))}{Environment.NewLine}"
        + $"    a '{EnrollmentPlan.PeersHeading}' section, if it has none, telling the agent how to "
        + "use Parley"
        + $"{Environment.NewLine}{Safe.Text(catalogueFile)}{Environment.NewLine}"
        + "    a line saying this machine may start it when a question arrives for it"
        + $"{Environment.NewLine}{Environment.NewLine}{Starts}";

    /// <summary>The file in a repository that tells its agent how to work.</summary>
    /// <remarks>
    /// <b>Found by lens 2: this was written and never described.</b> Enrolment creates or appends
    /// to it, and the confirmation named two files — the launch configuration and the catalogue —
    /// under a title that said "these two files". Three were written. A surface that writes a file
    /// in somebody's repository without its description covering it is doing something its
    /// description did not cover, which is the rule this whole dialog exists for, and the
    /// repository is not the one the person is looking at.
    /// </remarks>
    internal const string Instructions = "CLAUDE.md";

    // **`Shared` was here, and 018 removed the state it described.** It explained why a second project got
    // a launch entry and no catalogue line: FR-013 let a repository answer in several projects and be
    // started unattended by only one, because two nodes launching one working directory is two models
    // editing one checkout. The catalogue is one file keyed by the agent's identity now, so every project
    // shares the one entry and there is no second case to explain.

    /// <summary>What is said about the process this is about to start.</summary>
    /// <remarks>
    /// <b>Named because it happens, not because it is dangerous.</b> Adding an agent now launches
    /// it once so that it registers itself, which is what makes it startable without somebody
    /// going and opening the repository by hand. That is a process appearing on the operator's
    /// machine, and a surface that starts one without saying so is a surface doing something its
    /// description did not cover.
    /// </remarks>
    internal const string Starts =
        "Then it is started once, so that it registers itself and can be started again later. "
        + "That runs the repository's own server on this machine.";

    /// <summary>
    /// Opens the dialog that adds an agent, and writes the files when it is confirmed.
    /// </summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="project">The project the repository would join.</param>
    /// <param name="catalogue">
    /// The project's catalogue, and a way to create one when it names none.
    /// </param>
    /// <param name="start">
    /// Asks the broker to launch the agent once it is written, or null when there is none to ask.
    /// </param>
    /// <param name="registered">
    /// Says whether the registry holds the agent now, or null when nothing can be asked.
    /// <b>Without it this screen could only predict.</b> A node reports that it launched
    /// something; whether the adapter attached and registered itself is a different question, and
    /// the registry is the only thing that answers it.
    /// </param>
    /// <param name="queue">Runs the writes, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <param name="lastAdded">
    /// The repository this session last added, so the search starts beside it.
    /// </param>
    /// <param name="remember">Told which repository was added, once the files are written.</param>
    /// <param name="startedIn">The folder the terminal was started from, where the picker opens before anything is added.</param>
    /// <param name="enrolling">
    /// Told what is happening to the agent being added, at each step, and null when it is over. Adding waits up to
    /// <see cref="Starting.Patience"/> for the agent to register, and without this the screen said nothing for all of it.
    /// </param>
    /// <returns>When the first dialog is open.</returns>
    /// <remarks>
    /// <b>The folder is found before it is named.</b> The owner was typing an absolute path per
    /// agent, into a field that could only report afterwards that one of them was not a directory —
    /// nineteen repositories is nineteen chances to mistype one. What is chosen fills the field in,
    /// and the field is still typed into: somebody who knows the path should not have to walk to it.
    /// </remarks>
    internal static Task OpenAsync(
        IApplication app,
        ProjectSettings project,
        Catalogue catalogue,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? start,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? registered,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        string? lastAdded = null,
        Action<string>? remember = null,
        string? startedIn = null,
        Func<string?, Task>? enrolling = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var browsing = new Browsing(Browsing.Opening(lastAdded, startedIn));
        var walk = Prompting.Open(app, browsing);

        browsing.Done += (_, folder) =>
        {
            Prompting.Close(app, walk);

            if (folder is null)
            {
                _ = done(string.Empty);

                return;
            }

            Ask(app, project, catalogue, folder, start, registered, queue, done, remember, enrolling);
        };

        return Task.CompletedTask;
    }

    /// <summary>Asks for the name and the ownership line, with the repository already known.</summary>
    private static void Ask(
        IApplication app,
        ProjectSettings project,
        Catalogue catalogue,
        string workspace,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? start,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? registered,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        Action<string>? remember,
        Func<string?, Task>? enrolling)
    {
        var form = new FormDialog(
            Title,
            [
                new FormField("Repository directory on this machine:", workspace, WorkspaceProblem),
                new FormField(
                    "The role peers will address it by — lowercase letters, digits and hyphens:",
                    string.Empty,
                    RoleProblem,
                    Correct: EnrollmentPlan.CorrectName,
                    Corrected: EnrollmentPlan.NameWasCorrected),
                new FormField(
                    "What it owns, and what it does not (optional, and never invented for you):",
                    string.Empty,
                    OwnsProblem,
                    Long: true),
            ]);

        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null)
            {
                _ = done(string.Empty);

                return;
            }

            Confirm(app, project, catalogue, values, start, registered, queue, done, remember, enrolling);
        };
    }

    /// <summary>Shows the two paths and writes them only if the person says so.</summary>
    private static void Confirm(
        IApplication app,
        ProjectSettings project,
        Catalogue catalogue,
        IReadOnlyList<string> values,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? start,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? registered,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        Action<string>? remember,
        Func<string?, Task>? enrolling)
    {
        var workspace = Path.GetFullPath(values[0].Trim());
        var role = values[1].Trim();

        var asking = Prompting.Choose(
            ConfirmTitle,
            Describe(workspace, catalogue.Proposed(), project.Name.Value)
            + Environment.NewLine
            + Environment.NewLine
            + (catalogue.Exists()
                ? string.Empty
                : $"This machine has no catalogue yet, so one is created at "
                  + $"{catalogue.Proposed()}. It is what starts every agent on this machine, in "
                  + "every project."),
            [NotNow, Write]);

        var token = Prompting.Open(app, asking);

        asking.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Write)
            {
                _ = done(NothingWritten);

                return;
            }

            queue(token2 => WriteAsync(
                project, catalogue, workspace, role, values[2].Trim(), start, registered, token2,
                done, remember, enrolling));
        };
    }

    /// <summary>Asks a broker to start the agent that was just added.</summary>
    /// <param name="start">Asks a project's broker, or null when there is none to ask.</param>
    /// <param name="registered">Asks whether the registry holds it, or null when nothing can.</param>
    /// <param name="role">The role that was added.</param>
    /// <param name="project">The project it joined.</param>
    /// <param name="deployment">Every project on the machine, so that one can be found.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>What to say about it.</returns>
    /// <remarks>
    /// <para>
    /// <b>A failure here is reported and never thrown.</b> The files are already written and the
    /// agent is already catalogued, so a broker that did not answer leaves an agent somebody can
    /// still start later -- exactly the state 005 shipped. Losing the whole enrolment because the
    /// last of three steps could not be taken would be the worse outcome.
    /// </para>
    /// <para>
    /// <b>The start goes to whichever project can actually take it, which is not always this
    /// one.</b> When another project holds the unattended consent, this project's node does not
    /// name the role and would refuse — so the request goes to the one that does. That is not a
    /// detour: the node starts a client in the repository's own directory, the client brings up
    /// every parley server in its launch configuration, and the entry just written is one of them.
    /// One start, two registrations.
    /// </para>
    /// </remarks>
    internal static async Task<string> StartAsync(
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? start,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? registered,
        string role,
        ProjectSettings project,
        IReadOnlyList<ProjectSettings> deployment,
        CancellationToken cancellationToken)
    {
        if (start is null)
        {
            return NotStarted;
        }

        // **One catalogue entry now, so the start goes to the project being added to.** It used to be
        // dispatched through whichever project held the consent, because only that project's node could
        // launch the checkout. Every project's node reads the same catalogue.
        var (asking, named) = (project, role);

        if (asking is null
            || !AgentName.TryCreate($"{named}@{MachineName.Current}", out var agent)
            || !AgentName.TryCreate($"{role}@{MachineName.Current}", out var added))
        {
            return NotStarted;
        }

        // **Asked, and then looked at.** This used to end here, with the node's answer and a
        // sentence promising what would happen next — "It registers itself as it attaches" — which
        // is a prediction rather than a result. The owner enrolled nineteen repositories, was told
        // each one had started, and found every one of them unaddressable: a node reports that it
        // launched something, and whether the adapter attached and registered is a question only
        // the registry answers. `parley enroll apply` has looked since 012 step 1; this did not.
        // **Started through one project, looked for in the other.** The start goes to the project whose node names this
        // repository, under the role it has there; the question this screen answers is whether the agent just added is
        // in the project it was added to, under the role it was added as. Asking the starting project about its own role
        // was always yes for a repository it already ran, so the owner was told "It can be asked and told now" about a
        // name the project it was added to had never seen — and the agents list, redrawn from that project, said
        // otherwise beside the claim.
        var was = await Starting.OnceAsync(
            agent,
            (who, token) => start(asking, who, token),
            registered is null
                ? (_, _) => Task.FromResult(ProjectRead<bool>.Unreachable(Unchecked))
                : (_, token) => registered(project, added, token),
            Starting.Patience,
            TimeProvider.System,
            cancellationToken,
            catchUp: Starting.CatchUp).ConfigureAwait(false);

        var through = $"'{role}'";

        // **Three answers, not two, and the third is the one that misled somebody.** Nothing
        // started is the old sentence unchanged; started and registered is the only case that may
        // say the agent can be addressed; started and not seen says exactly that and no more.
        return was switch
        {
            { Launched: false } => $"{Safe.Text(was.Says)} {NotStarted}",
            { Registered: true } =>
                $"Started {through} and it registered. It can be asked and told now.",
            _ => $"Started {through} — {Safe.Text(was.Says)}. {Later}",
        };
    }

    /// <summary>What is added when a repository another project runs was not seen registering in this one.</summary>
    /// <remarks>
    /// The likeliest reason, and the one thing a person can do about it. A session reads its launch configuration when it
    /// starts, so a session the other project already has running holds every server but the one just written, and a
    /// start through that project gives it a turn rather than a restart.
    /// </remarks>
    private const string Reload =
        "If that repository's session was already running, it loads the new entry only when it is restarted.";

    /// <summary>What is said when there is no broker to ask whether it registered.</summary>
    private const string Unchecked = "nothing here could ask the registry whether it did";

    /// <summary>What is said after a start whose registration was not seen.</summary>
    /// <remarks>
    /// <b>"I do not know", never "it did not happen".</b> 008 exists because a broker stopped
    /// waiting before a node had finished and recorded the opposite of what occurred, and an agent
    /// that registers a second after this stopped looking is working. What this says is what to do
    /// about either case, which is the same thing in both.
    /// </remarks>
    private const string Later =
        "Check the agents list shortly: until something has registered under this name, it cannot "
        + "be asked or told.";

    /// <summary>What is said when the start did not happen.</summary>
    /// <remarks>
    /// <para>
    /// <b>This said the next Request would start it, and that cannot happen.</b> Every message is
    /// refused for an unroutable recipient, and §14 counts a name nothing has
    /// registered as unroutable — so until something has attached under this name, no message
    /// reaches it and none of them starts it. The sentence sent a person to the one remedy that
    /// does not work.
    /// </para>
    /// <para>
    /// What does work is what §12.3 permits: an operator asking its machine to start it, which is
    /// what this screen just tried, and opening the repository so its own server attaches. Both end
    /// the same way — the adapter registers the agent, and from then on it can be asked and told
    /// like any other.
    /// </para>
    /// <para>
    /// The equivalent line in <c>parley node</c> is about an agent whose process has died and is
    /// correct: that one is registered, so a question does reach it and does start it. The two
    /// cases read alike and are not the same.
    /// </para>
    /// </remarks>
    internal const string NotStarted =
        "It was not started now. Opening its repository starts it, and so does starting it from "
        + "this screen. Until something has, it cannot be asked or told: a name nothing has "
        + "registered is not one messages can be addressed to.";

    /// <summary>Writes the files, creating the catalogue first when there is none.</summary>
    private static async Task WriteAsync(
        ProjectSettings project,
        Catalogue catalogue,
        string workspace,
        string role,
        string owns,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? start,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? registered,
        CancellationToken cancellationToken,
        Func<string, Task> done,
        Action<string>? remember,
        Func<string?, Task>? enrolling)
    {
        var said = new List<string>();
        var deployment = catalogue.Deployment();

        try
        {
            await Telling($"Adding '{role}': writing its files...").ConfigureAwait(false);

            var file = await catalogue.EnsureAsync(said, cancellationToken).ConfigureAwait(false);

            // **Unattended only when nothing else starts this repository.** FR-013 admits the
            // repository into a second project and refuses it a second unattended start, and this
            // is where the difference is made: asking for one here is what used to make the whole
            // enrolment throw, with no way past it from this screen.
            var entry = new EnrollmentEntry(
                workspace, role, owns.Length == 0 ? null : owns, Unattended: true)
            {
                Enroll = true,
                OwnsApproved = owns.Length > 0,
            };

            var outcome = await Enrolment.AddAsync(
                entry,
                project.Name.Value,
                file,
                Path.Combine(workspace, Instructions),
                cancellationToken,
                deployment,
                catalogue.ManifestPath).ConfigureAwait(false);

            // Remembered only once the files are on disk, so the next search opens beside a
            // repository that really is one of this project's agents.
            remember?.Invoke(workspace);

            said.Add($"{Added}'{role}'.");
            said.AddRange(outcome.Notes);
            said.AddRange(outcome.Written.Select(path => $"wrote {path}"));

            // **The step 005 said could not be taken.** Its planning was right that a surface must
            // not write a registration on a repository's behalf -- §12.3 forbids it and §16 makes
            // that the whole trust model -- and wrong that this left nothing to do. §12.3's own
            // closing paragraph routes spawn-on-demand through the manifest, which is what the
            // catalogue entry just written is, so the broker can be asked to launch the role and
            // the launched adapter registers itself under the identity its launch fixes.
            //
            // Nothing here claims to be anybody. The start is a request; the registration is the
            // agent's own act.
            await Telling(
                $"Adding '{role}': its files are written. Starting it and waiting up to "
                + $"{(int)Starting.Patience.TotalSeconds} seconds for it to register...").ConfigureAwait(false);

            said.Add(await StartAsync(start, registered, role, project, deployment, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (InterruptedEnrolment interrupted)
        {
            // **The one failure that arrives with files already changed, and the sentence below was
            // untrue on it.** The catch under this one said `Nothing was written.` for every failure
            // alike, so on this path the screen printed the list of changed files and then denied it
            // on the next line — and a report that asserts the opposite of what happened settles the
            // question wrongly, where one that merely omits leaves somebody uncertain.
            said.Add($"{role}: {interrupted.Message}");
            said.AddRange(interrupted.Written.Select(path => $"wrote {path}"));
        }
        catch (Exception failure) when (failure is InvalidDataException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or System.Text.Json.JsonException)
        {
            // The refusal in its own words, and nothing written. Every one of these is a file this
            // does not understand or cannot write, and the alternative to saying so is a partial
            // enrolment that reads as a working agent.
            said.Add($"{role}: {failure.Message}");
            said.Add(NothingWritten);
        }

        // Over, whichever way it ended: the line above the agents goes before the result is said.
        if (enrolling is not null)
        {
            await enrolling(null).ConfigureAwait(false);
        }

        await done(Reports.Sentences(said)).ConfigureAwait(false);

        Task Telling(string progress) => enrolling is null ? Task.CompletedTask : enrolling(progress);
    }
}

/// <summary>
/// This machine's catalogue: where it is, and how one is made when there is none yet.
/// </summary>
/// <param name="ManifestPath">The manifest this machine's projects are named in, or null.</param>
/// <param name="Given">The catalogue somebody named, or null to work it out from the manifest.</param>
/// <remarks>
/// <para>
/// <b>One catalogue per machine, beside the manifest</b> (018). It was one per project, recorded on the
/// project through the command that owns the manifest — and that binding is what made a checkout
/// startable in the project whose file named it and unstartable in every other, while being addressable
/// in all of them. Nothing is recorded on a project any more, so creating one no longer rewrites the
/// manifest and no longer restarts a broker.
/// </para>
/// <para>
/// <b>Beside the manifest rather than beside a store</b>, which is where the per-project one went. A
/// store belongs to one project and this file belongs to none of them; the manifest is the one file on
/// the machine that every project already shares, which is the same reason the enrolment gate takes its
/// lock. The node resolves it the same way, and it must: writing consent into a file the supervisor does
/// not read is the defect this feature exists to remove, and changing one side of that resolution alone
/// would reintroduce it in the opposite direction.
/// </para>
/// </remarks>
internal sealed record Catalogue(string? ManifestPath, string? Given = null)
{
    /// <summary>Every project on this machine, or none when the manifest cannot be read.</summary>
    /// <returns>The projects.</returns>
    /// <remarks>
    /// Read at the moment of writing rather than kept from the draw, for the reason the catalogue
    /// itself is: somebody may have added a project between the two, and what this is used for —
    /// deciding whether another project already starts this workspace unattended — is only true of
    /// the manifest as it is now. An unreadable manifest gives none, which says nothing rather than
    /// asserting there is nothing.
    /// </remarks>
    internal IReadOnlyList<ProjectSettings> Deployment() =>
        ManifestPath is { Length: > 0 } path && ProjectManifest.TryRead(path, out var projects, out _)
            ? projects
            : [];

    /// <summary>Whether this machine already has a catalogue.</summary>
    /// <returns>Whether the file is there.</returns>
    internal bool Exists() => File.Exists(Proposed());

    /// <summary>Where this machine's catalogue is, whether or not it has been written yet.</summary>
    /// <returns>The path.</returns>
    /// <remarks>
    /// What somebody named wins, then the file beside the manifest, then the bare name the node falls
    /// back to when there is no manifest at all — so the two always agree about which file consent is
    /// written into. **The environment is not read here**: whoever builds the surface resolves it and
    /// passes it in, because reading it from inside this call is what let the terminal's own tests
    /// enrol their temporary repositories into a real machine's catalogue.
    /// </remarks>
    internal string Proposed()
    {
        if (Given is { Length: > 0 } named)
        {
            return named;
        }

        if (ManifestPath is { Length: > 0 } manifest
            && Path.GetDirectoryName(Path.GetFullPath(manifest)) is { Length: > 0 } directory)
        {
            return Path.Combine(directory, NodeCatalogue.DefaultFileName);
        }

        return NodeCatalogue.DefaultFileName;
    }

    /// <summary>Makes sure this machine has a catalogue, creating one if not.</summary>
    /// <param name="said">Appended to with what was done.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The catalogue file to write into.</returns>
    /// <remarks>
    /// <b>Creating one no longer records anything on a project</b> (018). It used to run
    /// <c>project set --node-config</c>, which rewrote the manifest — and the serve watcher compares the
    /// manifest to decide whether a deployment is the same one, so adding the first agent to a project
    /// restarted its broker and dropped every agent attached to it. There is nothing to record: the node
    /// resolves this file the same way this does, from the machine rather than from a row.
    /// </remarks>
    internal async Task<string> EnsureAsync(
        List<string> said,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(said);

        var path = Proposed();

        if (!File.Exists(path))
        {
            await Enrolment.SaveCatalogueAsync(
                path,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["agents"] = new System.Text.Json.Nodes.JsonObject(),
                },
                cancellationToken).ConfigureAwait(false);

            said.Add($"Created {path}, which is what this machine starts its agents from.");
        }

        return path;
    }
}
