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

    private const string DefaultManifest = "parley-agents.json";

    private static readonly string[] Guidance =
    [
        "Fill in `owns` for every repository you want enrolled, then:",
        "    parley enroll plan <this file>      say what would change",
        "    parley enroll apply <this file>     make the changes",
        "",
        "enroll: only rows with true are touched at all. Everything else in this file is",
        "  ignored, including its agent name and owns line, so a row you are unsure about",
        "  costs nothing to leave. Set it here, or re-run `parley enroll scan` and tick the",
        "  ones you want.",
        "",
        "owns: one line, at most 100 characters, saying what the repository owns AND what it",
        "  does not. The exclusion is the half that stops a peer asking the wrong agent.",
        "  Leave it empty and the repository is still enrolled, listed with no line; askers",
        "  fall back to its name.",
        "",
        "owns_approved: enrolling refuses a filled-in owns line that does not carry this. A",
        "  suggestion may be drafted for you - see _evidence below for what it was drawn from -",
        "  but what the registry promises is that a person read the line and said yes, so the",
        "  yes has to be written down. Read it, edit it if it is wrong, then set this true.",
        "",
        "  Check the exclusion half yourself. A repository can show what it contains and cannot",
        "  show what it has decided not to own: nothing here can tell that no tax code means",
        "  somebody else owns tax, or only that nobody has written it yet.",
        "",
        "_evidence: what the repository already says about itself, quoted with where each piece",
        "  came from. It is here so that approving a suggestion is checkable against something.",
        "  Ignored when enrolling; delete it freely.",
        "",
        "  The 100 is a CHECK constraint in the broker's schema, not a style rule: this text is",
        "  charged against every agent's working memory once per peer, every time the roster is",
        "  listed. It is for routing a question, not for explaining the boundary - the",
        "  explanation belongs in the answer you give when a peer asks. Control characters, line",
        "  separators and bidirectional overrides are refused too, because a roster entry able to",
        "  break its own line can imitate the entries around it.",
        "",
        "agent: taken from the directory name, which is a starting point and not an answer. It",
        "  should name the domain the repository owns - orders, billing, inventory - not the",
        "  directory, the framework or the model. Two repositories must never share one:",
        "  identity is the single thing Parley makes unforgeable, and two servers claiming one",
        "  name means either may collect the other's mail.",
        "",
        "unattended: true adds the repository to node.json, which is consent for a peer's",
        "  question to start it with nobody watching, spending your tokens. Leave it false",
        "  unless you mean it; withdraw it by removing the entry.",
        "",
        "Re-running scan against this file keeps what you have written here.",
    ];

    /// <summary>
    /// Runs one enroll subcommand.
    /// </summary>
    /// <param name="arguments">The subcommand and its operands.</param>
    /// <param name="selectAll">
    /// True or false to mark every repository found, skipping the picker; null to show it.
    /// </param>
    /// <param name="nodeConfig">
    /// The node catalogue the entry point resolved, and which input decided it. The manifest's
    /// own <c>node_config</c> still outranks it; this is what wins when the manifest names none.
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
    /// <param name="deployment">
    /// Every project this machine describes, so an address-pinned entry can be resolved and an
    /// unattended start granted elsewhere can be found.
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
        IReadOnlyList<ProjectSettings>? deployment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(nodeConfig);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        return arguments switch
        {
            ["scan", var root] => await ScanAsync(root, DefaultManifest, selectAll, output, error, cancellationToken)
                .ConfigureAwait(false),
            ["scan", var root, var into] => await ScanAsync(root, into, selectAll, output, error, cancellationToken)
                .ConfigureAwait(false),
            ["plan", var manifest] => await ApplyAsync(manifest, write: false, nodeConfig, output, error, project, deployment, cancellationToken)
                .ConfigureAwait(false),
            ["apply", var manifest] => await ApplyAsync(manifest, write: true, nodeConfig, output, error, project, deployment, cancellationToken)
                .ConfigureAwait(false),
            _ => await UsageAsync(error).ConfigureAwait(false),
        };
    }

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync(
            "parley enroll scan <directory> [<manifest>]   find repositories and tick the ones to enroll\n" +
            "                              [--all|--none]  skip the picker; for scripts and CI\n" +
            "parley enroll plan <manifest>                 say what would change, without changing it\n" +
            "parley enroll apply <manifest>                make the changes").ConfigureAwait(false);

        return Usage;
    }

    private static async Task<int> ScanAsync(
        string root,
        string into,
        bool? selectAll,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            await error.WriteLineAsync($"{root} is not a directory.").ConfigureAwait(false);
            return Usage;
        }

        // Merged with whatever is already at the output path, so re-scanning after somebody has
        // spent an afternoon writing owns lines does not throw them away. A scan that destroyed
        // hand-written prose would be discovered exactly once, by the person who wrote it.
        var previous = ReadManifest(into)?.Agents?
            .ToDictionary(e => e.Workspace, StringComparer.OrdinalIgnoreCase)
            ?? [];

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
            var carried = previous.GetValueOrDefault(workspace);

            entries.Add(new EnrollmentEntry(
                workspace,
                carried?.Agent ?? agent ?? SuggestName(name),
                carried?.Owns is { Length: > 0 } typed ? typed : owns ?? string.Empty,
                carried?.Unattended ?? false)
            {
                Enroll = carried?.Enroll ?? false,

                // An approval already given survives a re-scan; one never given is not invented
                // by re-running the command that would have asked for it.
                OwnsApproved = carried?.OwnsApproved ?? false,
                Evidence = Evidence(directory),
            });
        }

        if (entries.Count == 0)
        {
            await error.WriteLineAsync($"No git repositories directly under {root}.").ConfigureAwait(false);
            return Usage;
        }

        var chosen = await ChooseAsync(entries, selectAll, output, error).ConfigureAwait(false);

        if (chosen is null)
        {
            await output.WriteLineAsync("Cancelled. Nothing was written.").ConfigureAwait(false);
            return 0;
        }

        entries = chosen;

        var manifest = new EnrollmentManifest(Guidance, string.Empty, entries);
        var json = JsonSerializer.Serialize(manifest, SerialisationOptions.Indented) + "\n";

        try
        {
            await File.WriteAllTextAsync(into, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Could not write {into}: {ex.Message}").ConfigureAwait(false);
            return IoError;
        }

        var enrolling = entries.Count(e => e.Enroll);
        var described = entries.Count(e => e.Enroll && !string.IsNullOrEmpty(e.Owns));

        await output.WriteLineAsync(
            $"Wrote {into}: {enrolling} of {entries.Count} repositories marked for enrollment.")
            .ConfigureAwait(false);

        if (described > 0)
        {
            // Worth saying, because it is the difference between filling in eleven lines and
            // filling in the two that are new.
            await output.WriteLineAsync(
                $"  {described} already had an owns line; those were carried over.").ConfigureAwait(false);
        }

        if (enrolling > described)
        {
            await output.WriteLineAsync(
                $"  {enrolling - described} still need one. An empty owns line is allowed - the agent is " +
                "listed\n  with only its name - but it is the field peers route by.").ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"Fill in the owns lines, then: parley enroll plan {into}").ConfigureAwait(false);

        return 0;
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
            // Whatever a previous scan or a hand edit already marked is kept; what is new stays
            // unticked. Saying "nothing was marked" would be wrong the moment this is re-run
            // against a manifest somebody has already filled in, which is the common case.
            await error.WriteLineAsync(
                "No terminal to choose from, so nothing new was marked for enrollment. Either set\n" +
                "\"enroll\": true in the manifest, or re-run with --all to mark every repository found.")
                .ConfigureAwait(false);

            return entries;
        }

        var selection = ConsolePicker.Show(RepositorySelection.Over(
            [.. entries.Select(e => new SelectableRepository(
                Path.GetFileName(e.Workspace.TrimEnd('/')),
                e.Workspace,
                Note(e),
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
    private static string Note(EnrollmentEntry entry) =>
        IsWorktree(entry.Workspace) ? "worktree of another checkout"
        : File.Exists(Path.Combine(entry.Workspace, ".mcp.json"))
            && !string.IsNullOrEmpty(entry.Owns) ? "already an agent"
        : File.Exists(Path.Combine(entry.Workspace, ".mcp.json")) ? "has an .mcp.json"
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

    /// <summary>
    /// Reads a manifest, treating an absent or unreadable one as nothing to carry over.
    /// </summary>
    /// <remarks>
    /// Failing quietly is right here and only here: this is used to preserve a previous scan's
    /// answers, so the worst outcome of not reading one is that somebody retypes what they
    /// wrote. <see cref="ApplyAsync"/> reads the same file to act on it, and reports there.
    /// </remarks>
    private static EnrollmentManifest? ReadManifest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<EnrollmentManifest>(
                    File.ReadAllText(path), SerialisationOptions.Indented)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gathers what a repository already says about itself, for somebody to write from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Condition 1 of the amendment to SPEC.md §17 question 10: a suggested <c>owns</c> line
    /// travels with the material it was drawn from, each piece quoted and attributed, so that
    /// approving it is checkable against something rather than against nothing.
    /// </para>
    /// <para>
    /// <b>Quoted, never summarised.</b> A summary is one more thing to take on trust, and the
    /// point of this list is to be the part that does not have to be. Everything here is read
    /// off disk or out of the repository's own history.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string>? Evidence(string workspace)
    {
        var found = new List<string>();

        foreach (var name in (string[])["README.md", "README", "readme.md"])
        {
            var path = Path.Combine(workspace, name);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var line = File.ReadLines(path).FirstOrDefault(l =>
                    l.Trim().Length > 20
                    && !l.StartsWith('#')
                    && !l.StartsWith('[')
                    && !l.StartsWith("!["));

                if (line is { Length: > 0 })
                {
                    found.Add($"{name} — \"{Present(line.Trim())}\"");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Evidence is optional by definition: the person can still write the line.
            }

            break;
        }

        try
        {
            var directories = Directory.EnumerateDirectories(workspace)
                .Select(Path.GetFileName)
                .Where(n => n is { Length: > 0 } && !n.StartsWith('.'))
                .Order(StringComparer.Ordinal)
                .Take(8)
                .ToArray();

            if (directories.Length > 0)
            {
                found.Add("top-level — " + Present(string.Join(' ', directories)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // FR-012. Said before the git history is consulted, because what a repository answers in
        // *now* outranks what it once answered in — and because a repository already in one project
        // is the one somebody is most likely to enrol into a second by accident, having seen no sign
        // of the first.
        switch (Existing(workspace))
        {
            case null:
                found.Add("its .mcp.json could not be read");
                break;

            case { Count: > 0 } enrolled:
                found.Add("answers in: " + string.Join(
                    ", ",
                    enrolled.Select(entry =>
                        $"{entry.Project ?? "no project"} (as `{entry.Agent ?? "unnamed"}`)")));
                break;

            default:
                break;
        }

        if (PriorRegistration(workspace) is { } prior)
        {
            found.Add(prior);
        }

        return found.Count > 0 ? found : null;
    }

    /// <summary>
    /// Finds an <c>owns</c> line this repository was registered with before.
    /// </summary>
    /// <remarks>
    /// The strongest evidence there is, and the only kind that is not a suggestion at all: it is
    /// the person's own earlier sentence, recovered rather than composed. A repository whose
    /// <c>.mcp.json</c> was deleted still has it in history, which is the common state of a
    /// deployment being reconfigured.
    /// </remarks>
    private static string? PriorRegistration(string workspace)
    {
        try
        {
            var commit = Git(workspace, "log --format=%H --all -- .mcp.json")?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (commit is null)
            {
                return null;
            }

            var text = Git(workspace, $"show {commit}:.mcp.json");

            if (text is null)
            {
                return null;
            }

            var entry = LaunchEntries
                .Read(JsonNode.Parse(text)?["mcpServers"] as JsonObject)
                .FirstOrDefault(found => found.Owns is { Length: > 0 });

            return entry is not null
                ? $"registered before ({commit[..7]}) as `{entry.Agent}` — \"{Present(entry.Owns!)}\""
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                       or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Runs a read-only git command, or returns null if git cannot answer.</summary>
    /// <remarks>
    /// Silent on failure, and safely so: this only ever gathers evidence, so a machine without
    /// git produces a shorter list rather than a wrong one. That is the opposite of how the node
    /// catalogue is read, where an unreadable file must stop the run.
    /// </remarks>
    private static string? Git(string workspace, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{workspace}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            // Both streams are drained, and started before waiting. Reading one to the end while
            // the other is redirected and unread deadlocks the moment the unread pipe fills: the
            // child blocks writing, the parent blocks reading, and neither timeout applies
            // because nothing has exited. git writes to stderr routinely here — "not a git
            // repository" is the ordinary answer for a directory that is not one.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            Task.WaitAll([output, errors], TimeSpan.FromSeconds(5));

            return process.ExitCode == 0 && output.IsCompletedSuccessfully ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes a line of somebody else's file safe to show to a person who is deciding by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Evidence is untrusted text — a README from a repository that may have been cloned from
    /// anywhere — and the amendment to SPEC.md §17 question 10 rests on approval being
    /// <i>checkable against it</i>. Evidence that can reorder its own display or break its own
    /// line is not checkable: a README reading
    /// <c>owns billing&#x202E; .tidua sdrocer ylno tI</c> renders as a claim it does not make,
    /// and somebody approving a drafted line against it is approving against a lie.
    /// </para>
    /// <para>
    /// The same class the schema refuses in <c>owns</c> itself, for the same stated reason:
    /// anything able to break the line, hide itself, or reorder what follows lets one entry
    /// imitate the ones around it. Stripped rather than refused, because unlike an <c>owns</c>
    /// line this text is nobody's statement — dropping the character loses nothing a reader
    /// wanted.
    /// </para>
    /// </remarks>
    private static string Present(string line)
    {
        var kept = new System.Text.StringBuilder(line.Length);

        foreach (var rune in line.EnumerateRunes())
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value);

            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            kept.Append(rune);

            if (kept.Length > 160)
            {
                return kept.ToString(0, 159) + "…";
            }
        }

        return kept.ToString();
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

    private static async Task<int> ApplyAsync(
        string manifestPath,
        bool write,
        Resolved nodeConfig,
        TextWriter output,
        TextWriter error,
        ProjectSettings? project,
        IReadOnlyList<ProjectSettings>? deployment,
        CancellationToken cancellationToken)
    {
        EnrollmentManifest? manifest = null;
        string? unreadable = null;
        var unreadableCode = 0;

        try
        {
            var text = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<EnrollmentManifest>(text, SerialisationOptions.Indented);
        }
        catch (FileNotFoundException)
        {
            unreadable = $"{manifestPath} not found. Write one with: parley enroll scan <directory>";
            unreadableCode = Usage;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            unreadable = $"Could not read {manifestPath}: {ex.Message}";
            unreadableCode = Config;
        }

        // Said before the manifest is judged, and said even when it could not be read at all.
        //
        // The catalogue is the file this command records consent to run unattended in, and naming
        // it is what stops a run writing ten repositories where nothing reads them. It used to be
        // printed by the entry point, which knew where the resolution *started* but not where the
        // manifest would take it — so a manifest carrying `node_config` produced a run that named
        // one file and wrote another, which is worse than naming none: a wrong report is believed.
        //
        // Here it is true in both directions, and the provenance travels with it, because "which
        // of the four inputs won" is the question somebody reading this line is actually asking.
        await output.WriteLineAsync(
            $"node catalogue: {Naming(manifest?.NodeConfig, nodeConfig)}").ConfigureAwait(false);

        if (unreadable is not null)
        {
            await error.WriteLineAsync(unreadable).ConfigureAwait(false);
            return unreadableCode;
        }

        var rows = manifest?.Agents;

        if (rows is null || rows.Count == 0)
        {
            await error.WriteLineAsync($"{manifestPath} names no agents.").ConfigureAwait(false);
            return Config;
        }

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
            await error.WriteLineAsync(
                $"{manifestPath} marks no repositories for enrollment. Set \"enroll\": true on the ones " +
                "you want,\nor re-run `parley enroll scan` and tick them.").ConfigureAwait(false);

            return Config;
        }

        await output.WriteLineAsync(
            skipped > 0
                ? $"{entries.Length} repositories to enroll, {skipped} skipped."
                : $"{entries.Length} repositories to enroll.").ConfigureAwait(false);

        var problems = EnrollmentPlan.Validate(entries, Directory.Exists);

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

        var nodePath = ResolveNodeConfig(manifest?.NodeConfig, nodeConfig.Value);
        var node = ReadNode(nodePath);

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

        var nodeChanged = false;
        var written = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instructionsPath = InstructionsFile(entry.Workspace);
            var launchPath = Path.Combine(entry.Workspace, ".mcp.json");

            RepositoryPlan plan;

            try
            {
                plan = EnrollmentPlan.Describe(
                    entry,
                    File.Exists(launchPath) ? await File.ReadAllTextAsync(launchPath, cancellationToken).ConfigureAwait(false) : null,
                    File.Exists(instructionsPath) ? await File.ReadAllTextAsync(instructionsPath, cancellationToken).ConfigureAwait(false) : null,
                    listed.ContainsKey(entry.Agent),
                    project?.Name.Value,
                    deployment?.ToDictionary(
                        settings => settings.Name.Value,
                        settings => settings.Endpoint,
                        StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Refused rather than merged into. Everything this cannot merge is somebody's
                // own file in a shape it does not understand, and the failure of the alternative
                // is silent: the entries it could not read are simply not in what gets written.
                await error.WriteLineAsync(
                    $"{entry.Agent}: {launchPath} cannot be merged into — {ex.Message}.\n" +
                    "Fix or move it first; enrolling would replace what is there.").ConfigureAwait(false);

                return Config;
            }

            await output.WriteLineAsync($"\n{entry.Agent}  ({entry.Workspace})").ConfigureAwait(false);

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

            foreach (var note in plan.Notes)
            {
                await output.WriteLineAsync($"  {note}").ConfigureAwait(false);
            }

            if (!write)
            {
                continue;
            }

            try
            {
                // Through Enrolment, the one writer of these two files. This loop wrote them
                // itself until the terminal needed to write them too, and two copies of a file
                // write are how a surface and a command come to disagree about what makes a
                // repository an agent.
                written += (await Enrolment
                    .WriteAsync(plan, entry.Workspace, instructionsPath, cancellationToken)
                    .ConfigureAwait(false)).Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"{entry.Agent}: {ex.Message}").ConfigureAwait(false);
                return IoError;
            }

            if (plan.AddsNodeEntry)
            {
                // FR-013, refused rather than warned about. A launch entry does nothing until a
                // person opens the repository; a catalogue entry is consent for a machine to start
                // a turn with nobody watching, and a node starts one with the repository as its
                // working directory -- so two projects consenting is two models editing one
                // checkout. The launch entries written above stand; it is only this that is
                // singular.
                if (Catalogues.Elsewhere(deployment, entry.Workspace, nodePath) is { } already)
                {
                    await error.WriteLineAsync($"{entry.Agent}: {Catalogues.Refusal(already)}")
                        .ConfigureAwait(false);

                    return Config;
                }

                Enrolment.List(listed, entry.Agent, entry.Workspace);
                nodeChanged = true;
            }
        }

        if (!write)
        {
            await output.WriteLineAsync(
                "\nNothing was written. Run the same manifest through `parley enroll apply` to make these changes.")
                .ConfigureAwait(false);

            return 0;
        }

        if (nodeChanged)
        {
            try
            {
                await File.WriteAllTextAsync(
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

        // Step 1 names the node as well as the broker, and only when this run recorded consent
        // for one. A run that wrote `unattended` into the catalogue and then said "start a
        // broker" has described half a deployment: the repositories it just recorded as
        // startable are startable by a process it never mentioned, so they sit there reading as
        // available and are ghosted when somebody asks them anything. The catalogue is named
        // because a supervisor starts a process with no shell environment, and this is the one
        // moment somebody has both facts in front of them.
        var start = nodeChanged
            ? "  1. A broker and this machine's node:\n" +
              $"       parley serve --db <path to the store> --with-node --node-config {nodePath}\n" +
              "     Or install both as one logon entry:\n" +
              $"       parley autostart install --db <path to the store> --node-config {nodePath}\n"
            : "  1. A broker running:  parley serve --db <path to the store>\n";

        await output.WriteLineAsync(
            $"\nEnrolled {entries.Length} repositories ({written} files written).\n\n" +
            "Next, in this order:\n" +
            start +
            "  2. Restart your editor in each repository, and check the parley server connected.\n" +
            "  3. Only then, /parley:write-peers in each one.\n\n" +
            "Step 3 is last on purpose. Every agent that registers makes every block already\n" +
            "written stale, so reading the roster once at the end is the only way each\n" +
            "repository's block is true when it is written — including the repositories that\n" +
            "were already agents before this run.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Says which catalogue is about to be written, and which input decided that.
    /// </summary>
    /// <param name="fromManifest">The enrollment manifest's own <c>node_config</c>, if it has one.</param>
    /// <param name="resolved">What the entry point resolved, with its provenance.</param>
    /// <returns>The path and where it came from.</returns>
    /// <remarks>
    /// The provenance is the useful half. Somebody reading this line is deciding whether the file
    /// about to be written is the file their node reads, and "which of the inputs won" is the
    /// question that answers — a bare path leaves them comparing two strings by eye.
    /// </remarks>
    private static string Naming(string? fromManifest, Resolved resolved) =>
        string.IsNullOrWhiteSpace(fromManifest)
            ? resolved.ToString()

            // Not routed through Resolved, which describes the four inputs the entry point knows
            // about and has never heard of this one. A fifth case added there would be a value
            // that source can produce and that resolver cannot.
            : $"{fromManifest}   (enrollment manifest)";

    /// <summary>
    /// Finds the node catalogue this machine actually reads.
    /// </summary>
    /// <remarks>
    /// Writing consent into a file the node does not read would leave a repository listed,
    /// apparently startable, and never started — with nothing to show why. That used to be a
    /// warning about keeping two resolutions in step, one here and one the node performed on
    /// itself; the node no longer performs one, and is handed the path this produced. Keeping
    /// them in step is now structural rather than remembered.
    /// </remarks>
    private static string ResolveNodeConfig(string? fromManifest, string? resolved)
    {
        if (!string.IsNullOrWhiteSpace(fromManifest))
        {
            return fromManifest;
        }

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

    /// <summary>
    /// Reads the node catalogue, refusing rather than starting from blank.
    /// </summary>
    /// <returns>The catalogue, or null when the file is there and cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// <b>An unreadable file is not an empty one, and treating it as one destroys it.</b> This
    /// swallowed a parse error and returned blank, so a <c>node.json</c> listing six agents with
    /// one trailing comma in it was replaced by a file listing only the two just enrolled —
    /// silently, reporting success. That file is the operator's record of which repositories may
    /// be started with nobody watching, and nothing else holds a copy.
    /// </para>
    /// <para>
    /// <see cref="NodeCatalogue"/> reads the same file and does return empty on a parse failure,
    /// which is right there and wrong here: it only ever reads, so its worst outcome is that
    /// nothing starts automatically. This writes.
    /// </para>
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
