using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Cli.Top;

/// <summary>
/// Making a repository on this machine an agent of the selected project.
/// </summary>
/// <remarks>
/// <para>
/// Two files, both of which a person can read and revert: the repository's launch configuration,
/// naming the project and the role, and a line in the project's catalogue so a node may start it
/// when a question arrives. Both are written by <see cref="Enrolment"/>, which is the same code
/// <c>parley enroll apply</c> runs — FR-008 asks for one writer, and two would agree today and
/// differ in six months.
/// </para>
/// <para>
/// <b>What it does not do is register the agent</b>, and it cannot. SPEC.md §12.3 makes
/// registration the adapter's own act on attach, and a surface that inserted a registry row on a
/// repository's behalf would be a caller-supplied identity by another name. So the promise is the
/// honest one: the agent appears as <c>catalogued, not yet registered</c>, and reads as startable
/// the first time its own server attaches. `plan.md` "Q1 settled" carries the argument.
/// </para>
/// <para>
/// <b>Nothing is written until the person has seen what would be.</b> The paths are shown and
/// confirmed, because this writes into a repository that is not the one they are looking at, and
/// the two files are the whole of what makes an agent an agent.
/// </para>
/// </remarks>
internal static class Adding
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Add an agent to this project";

    /// <summary>What the confirmation is called.</summary>
    internal const string ConfirmTitle = "Write these two files?";

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
    /// Empty is legitimate and is not a gap: SPEC.md §17 question 10 allows an agent with no line,
    /// listed with none, and askers fall back to its name. What is refused is a line too long for
    /// the schema, which would otherwise be refused at registration — a long way from here, and
    /// looking like an agent that simply never appeared.
    /// </remarks>
    internal static string? OwnsProblem(string owns) =>
        (owns ?? string.Empty).Trim().Length <= EnrollmentPlan.OwnsLimit
            ? null
            : $"At most {EnrollmentPlan.OwnsLimit} characters.";

    /// <summary>What would be written, in the order it would be written.</summary>
    /// <param name="workspace">The repository.</param>
    /// <param name="catalogueFile">The project's catalogue.</param>
    /// <param name="project">The project it would join.</param>
    /// <returns>The two paths, and what each would gain.</returns>
    internal static string Describe(string workspace, string catalogueFile, string project) =>
        $"{Enrolment.LaunchPath(workspace)}{Environment.NewLine}"
        + $"    a parley server naming this repository, and the project '{project}'"
        + $"{Environment.NewLine}{catalogueFile}{Environment.NewLine}"
        + "    a line saying this machine may start it when a question arrives for it";

    /// <summary>
    /// Opens the dialog that adds an agent, and writes the two files when it is confirmed.
    /// </summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="project">The project the repository would join.</param>
    /// <param name="catalogue">
    /// The project's catalogue, and a way to create one when it names none.
    /// </param>
    /// <param name="queue">Runs the writes, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        ProjectSettings project,
        Catalogue catalogue,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var form = new FormDialog(
            Title,
            [
                new FormField("Repository directory on this machine:", string.Empty, WorkspaceProblem),
                new FormField("The role peers will address it by:", string.Empty, RoleProblem),
                new FormField(
                    "What it owns, and what it does not (optional, and never invented for you):",
                    string.Empty,
                    OwnsProblem),
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

            Confirm(app, project, catalogue, values, queue, done);
        };

        return Task.CompletedTask;
    }

    /// <summary>Shows the two paths and writes them only if the person says so.</summary>
    private static void Confirm(
        IApplication app,
        ProjectSettings project,
        Catalogue catalogue,
        IReadOnlyList<string> values,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var workspace = Path.GetFullPath(values[0].Trim());
        var role = values[1].Trim();

        var asking = Prompting.Choose(
            ConfirmTitle,
            Describe(workspace, Catalogue.Proposed(project), project.Name.Value)
            + Environment.NewLine
            + Environment.NewLine
            + (Catalogue.Exists(project)
                ? string.Empty
                : $"This project names no catalogue, so one is created at "
                  + $"{Catalogue.Proposed(project)} and recorded on it. Its broker restarts to "
                  + "host the node from it."),
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

            queue(token2 => WriteAsync(project, catalogue, workspace, role, values[2].Trim(), token2, done));
        };
    }

    /// <summary>Writes the two files, creating the catalogue first when there is none.</summary>
    private static async Task WriteAsync(
        ProjectSettings project,
        Catalogue catalogue,
        string workspace,
        string role,
        string owns,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var said = new List<string>();

        try
        {
            var file = await catalogue.EnsureAsync(project, said, cancellationToken).ConfigureAwait(false);

            var entry = new EnrollmentEntry(workspace, role, owns.Length == 0 ? null : owns, Unattended: true)
            {
                Enroll = true,
                OwnsApproved = owns.Length > 0,
            };

            var outcome = await Enrolment.AddAsync(
                entry,
                project.Name.Value,
                file,
                Path.Combine(workspace, "CLAUDE.md"),
                cancellationToken).ConfigureAwait(false);

            said.Add($"{Added}'{role}'.");
            said.AddRange(outcome.Notes);
            said.AddRange(outcome.Written.Select(path => $"wrote {path}"));
            said.Add(
                $"It will read as '{AgentsRegion.CataloguedOnly}' until its own server attaches "
                + "once and registers it. Nothing here can do that for it.");
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

        await done(string.Join(Environment.NewLine, said)).ConfigureAwait(false);
    }
}

/// <summary>
/// A project's catalogue: where it is, and how one is made when the project names none.
/// </summary>
/// <param name="ManifestPath">The manifest the project is named in, or null when there is none.</param>
/// <remarks>
/// <b>Creating one is recorded on the project through the command that owns the manifest</b>, which
/// is what <c>parley project set --node-config</c> does from a shell. The serve watcher compares
/// the catalogue path as part of what makes a deployment the same one, so recording it restarts
/// that project's broker and node within a couple of seconds — agents attached to it drop and
/// reattach, which the seat book already survives. That is worth a sentence on screen and not worth
/// a new mechanism: it happens once per project, the first time an agent is added.
/// </remarks>
internal sealed record Catalogue(string? ManifestPath)
{
    /// <summary>Whether the project already names a catalogue.</summary>
    /// <param name="project">The project.</param>
    /// <returns>Whether it does.</returns>
    internal static bool Exists(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.NodeConfig is { Length: > 0 };
    }

    /// <summary>Where a catalogue would go if the project needs one.</summary>
    /// <param name="project">The project.</param>
    /// <returns>Its catalogue, existing or proposed.</returns>
    /// <remarks>
    /// Beside the store, named after it, for the reason a store is named after its project: a
    /// machine that keeps its files somewhere has already decided where, and a second convention
    /// is a second place to look.
    /// </remarks>
    internal static string Proposed(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.NodeConfig is { Length: > 0 } named)
        {
            return named;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(project.Store));
        var name = Path.GetFileNameWithoutExtension(project.Store);

        return Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            (string.IsNullOrEmpty(name) ? project.Name.Value : name) + "-node.json");
    }

    /// <summary>Makes sure the project has a catalogue, creating and recording one if not.</summary>
    /// <param name="project">The project.</param>
    /// <param name="said">Appended to with what was done.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The catalogue file to write into.</returns>
    internal async Task<string> EnsureAsync(
        ProjectSettings project,
        List<string> said,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(said);

        if (Exists(project))
        {
            return project.NodeConfig!;
        }

        var path = Proposed(project);

        if (!File.Exists(path))
        {
            await Enrolment.SaveCatalogueAsync(
                path,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["agents"] = new System.Text.Json.Nodes.JsonObject(),
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (ManifestPath is { Length: > 0 } manifest)
        {
            var output = new StringWriter();
            var error = new StringWriter();

            await ProjectCommand.RunAsync(
                ["set", project.Name.Value],
                manifest,
                new ProjectFields(null, null, path, null),
                output,
                error,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            said.Add(
                $"Created {path} and recorded it on '{project.Name.Value}'. Its broker restarts "
                + "to host the node from it.");
        }
        else
        {
            said.Add(
                $"Created {path}, but this surface was given its projects and has no manifest to "
                + "record it on. Nothing will host a node from it until something does.");
        }

        return path;
    }
}
