using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// Configures several repositories as Parley agents in one pass.
/// </summary>
/// <remarks>
/// <para>
/// <c>/parley:setup</c> is right for one repository and stops being right somewhere around the
/// fourth. It costs a session, a restart and somebody's attention each time — and because
/// <c>/parley:write-peers</c> reads the live roster, every agent that registers makes every block
/// already written stale, so the first repository's block is rewritten once per repository that
/// follows it.
/// </para>
/// <para>
/// Nine tenths of setup needs no tools at all: the launch configuration, the node entry and the
/// placeholder frame are file writes against known values. Only the roster needs a bound server.
/// So this does that nine tenths for a list of repositories, and stops — the roster pass is
/// <c>/parley:write-peers</c>, run once per repository <i>after</i> the last one has registered.
/// </para>
/// <para>
/// <b>It is here rather than in a script beside the plugin because .NET is the only runtime a
/// deployment already has.</b> Nothing is a Parley agent without <c>parley-mcp</c> on PATH, and
/// that arrives through <c>dotnet tool install</c>. A Python helper would add a second runtime to
/// a machine that has one — and the plugin's existing Python hook is written to fail silently
/// when its interpreter is missing, which is survivable for a mail check and not for a program
/// that writes configuration into eleven repositories.
/// </para>
/// </remarks>
public static class EnrollCommand
{
    private const int Usage = 64;          // EX_USAGE
    private const int Config = 78;         // EX_CONFIG
    private const int IoError = 74;        // EX_IOERR

    /// <summary>
    /// Runs one enroll subcommand.
    /// </summary>
    /// <param name="arguments">The subcommand and its operands.</param>
    /// <param name="selectAll">
    /// True or false to mark every repository found, skipping the picker; null to show it.
    /// </param>
    /// <param name="nodeConfig">
    /// The node catalogue the entry point resolved, and which input decided it.
    /// </param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="project">
    /// The project being enrolled into, or null when the deployment names none. **Taken from the
    /// entry point's own <c>--project</c> resolution rather than from a field in the enrolment
    /// manifest**, which the plan had asked for: <c>ProjectResolution.TrySelect</c> already refuses
    /// to guess when several are configured and none is named, so a second place to say which
    /// project would be a second answer to a question that is already answered — and the two would
    /// disagree first on the machine with more than one deployment, which is the only machine where
    /// it matters.
    /// </param>
    /// <param name="manifestPath">
    /// The machine's root, whose lock is held for the length of the run so that two runs cannot
    /// interleave their reads and writes of the catalogue.
    /// </param>
    /// <param name="describe">
    /// Report what the verb would configure and change nothing.
    /// </param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        bool? selectAll,
        Resolved nodeConfig,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default,
        ProjectSettings? project = null,
        string? manifestPath = null,
        bool describe = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(nodeConfig);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        return arguments switch
        {
            // One verb, one operand: the folder holding the checkouts.
            [var directory] => await EnrollAsync(
                    directory, selectAll, describe, nodeConfig, output, error,
                    project, manifestPath, cancellationToken)
                .ConfigureAwait(false),

            _ => await UsageAsync(error).ConfigureAwait(false),
        };
    }

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync(
            "parley enroll <directory>            find the repositories under it, tick the ones to\n" +
            "                                     enroll, and configure, catalogue and start them\n" +
            "              [--all|--none]         skip the picker; for scripts and CI\n" +
            "              [--describe]           say what would change, and change nothing\n" +
            "\n" +
            "Enrolling a repository is the consent to start it: a catalogued checkout can be started\n" +
            "by a peer's request with nobody watching. The count is named before anything is written."
            ).ConfigureAwait(false);

        return Usage;
    }

    /// <summary>
    /// Finds the repositories under a directory, offers them, and configures the ones chosen.
    /// </summary>
    /// <param name="root">The directory holding the checkouts.</param>
    /// <param name="selectAll">True for all, false for none, null to offer a picker.</param>
    /// <param name="describe">Report what would be configured and change nothing.</param>
    /// <param name="nodeConfig">The catalogue this machine reads, as the entry point resolved it.</param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="project">The project being enrolled into, or null.</param>
    /// <param name="projectManifest">The machine's root, so the gate can be held across the run.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// <para>
    /// <b>One verb, and no file for anybody to author.</b> It finds, configures and starts.
    /// </para>
    /// <para>
    /// <b>What a repository owns is asked of the agent, not typed in here.</b> What a launch
    /// configuration already says is carried forward by <see cref="Discover"/> reading it.
    /// </para>
    /// </remarks>
    private static async Task<int> EnrollAsync(
        string root,
        bool? selectAll,
        bool describe,
        Resolved nodeConfig,
        TextWriter output,
        TextWriter error,
        ProjectSettings? project,
        string? projectManifest,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            await error.WriteLineAsync($"{root} is not a directory.").ConfigureAwait(false);

            return Usage;
        }

        var entries = Discover(root);

        if (entries.Count == 0)
        {
            await error.WriteLineAsync($"No git repositories directly under {root}.").ConfigureAwait(false);

            return Usage;
        }

        var chosen = await ChooseAsync(entries, selectAll, output, error).ConfigureAwait(false);

        if (chosen is null)
        {
            await output.WriteLineAsync("Cancelled. Nothing was configured.").ConfigureAwait(false);

            return 0;
        }

        if (chosen.All(e => !e.Enroll))
        {
            await error.WriteLineAsync(
                $"No repositories chosen under {root}. Nothing was configured.").ConfigureAwait(false);

            return Config;
        }

        return await ConfigureAsync(
            chosen,
            source: root,
            write: !describe,
            nodeConfig,
            output,
            error,
            project,
            projectManifest,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The git repositories directly under a directory, for a surface to offer.</summary>
    /// <param name="root">The directory holding the checkouts.</param>
    /// <returns>One row per repository, in a stable order, none of them ticked.</returns>
    /// <remarks>
    /// <b>Public so that the terminal can draw the picker and still enrol through this command.</b>
    /// The alternative is the terminal finding repositories itself, which would be a second
    /// implementation of the rule for what counts as one — and that rule decides which names end up
    /// on a roster no agent will ever answer to. What a surface supplies is the choosing; what is
    /// written stays this command's.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public static IReadOnlyList<EnrollmentEntry> Find(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return Directory.Exists(root) ? Discover(root) : [];
    }

    /// <summary>Configures, catalogues and starts repositories a surface has already chosen.</summary>
    /// <param name="chosen">The rows, with <c>Enroll</c> set on the ones to configure.</param>
    /// <param name="describe">Report what would be configured and change nothing.</param>
    /// <param name="nodeConfig">The catalogue this machine reads, as the caller resolved it.</param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="project">The project being enrolled into, or null.</param>
    /// <param name="manifestPath">The machine's root, so the gate is held across the run.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="outcome">
    /// Filled in with who was enrolled and who registered, for a surface that says so in its own words
    /// rather than reprinting <paramref name="output"/>. Null when the caller wants only the text.
    /// </param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// The other half of <see cref="Find"/>: a surface that drew its own picker hands the rows back
    /// and this writes them. What a repository is responsible for is asked of the agent itself.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> EnrolAsync(
        IReadOnlyList<EnrollmentEntry> chosen,
        bool describe,
        Resolved nodeConfig,
        TextWriter output,
        TextWriter error,
        ProjectSettings? project = null,
        string? manifestPath = null,
        CancellationToken cancellationToken = default,
        EnrolmentOutcome? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(nodeConfig);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // The consent is granted in `ConfigureAsync`, where this and the command line's own picker
        // both arrive. See the note there.
        return await ConfigureAsync(
            chosen,
            source: "the repositories chosen",
            write: !describe,
            nodeConfig,
            output,
            error,
            project,
            manifestPath,
            cancellationToken,
            outcome).ConfigureAwait(false);
    }

    /// <summary>The git repositories directly under a directory, as enrolment rows.</summary>
    /// <param name="root">The directory holding the checkouts.</param>
    /// <returns>One row per repository, in a stable order.</returns>
    /// <remarks>
    /// The one implementation of the rule for what counts as a repository, shared by the command
    /// line's picker and the surfaces that draw their own — and that rule is load-bearing, since
    /// anything it admits wrongly becomes a name on the roster no agent will ever answer to.
    /// </remarks>
    private static List<EnrollmentEntry> Discover(string root)
    {
        var entries = new List<EnrollmentEntry>();

        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            // A directory is a repository when git says so. Anything else under a folder of
            // checkouts is a build output, a scratch clone or somebody's notes, and enrolling it
            // would put a name on the roster that no agent will ever answer to.
            if (!Directory.Exists(Path.Combine(directory, ".git"))
                && !File.Exists(Path.Combine(directory, ".git")))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var workspace = directory.Replace('\\', '/');
            var (agent, owns) = ReadExisting(directory);

            // Never unattended from a scan: it reads a directory of clones, content the repositories
            // themselves wrote, so nothing it produces may carry consent. Enrolling is the consent,
            // and enrolling happens at a door, which is where the flag is set.
            entries.Add(new EnrollmentEntry(
                workspace,
                agent ?? SuggestName(name),
                owns ?? string.Empty,
                Unattended: false)
            {
                Enroll = false,

                // Computed once, here, so both pickers show the same thing. It was worked out
                // privately for this command's own picker and the terminal composed its own from
                // `Owns` alone — so a worktree was called out on one surface and offered silently on
                // the other, which is the one people enrol from.
                Note = Noted(workspace, owns ?? string.Empty),
            });
        }

        return entries;
    }

    /// <summary>
    /// Settles which repositories are to be enrolled: by picker, by flag, or not at all.
    /// </summary>
    /// <returns>The entries with <see cref="EnrollmentEntry.Enroll"/> set, or null if cancelled.</returns>
    /// <remarks>
    /// The flags exist because the picker cannot run everywhere. A redirected stdin is a script
    /// or a CI job, and prompting there blocks forever — so the fallback is explicit rather than
    /// a default that quietly enrolls a directory nobody looked at.
    /// </remarks>
    private static async Task<List<EnrollmentEntry>?> ChooseAsync(
        List<EnrollmentEntry> entries,
        bool? selectAll,
        TextWriter output,
        TextWriter error)
    {
        if (selectAll is { } all)
        {
            return [.. entries.Select(e => e with { Enroll = all })];
        }

        if (!ConsolePicker.Available)
        {
            // Only --all is named: nothing is carried from a previous run, so every row here is unticked.
            await error.WriteLineAsync(
                "No terminal to choose from, so no repository was chosen. Re-run with --all to enroll\n" +
                "every repository found.")
                .ConfigureAwait(false);

            return entries;
        }

        var selection = ConsolePicker.Show(RepositorySelection.Over(
            [.. entries.Select(e => new SelectableRepository(
                Path.GetFileName(e.Workspace.TrimEnd('/')),
                e.Workspace,
                e.Note,
                e.Enroll))]));

        if (selection.Status == SelectionStatus.Cancelled)
        {
            return null;
        }

        await output.WriteLineAsync(string.Empty).ConfigureAwait(false);

        return [.. entries.Select((e, i) => e with { Enroll = selection.Repositories[i].Selected })];
    }

    /// <summary>
    /// Says why a repository is worth a second look in the picker.
    /// </summary>
    /// <remarks>
    /// Only facts read off disk. A repository that is already an agent is the one somebody most
    /// needs to recognise in a list of twenty, because leaving it unticked is how a working
    /// agent silently stops being offered.
    /// </remarks>
    /// <remarks>
    /// Takes the workspace and the owns line rather than an entry, because it is called while the
    /// entry is being built — which is what puts the note on the row, where the terminal can read it
    /// too. <see cref="EnrollmentEntry.Note"/> carries why that matters.
    /// </remarks>
    private static string Noted(string workspace, string owns) =>
        IsWorktree(workspace) ? "worktree of another checkout"
        : File.Exists(Path.Combine(workspace, ".mcp.json"))
            && !string.IsNullOrEmpty(owns) ? "already an agent"
        : File.Exists(Path.Combine(workspace, ".mcp.json")) ? "has an .mcp.json"
        : string.Empty;

    /// <summary>
    /// Says whether a directory is a git worktree rather than a repository of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A worktree's <c>.git</c> is a file holding <c>gitdir:</c>; a repository's is a directory.
    /// Cheap, and it needs no git on PATH.
    /// </para>
    /// <para>
    /// Worth telling apart because a worktree is the <i>same repository</i> at another path.
    /// Enrolling one gives a second agent to one codebase, and <c>.mcp.json</c> is a tracked
    /// file — so the two agents' launch configurations are the same path in one repository on
    /// two branches. A folder of worktrees looks exactly like a folder of checkouts to a scan.
    /// </para>
    /// </remarks>
    private static bool IsWorktree(string workspace)
    {
        var marker = Path.Combine(workspace, ".git");

        return File.Exists(marker) && !Directory.Exists(marker);
    }

    /// <summary>Every enrolment a repository already has, or null when the file cannot be read.</summary>
    /// <param name="workspace">The repository.</param>
    /// <returns>The entries, empty when it is enrolled nowhere, null when nothing could be read.</returns>
    /// <remarks>
    /// <b>Three answers, not two.</b> Enrolled nowhere and could-not-be-read look identical to a
    /// caller that returns an empty list for both, and they lead somewhere completely different: the
    /// first is a repository to enrol and the second is a file to look at. The same distinction the
    /// install scripts draw when a broker cannot be asked.
    /// </remarks>
    private static IReadOnlyList<LaunchEntry>? Existing(string workspace)
    {
        try
        {
            var path = Path.Combine(workspace, ".mcp.json");

            // **A link is not read, on the same terms the write refuses one.** What comes back from
            // here becomes a suggested agent name and owns line — an identity — so taking it from a
            // file the repository merely points at is the write-anywhere defect with the arrow
            // reversed. Unreadable rather than empty, because "enrolled nowhere" and "there is a file
            // to look at" are the three-answers distinction this method exists for.
            if (Enrolment.Linked(path) is not null)
            {
                return null;
            }

            return File.Exists(path)
                ? LaunchEntries.Read(JsonNode.Parse(File.ReadAllText(path))?["mcpServers"] as JsonObject)
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                       or UnauthorizedAccessException or FormatException
                                       or InvalidDataException)
        {
            // InvalidDataException is a file naming one server twice. It belongs with the rest: a
            // scan gathers evidence and must never be the thing that ends the run, and "could not be
            // read" is the honest answer for a file this cannot make sense of.
            return null;
        }
    }

    private static (string? Agent, string? Owns) ReadExisting(string workspace)
    {
        try
        {
            // The first entry, and only as a suggestion to fill a blank row with. Which project it
            // belongs to does not matter here — the person is being offered a name they already used
            // for this repository, and a repository in three projects has three of those; the scan
            // reports all three separately, as evidence rather than as a default.
            var entry = Existing(workspace)?.FirstOrDefault();

            return (entry?.Agent, entry?.Owns);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                       or UnauthorizedAccessException or FormatException)
        {
            // A repository whose .mcp.json cannot be read still belongs in the manifest — as a
            // blank row somebody fills in. Validation refuses it later with a message naming the
            // file, which is a better place to fail than a scan that skipped it silently.
            return (null, null);
        }
    }

    /// <summary>
    /// Turns a directory name into a candidate agent name.
    /// </summary>
    /// <param name="directoryName">The directory the repository is checked out in.</param>
    /// <returns>A well-formed name, which is a starting point rather than an answer.</returns>
    /// <remarks>
    /// SPEC-wise this is the wrong source — a name should say what the repository owns, not where
    /// it sits — and it is offered anyway because a blank field is answered with a directory name
    /// far more often than with a considered one. The manifest says so in as many words, and
    /// whoever fills it in is looking at the list.
    /// </remarks>
    public static string SuggestName(string directoryName)
    {
        ArgumentNullException.ThrowIfNull(directoryName);

        var slug = new System.Text.StringBuilder(directoryName.Length);

        foreach (var c in directoryName)
        {
            if (char.IsAsciiLetterUpper(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var name = slug.ToString().Trim('-');

        while (name.Length > 0 && !char.IsAsciiLetterLower(name[0]))
        {
            name = name[1..];
        }

        if (name.Length > EnrollmentPlan.NameLimit)
        {
            name = name[..EnrollmentPlan.NameLimit].TrimEnd('-');
        }

        return name.Length == 0 ? "agent" : name;
    }

    /// <summary>Starts each newly enrolled agent, so that it registers itself.</summary>
    /// <param name="project">The project just enrolled into, or null when none was resolved.</param>
    /// <param name="enrolled">The roles written, in the order they were written.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="recorded">Where to record who registered, or null.</param>
    /// <param name="cancellationToken">Stops the starts.</param>
    /// <returns>How many were seen to register.</returns>
    /// <remarks>
    /// <para>
    /// <b>Reported per repository and never thrown.</b> The two files are already written for every
    /// one of them, so an agent that could not be started is enrolled and startable later — losing
    /// the whole run because the last step did not take would be the worse outcome, which is the
    /// same reasoning the terminal's enrolment records.
    /// </para>
    /// <para>
    /// Sequentially, because each start is a process launching on this machine and twenty at once
    /// is a spike nobody asked for. It is a launch and not a turn, so it costs no model tokens —
    /// which is what makes doing it at all affordable.
    /// </para>
    /// </remarks>
    private static async Task<int> SettleAsync(
        ProjectSettings? project,
        IReadOnlyList<string> enrolled,
        TextWriter output,
        EnrolmentOutcome? recorded,
        CancellationToken cancellationToken)
    {
        foreach (var role in enrolled)
        {
            recorded?.Enrolled(role);
        }

        if (enrolled.Count == 0)
        {
            return 0;
        }

        if (project is null || string.IsNullOrWhiteSpace(project.Endpoint))
        {
            await output.WriteLineAsync(
                "\nNo broker was named, so nothing was started. These repositories are enrolled and "
                + "cannot be addressed until each has run once.").ConfigureAwait(false);

            foreach (var role in enrolled)
            {
                recorded?.NotRegistered(role, "nothing was started, because no broker was named");
            }

            return 0;
        }

        await output.WriteLineAsync("\nStarting each one so it registers itself:").ConfigureAwait(false);

        var registered = 0;

        foreach (var role in enrolled)
        {
            recorded?.Say(role, EnrolmentOutcome.Starting);

            if (!AgentName.TryCreate($"{role}@{MachineName.Current}", out var agent))
            {
                const string NotAName = "written, but is not a name an agent can have.";

                await output.WriteLineAsync($"  {role}: {NotAName}").ConfigureAwait(false);
                recorded?.NotRegistered(role, NotAName);
                recorded?.Say(role, "not a name an agent can have");

                continue;
            }

            var outcome = await Registering.StartAsync(
                project.Endpoint,
                agent,
                TimeProvider.System,
                cancellationToken,
                recorded is null ? null : detail => recorded.Say(role, detail)).ConfigureAwait(false);

            if (outcome.Registered)
            {
                registered++;
                recorded?.Registered(role);
                recorded?.Say(role, EnrolmentOutcome.Running);
            }
            else
            {
                recorded?.NotRegistered(role, outcome.Says);
                recorded?.Say(role, EnrolmentOutcome.NotYet);
            }

            await output.WriteLineAsync($"  {role}: {outcome.Says}").ConfigureAwait(false);
        }

        return registered;
    }

    /// <summary>
    /// Configures, catalogues and starts a set of repositories that have already been chosen.
    /// </summary>
    /// <param name="rows">
    /// The repositories, ticked and unticked.
    /// </param>
    /// <param name="source">What to name in a refusal — a directory, or a description of the choice.</param>
    /// <param name="write">Whether to change anything, or only describe what would change.</param>
    /// <param name="nodeConfig">The catalogue this machine reads, as the entry point resolved it.</param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="project">The project being enrolled into, or null.</param>
    /// <param name="projectManifest">The machine's root, so the gate can be held across the run.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="outcome">Filled in with who was enrolled and who registered, or null.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// <para>
    /// <b>Enrolling needs no file:</b> the rows are the input, and where they came from is one string
    /// used in refusals.
    /// </para>
    /// <para>
    /// <b>The gate is taken here</b>, so both callers have it without each remembering to.
    /// </para>
    /// </remarks>
    private static async Task<int> ConfigureAsync(
        IReadOnlyList<EnrollmentEntry> rows,
        string source,
        bool write,
        Resolved nodeConfig,
        TextWriter output,
        TextWriter error,
        ProjectSettings? project,
        string? projectManifest,
        CancellationToken cancellationToken,
        EnrolmentOutcome? outcome = null)
    {
        // Held for the whole run: validation and writing are far apart by design (everything is refused
        // before anything is written), so a gate spanning only one of them would leave a window.
        using var gate = await Catalogues.GateAsync(projectManifest, cancellationToken).ConfigureAwait(false);

        // The catalogue is the file this command records consent to run unattended in, and naming
        // it is what stops a run writing ten repositories where nothing reads them. The provenance
        // travels with it, because "which of the inputs won" is the question the line answers.
        await output.WriteLineAsync($"node catalogue: {nodeConfig}").ConfigureAwait(false);

        if (rows.Count == 0)
        {
            await error.WriteLineAsync($"{source} names no agents.").ConfigureAwait(false);
            return Config;
        }

        // Enrolling is the consent, and this is where it is granted. Both callers arrive here — the
        // command line after its picker, and the terminal after its door — and one place is the point:
        // two grants agree until one is edited, and what they agree about is whether a peer may start
        // somebody's checkout with nobody watching. Not where the repositories are found: a scan reads
        // content the repositories themselves wrote, so only a person choosing can grant it, and only
        // what they chose is touched here.
        //
        // `describe` reaches this too, deliberately: it must predict what a real run would do, and
        // it writes nothing on the way past.
        rows = [.. rows.Select(row => row.Enroll ? row with { Unattended = true } : row)];

        var manifestPath = source;

        // Only the chosen ones, and only they are validated. A row somebody deliberately left
        // out has no reason to be well formed, and refusing the run because an unselected
        // repository has a blank name would make declining one repository cost an edit.
        // A null row is a hand-edited manifest with an object's body deleted and its comma left.
        // Kept rather than filtered so validation names it, since silently dropping a row is how
        // a repository somebody meant to enroll goes missing with nothing said.
        var entries = rows.Where(e => e is null || e.Enroll).ToArray();
        var skipped = rows.Count - entries.Length;

        if (entries.Length == 0)
        {
            // The path is printed when there is one, because `--config` can name a root and saying
            // which one was read is the difference between a refusal and a puzzle.
            await error.WriteLineAsync(
                manifestPath is { Length: > 0 } under
                    ? $"No repositories were chosen under {under}. Nothing was configured."
                    : "No repositories were chosen. Nothing was configured.").ConfigureAwait(false);

            return Config;
        }

        if (skipped > 0)
        {
            await output.WriteLineAsync($"{skipped} skipped.").ConfigureAwait(false);
        }

        // Before anything is written, say that each repository becomes startable by a peer with nobody
        // watching. The sentence is `EnrolmentConsent`'s, so every surface reads one wording.
        // `--describe` prints it too and then changes nothing: it must show what a real run would put
        // in front of somebody.
        await output.WriteLineAsync(
            EnrolmentConsent.Says(entries.Length, project?.Name)).ConfigureAwait(false);

        var problems = new List<string>(
            EnrollmentPlan.Validate(entries, Directory.Exists, row: null));

        // Asked here, among every other reason to refuse the whole run, because enrolment must leave
        // each agent registered, registering is the agent attaching, and attaching needs a broker.
        // Writing the files and discovering it afterwards would leave a half-enrolled set.
        //
        // Only when a project was resolved: a machine naming none has no endpoint to ask.
        if (write
            && project is { } target
            && !string.IsNullOrWhiteSpace(target.Endpoint)
            && await Registering.UnreachableAsync(
                target.Endpoint, target.Name.Value, cancellationToken).ConfigureAwait(false)
                is { } unreachableBroker)
        {
            problems.Add(unreachableBroker);
        }

        if (problems.Count > 0)
        {
            await error.WriteLineAsync("This manifest is not usable yet:\n").ConfigureAwait(false);

            foreach (var problem in problems)
            {
                await error.WriteLineAsync($"  - {problem}").ConfigureAwait(false);
            }

            // Said explicitly because the alternative is tempting and worse. Enrolling the eight
            // repositories that validate leaves the ninth looking exactly like one nobody meant
            // to include, and nothing later distinguishes them.
            await error.WriteLineAsync(
                "\nNothing was written. A half-enrolled set is worse than none: the repository that " +
                "\nmissed out is indistinguishable from one you never meant to enroll.").ConfigureAwait(false);

            return Config;
        }

        var nodePath = ResolveNodeConfig(nodeConfig.Value);

        // **Held from the read to the write, which is the whole run.** This reads the catalogue here
        // and writes it after the last repository, and without a hold two runs sharing one catalogue
        // each read it, each added their own line, and the later write dropped the earlier one's --
        // an agent told its enrolment had succeeded and missing from the file that makes it
        // startable. The single-repository path was given this and the bulk one was not.
        using var nodeHeld = await Enrolment.HoldAsync(nodePath, cancellationToken).ConfigureAwait(false);

        var node = nodeHeld is null
            ? ReadNode(nodePath)
            : ReadNodeText(await Enrolment.ReadHeldAsync(nodeHeld, cancellationToken).ConfigureAwait(false));

        if (node is null)
        {
            // Refused before any repository is touched, and refused even when nothing here would
            // have written to it — because the person cannot tell from the output which run was
            // the one that would have, and the file is not recoverable once overwritten.
            await error.WriteLineAsync(
                $"{nodePath} exists but is not readable JSON. Fix or move it first: enrolling would\n" +
                "replace it, and it is the only record of which repositories may run unattended.")
                .ConfigureAwait(false);

            return Config;
        }

        // The same destruction as above in a different shape, and it survived the first fix:
        // `agents` can be well-formed JSON of the wrong type — an array, a string — in which
        // case casting it away and writing an object over it loses whatever it held just as
        // completely as an unparseable file did. Absent is the only shape that means "empty".
        //
        // The check now lives in Enrolment, which is the one writer of this file, so the surface
        // cannot make the same mistake by writing its own copy.
        JsonObject listed;

        try
        {
            listed = Enrolment.AgentsOf(node, nodePath);
        }
        catch (InvalidDataException)
        {
            await error.WriteLineAsync(
                $"{nodePath} has an \"agents\" entry that is not an object. Fix or move it first:\n" +
                "enrolling would replace it.").ConfigureAwait(false);

            return Config;
        }

        // The copied-repository refusal, asked of every row before any of them is written. It needs the
        // catalogue, which is read here and not in the validation phase above. Asked in this loop rather
        // than in the writing one because a refusal raised while writing leaves the repositories before
        // it on disk. `Enrolment.AddAsync` applies the same rule for the single-repository path.
        var copies = new List<string>();

        foreach (var entry in entries)
        {
            var declared = Enrolment.CopyRefusal(listed, entry.Workspace);

            if (declared is not null)
            {
                copies.Add($"{entry.Workspace}: {declared}");
            }
        }

        if (copies.Count > 0)
        {
            await error.WriteLineAsync("This manifest is not usable yet:\n").ConfigureAwait(false);

            foreach (var copy in copies)
            {
                await error.WriteLineAsync($"  - {copy}").ConfigureAwait(false);
            }

            await error.WriteLineAsync(
                "\nNothing was written. A half-enrolled set is worse than none: the repository that " +
                "\nmissed out is indistinguishable from one you never meant to enroll.").ConfigureAwait(false);

            return Config;
        }

        var nodeChanged = false;
        var written = 0;

        // Collected so each can be started once the catalogue has been written. **Not started
        // inside the loop**, because a start asks the broker to launch a role its catalogue does not
        // yet name -- the same ordering the terminal's enrolment already keeps, and the same one its
        // tests state: the files first, the start last.
        var enrolled = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instructionsPath = InstructionsFile(entry.Workspace);
            var launchPath = Path.Combine(entry.Workspace, ".mcp.json");

            RepositoryPlan plan;
            IReadOnlyList<string> wrote = [];
            IReadOnlyList<string> said = [];

            // The instructions are read by whoever holds them: reading at the top of the loop and writing
            // at the bottom would lose an update to the file that is entirely somebody's own prose. The
            // decision receives what was read under the hold that writes it.
            RepositoryPlan Decide(string? launch, string? instructions) => EnrollmentPlan.Describe(
                entry,
                launch,
                instructions,
                listed.ContainsKey(entry.Agent),
                project?.Name.Value,
                nodeWorkspace: listed[entry.Agent]?["workspace"]?.GetValue<string>());

            try
            {
                // Deciding and writing happen under one hold when this is going to write. Reporting
                // happens afterwards, so the notes describe what was done rather than what was going
                // to be.
                if (write)
                {
                    (plan, wrote, said) = await Enrolment
                        .WriteAsync(Decide, entry.Workspace, instructionsPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // `plan` writes nothing, so both files are read plainly here. Holding them would
                    // be a lock taken to describe a file and released before anything acted on it.
                    plan = Decide(
                        File.Exists(launchPath)
                            ? await File.ReadAllTextAsync(launchPath, cancellationToken).ConfigureAwait(false)
                            : null,
                        File.Exists(instructionsPath)
                            ? await File.ReadAllTextAsync(instructionsPath, cancellationToken).ConfigureAwait(false)
                            : null);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Refused rather than merged into. Everything this cannot merge is somebody's
                // own file in a shape it does not understand, and the failure of the alternative
                // is silent: the entries it could not read are simply not in what gets written.
                await error.WriteLineAsync(
                    $"{entry.Agent}: {launchPath} cannot be merged into — {ex.Message}.\n" +
                    "Fix or move it first; enrolling would replace what is there.").ConfigureAwait(false);

                // Say where it stopped. Validation is hoisted ahead of every write so that a set is never
                // half applied, but this refusal cannot be hoisted: knowing it means reading each
                // repository's own launch configuration. The rows already done have printed themselves;
                // the ones never reached are named, because silence makes them indistinguishable.
                var unreached = entries
                    .SkipWhile(row => !ReferenceEquals(row, entry))
                    .Skip(1)
                    .Select(row => row.Agent)
                    .ToArray();

                if (unreached.Length > 0)
                {
                    await error.WriteLineAsync(
                        $"\nNothing was written for {unreached.Length} repository(s) after it: "
                        + $"{string.Join(", ", unreached.Select(Quoting.Flat))}. Re-run once the "
                        + "fault is fixed; enrolling a repository twice changes nothing the second "
                        + "time.").ConfigureAwait(false);
                }

                return Config;
            }

            await output.WriteLineAsync($"\n{entry.Agent}  ({entry.Workspace})").ConfigureAwait(false);

            if (!Directory.Exists(Path.Combine(entry.Workspace, ".git"))
                && !File.Exists(Path.Combine(entry.Workspace, ".git")))
            {
                // Said rather than refused, on the same terms as the worktree note below: `scan`
                // only ever offers git repositories, so a directory without one arrived through a
                // hand-edited manifest, and the person who typed the path may have a reading of it
                // nobody here thought of. What must not happen is that it passes unremarked -- the
                // file lands either way, and nothing afterwards makes that visible.
                await output.WriteLineAsync(
                    "  ! this is not a git repository. It will still be written into.")
                    .ConfigureAwait(false);
            }

            if (IsWorktree(entry.Workspace))
            {
                // Said rather than refused: a worktree is the same repository at another path, so
                // this is almost certainly a mistake — but it is the person's directory and there
                // may be a reading of it I have not thought of. What must not happen is that it
                // passes unremarked, because nothing afterwards makes it visible.
                await output.WriteLineAsync(
                    "  ! this is a git worktree, not a repository of its own. Its .mcp.json is a\n" +
                    "    tracked file shared with the checkout it belongs to.").ConfigureAwait(false);
            }

            foreach (var note in plan.Notes.Concat(said))
            {
                await output.WriteLineAsync($"  {note}").ConfigureAwait(false);
            }

            if (!write)
            {
                continue;
            }

            // Through Enrolment, the one writer of these two files. This loop wrote them itself
            // until the terminal needed to write them too, and two copies of a file write are how a
            // surface and a command come to disagree about what makes a repository an agent.
            written += wrote.Count;
            enrolled.Add(entry.Agent);

            if (plan.AddsNodeEntry)
            {
                // Keyed by the identity the plan decided, which is the one the repository now carries.
                Enrolment.List(listed, plan.Identity, entry.Workspace);
                nodeChanged = true;
            }
        }

        if (!write)
        {
            await output.WriteLineAsync(
                "\nNothing was written. Run the same command without --describe to make these changes.")
                .ConfigureAwait(false);

            return 0;
        }

        if (nodeChanged)
        {
            try
            {
                await Enrolment.OverwriteHeldAsync(
                    nodeHeld,
                    nodePath,
                    node.ToJsonString(SerialisationOptions.Indented) + "\n",
                    cancellationToken).ConfigureAwait(false);

                await output.WriteLineAsync($"\nUpdated {nodePath}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Could not write {nodePath}: {ex.Message}").ConfigureAwait(false);
                return IoError;
            }
        }

        // **Let go before waiting on the node.** The hold above stops two runs losing each other's line, and it is over
        // now the catalogue is written. Kept through the starts, it left the node reading "used by another process",
        // holding the roles it had, and every start timing out as "its node did not start it" -- the node learns a new
        // role by reading this very file.
        nodeHeld?.Dispose();

        // **Enrolment is not finished until each agent has registered**, and it cannot register
        // itself without running once: §12.3 makes registration the adapter's own act on attach, so
        // the only honest way to leave one registered is to start it and let it do so. Until then
        // §14 counts the name unroutable and every message type is refused, a Request as surely as
        // any other, so a repository enrolled and never opened is not a quiet agent but one nothing
        // can address.
        //
        // Last, after both files and the catalogue: a start asks the broker to launch a role, and
        // the node cannot launch what its catalogue does not yet name.
        var registered = await SettleAsync(
            project, enrolled, output, outcome, cancellationToken).ConfigureAwait(false);

        // Step 1 names the node as well as the broker, and only when this run recorded consent
        // for one. A run that wrote `unattended` into the catalogue and then said "start a
        // broker" has described half a deployment: the repositories it just recorded as
        // startable are startable by a process it never mentioned, so they sit there reading as
        // available while work sent to them waits, never collected. The catalogue is named
        // because a supervisor starts a process with no shell environment, and this is the one
        // moment somebody has both facts in front of them.
        //
        // A machine is deployed as projects, and the logon entry `parley autostart install` writes serves
        // every one, hosting each project's node from the catalogue its entry names -- or, on a machine with
        // one project, from PARLEY_NODE_CONFIG when that is set, as `parley up` would. The entry is named as
        // something to install only when `autostart status` does not already show it, because installing it
        // again restarts the broker and ends every agent it started.
        var named = project?.Name.Value;

        const string Serve =
            "     Served at every logon by `parley autostart install`; a machine whose entry already serves the\n" +
            "     manifest (`parley autostart status` says) picks this up within seconds and needs nothing.\n";

        // There is one catalogue per machine, so no step points a project at one.
        var start = named is null
            ? "  1. Name the project these repositories answer in, on the first screen of:\n" +
              "       parley\n" +
              Serve
            : $"  1. A broker{(nodeChanged ? " and this machine's node" : string.Empty)} for {named}.\n" + Serve;

        // Enrolment starts each agent, so the only step for the person is chasing whichever did not come up.
        var unsettled = enrolled.Count - registered;

        var chase = unsettled > 0
            ? $"  2. {unsettled} of {enrolled.Count} did not register above. Open those repositories,\n"
              + "     or re-run this — enrolling a repository twice changes nothing the second time.\n"
            : string.Empty;

        await output.WriteLineAsync(
            $"\nEnrolled {entries.Length} repositories ({written} files written, "
            + $"{registered} registered).\n\n" +
            "Next, in this order:\n" +
            start +
            chase +
            $"  {(unsettled > 0 ? 3 : 2)}. Last, /parley:write-peers in each one.\n\n" +
            "That is last on purpose. Every agent that registers makes every block already\n" +
            "written stale, so reading the roster once at the end is the only way each\n" +
            "repository's block is true when it is written — including the repositories that\n" +
            "were already agents before this run.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Finds the node catalogue this machine actually reads.
    /// </summary>
    /// <remarks>
    /// Writing consent into a file the node does not read would leave a repository listed,
    /// apparently startable, and never started — with nothing to show why. The node is handed the
    /// path this produced, so the two cannot disagree.
    /// </remarks>
    private static string ResolveNodeConfig(string? resolved)
    {
        // Already resolved by the entry point, which applied the whole precedence chain including
        // the selected project. Reading the environment again here would be a second resolution of
        // one setting, and two of those disagree eventually — silently, and about which file
        // consent is written into.
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        var configured = Environment.GetEnvironmentVariable(NodeCatalogue.PathVariable);

        return string.IsNullOrWhiteSpace(configured) ? NodeCatalogue.DefaultFileName : configured;
    }

    /// <summary>The catalogue a held handle holds, or null when it cannot be read.</summary>
    /// <param name="text">What the file holds.</param>
    /// <returns>The catalogue, an empty one for an empty file, or null when it is not readable.</returns>
    /// <remarks>
    /// Split from <see cref="ReadNode"/> so that a read through an exclusive handle answers exactly
    /// what a read by path answers. Two parsers agree until one of them is edited.
    /// </remarks>
    private static JsonObject? ReadNodeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(text)?.AsObject() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the node catalogue, refusing rather than starting from blank.
    /// </summary>
    /// <param name="path">The catalogue file.</param>
    /// <returns>The catalogue, or null when the file is there and cannot be read.</returns>
    /// <remarks>
    /// An unreadable file is not an empty one, and treating it as one destroys it: the catalogue is the
    /// operator's only record of which repositories may be started with nobody watching.
    /// <see cref="NodeCatalogue"/> returns empty on a parse failure because it only reads; this writes.
    /// </remarks>
    private static JsonObject? ReadNode(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the standing-instructions file, preferring one that is already there.
    /// </summary>
    /// <remarks>
    /// A project that uses AGENTS.md and is given a CLAUDE.md beside it has gained a second file
    /// its agent may never read — which is the failure the peers frame exists to prevent, wearing
    /// a different hat.
    /// </remarks>
    private static string InstructionsFile(string workspace)
    {
        foreach (var name in (string[])["CLAUDE.md", "AGENTS.md"])
        {
            var candidate = Path.Combine(workspace, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(workspace, "CLAUDE.md");
    }
}
