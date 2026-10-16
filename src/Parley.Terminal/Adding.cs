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
/// <b>The catalogue is one file per machine, keyed by the agent's identity.</b> Every project shares
/// the one entry, so a repository added to a second project gains a launch entry and the instructions
/// section, and its catalogue line is already there.
/// </para>
/// <para>
/// <b>It does not write the registration, and it cannot.</b> SPEC.md §12.3 makes registration the
/// adapter's own act on attach, and a surface that inserted a registry row on a repository's behalf
/// would be a caller-supplied identity by another name. What it does instead is ask the machine to
/// start the agent once and let it register itself — which is what §12.3's own closing paragraph
/// routes through the catalogue. Nothing here may claim to be anybody, which is why the start is a
/// request rather than a write.
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
    /// Carries no count of files: a number in the title would have to be kept in step with the
    /// description beneath it.
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
    /// <b>It asks <see cref="OwnershipStatement"/> rather than measuring a length itself.</b> The
    /// character class that stops a line separator or a direction override is part of the same rule
    /// the database enforces, so one definition answers for both.
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
    /// Lists the three files in the order <c>Enrolment</c> writes them. The catalogue is shared by
    /// every project on the machine, so its line is always described.
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
    /// Enrolment creates or appends to it, so the confirmation must describe it: a surface that
    /// writes a file in somebody's repository has to say so first, and the repository is not the
    /// one the person is looking at.
    /// </remarks>
    internal const string Instructions = "CLAUDE.md";

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
    /// <see cref="Starting.Patience"/> for the agent to register, and this is what the screen says meanwhile.
    /// </param>
    /// <returns>When the first dialog is open.</returns>
    /// <remarks>
    /// <b>The folder is found before it is named.</b> A typed path can only be reported wrong
    /// afterwards, so a picker comes first. What is chosen fills the field in, and the field is still
    /// typed into: somebody who knows the path should not have to walk to it.
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
                    "Role (how peers address it):",
                    string.Empty,
                    RoleProblem,
                    Correct: EnrollmentPlan.CorrectName,
                    Corrected: EnrollmentPlan.NameWasCorrected),
                new FormField(
                    "What it owns, and what it does not (optional):",
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
                  + $"{catalogue.Proposed()}."),
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
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>What to say about it.</returns>
    /// <remarks>
    /// <para>
    /// <b>A failure here is reported and never thrown.</b> The files are already written and the
    /// agent is already catalogued, so a broker that did not answer leaves an agent somebody can
    /// still start later. Losing the whole enrolment because the last of three steps could not be
    /// taken would be the worse outcome.
    /// </para>
    /// <para>
    /// <b>The start goes to the project being added to.</b> Every project's node reads the same
    /// catalogue. The node starts a client in the repository's own directory, and the client brings
    /// up every parley server in its launch configuration, the entry just written among them.
    /// </para>
    /// </remarks>
    internal static async Task<string> StartAsync(
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? start,
        Func<ProjectSettings, AgentName, CancellationToken, Task<ProjectRead<bool>>>? registered,
        string role,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        if (start is null || !AgentName.TryCreate($"{role}@{MachineName.Current}", out var agent))
        {
            return NotStarted;
        }

        // **Asked, and then looked at.** A node reports that it launched something; whether the adapter
        // attached and registered is a question only the registry answers, so the screen claims the
        // agent can be addressed only once the registry holds it, under the project and role it was added as.
        var was = await Starting.OnceAsync(
            agent,
            (who, token) => start(project, who, token),
            registered is null
                ? (_, _) => Task.FromResult(ProjectRead<bool>.Unreachable(Unchecked))
                : (_, token) => registered(project, agent, token),
            Starting.Patience,
            TimeProvider.System,
            cancellationToken,
            catchUp: Starting.CatchUp).ConfigureAwait(false);

        var through = $"'{role}'";

        // **Three answers.** Nothing started; started and registered, the only case that may say the
        // agent can be addressed; started and not seen, which says exactly that and no more.
        return was switch
        {
            { Launched: false } => $"{Safe.Text(was.Says)} {NotStarted}",
            { Registered: true } =>
                $"Started {through} and it registered. It can be asked and told now.",
            _ => $"Started {through} — {Safe.Text(was.Says)}. {Later}",
        };
    }

    /// <summary>What is said when there is no broker to ask whether it registered.</summary>
    private const string Unchecked = "nothing here could ask the registry whether it did";

    /// <summary>What is said after a start whose registration was not seen.</summary>
    /// <remarks>
    /// <b>"I do not know", never "it did not happen".</b> An agent that registers a second after
    /// this stopped looking is working. What this says is what to do about either case, which is the
    /// same thing in both.
    /// </remarks>
    private const string Later =
        "Until it registers, it cannot be asked or told.";

    /// <summary>What is said when the start did not happen.</summary>
    /// <remarks>
    /// <para>
    /// <b>The next Request does not start it.</b> Every message is refused for an unroutable
    /// recipient, and §14 counts a name nothing has registered as unroutable — so until something has
    /// attached under this name, no message reaches it and none of them starts it.
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
        + "this screen. Until it registers, it cannot be asked or told.";

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

            // Unattended, so a node may start the agent when a question arrives for it.
            var entry = new EnrollmentEntry(
                workspace, role, owns.Length == 0 ? null : owns, Unattended: true)
            {
                Enroll = true,
            };

            var outcome = await Enrolment.AddAsync(
                entry,
                project.Name.Value,
                file,
                Path.Combine(workspace, Instructions),
                cancellationToken,
                catalogue.ManifestPath).ConfigureAwait(false);

            // Remembered only once the files are on disk, so the next search opens beside a
            // repository that really is one of this project's agents.
            remember?.Invoke(workspace);

            said.Add($"{Added}'{role}'.");
            said.AddRange(outcome.Notes);
            said.AddRange(outcome.Written.Select(path => $"wrote {path}"));

            // A surface must not write a registration on a repository's behalf (§12.3, §16). §12.3's
            // closing paragraph routes spawn-on-demand through the catalogue entry just written, so the
            // broker is asked to launch the role and the launched adapter registers itself under the
            // identity its launch fixes. The start is a request; the registration is the agent's own act.
            await Telling(
                $"Adding '{role}': its files are written. Starting it and waiting up to "
                + $"{(int)Starting.Patience.TotalSeconds} seconds for it to register...").ConfigureAwait(false);

            said.Add(await StartAsync(start, registered, role, project, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (InterruptedEnrolment interrupted)
        {
            // The one failure that arrives with files already changed, so it reports them and never
            // says `Nothing was written.`: a report that asserts the opposite of what happened settles
            // the question wrongly.
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
/// <b>One catalogue per machine, beside the manifest.</b> Nothing is recorded on a project, so
/// creating one never rewrites the manifest and never restarts a broker.
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
    /// <b>Creating one records nothing on a project.</b> The serve watcher compares the manifest to
    /// decide whether a deployment is the same one, so rewriting it would restart a broker and drop
    /// every agent attached. The node resolves this file the same way, from the machine rather than
    /// from a row.
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

            said.Add($"Created {path}.");
        }

        return path;
    }
}
