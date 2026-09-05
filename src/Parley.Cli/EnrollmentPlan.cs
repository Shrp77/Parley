using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Cli;

/// <summary>
/// What enrolling one repository would change, decided without touching a file.
/// </summary>
/// <remarks>
/// <para>
/// Enrolling repositories in bulk is the part of setup that batches, and the reason it is worth
/// batching is arithmetic rather than convenience. <c>/parley:write-peers</c> builds its block
/// from the live roster, so every agent that registers makes every block already written stale:
/// onboarding <i>n</i> repositories one at a time rewrites the first repository's block <i>n</i>
/// times. Registering everything first and reading the roster once at the end costs <i>n</i>,
/// and the blocks that come out are the only ones that were ever true.
/// </para>
/// <para>
/// <b>The decision is a pure function and the writing is not.</b> Same shape as
/// <see cref="AutostartPlan"/>, and for the same reason: a test cannot enroll eleven
/// repositories on a developer's machine without leaving something behind, but it can check
/// every decision that would be made. Merging JSON without dropping somebody else's MCP server,
/// and declining to rewrite a <c>## Peers</c> section, are where the defects live.
/// </para>
/// <para>
/// <b>It never composes an <c>owns</c> line, and it refuses one nobody has approved.</b> An
/// empty one leaves the key out entirely rather than writing a placeholder. SPEC.md §17
/// question 10, as amended, lets a tool <i>suggest</i> a line — but only carried beside the
/// evidence it came from, and only reaching a launch configuration once a person has affirmed
/// it, which <see cref="EnrollmentEntry.OwnsApproved"/> records. Composing the suggestion is
/// the adapter's job and affirming it is the person's; this decides neither, and enforces the
/// second.
/// </para>
/// </remarks>
public static class EnrollmentPlan
{
    /// <summary>The longest an <c>owns</c> line may be.</summary>
    /// <remarks>
    /// Forwarded from <see cref="OwnershipStatement"/> rather than restated. The number is a
    /// schema <c>CHECK</c> in the broker and a second copy of it here would eventually disagree
    /// with the one that is actually enforced — and the disagreement would show up as a
    /// registration refused at launch, which reads as an agent that simply never appeared.
    /// </remarks>
    public static int OwnsLimit => OwnershipStatement.MaxCharacters;

    /// <summary>The longest an agent name may be.</summary>
    public const int NameLimit = 32;

    /// <summary>
    /// The launch variable naming the agent a repository is.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal because it is now read as well as written: withdrawing
    /// checks it before removing an entry, and a name written under one spelling and read under
    /// another would silently make every entry look like nobody's.
    /// </remarks>
    public const string AgentVariable = "PARLEY_AGENT";

    /// <summary>
    /// The launch variable naming the project an agent belongs to.
    /// </summary>
    /// <remarks>
    /// The same string <c>Parley.Mcp</c>'s <c>ServerOptions</c> reads. Written here rather than
    /// referenced because this project must not depend on the adapter, and repeated in exactly
    /// one place on each side so a rename is two edits rather than a search.
    /// </remarks>
    public const string ProjectVariable = "PARLEY_PROJECT";

    /// <summary>The launch variable pinning an agent to one broker address.</summary>
    /// <remarks>
    /// Never written here — only read, to notice a repository that already belongs somewhere.
    /// An address goes stale the first time <c>parley project set</c> moves a port, which is why
    /// <see cref="ProjectVariable"/> is what enrolling writes.
    /// </remarks>
    public const string BrokerVariable = "PARLEY_BROKER";

    /// <summary>The heading that marks a peers block already present in a file.</summary>
    public const string PeersHeading = "## Peers";

    /// <summary>
    /// The placeholder peers block, as <c>/parley:setup</c> step 6 writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Repeated here rather than referenced because a frame that drifts from the one setup
    /// writes would leave two deployments claiming different invariants. The line saying the
    /// inbox is pull is the one an agent most needs and would most quietly lose: it is the
    /// difference between an agent that collects its messages and one that sits connected
    /// beside them.
    /// </para>
    /// <para>
    /// <b>Normalised to line feeds here rather than at each use.</b> It is a raw string literal,
    /// so it carries whatever endings this source file was checked out with — a clone with
    /// autocrlf on would otherwise emit a different file from one without, for the same command.
    /// Normalising at the use sites fixed the two that existed and left the next one to
    /// rediscover it.
    /// </para>
    /// </remarks>
    public static string Frame { get; } = FrameSource.ReplaceLineEndings("\n");

    private const string FrameSource = """
        ## Peers

        Other repositories have their own agents, reachable through Parley. They cannot see this
        repository and this repository cannot see theirs: only the text of a question and its answer
        crosses between them. `parley_inbox` is pull rather than push - call it when you start work,
        and again when you are waiting on a reply.

        - The roster has not been read yet. Run `/parley:write-peers` to fill this in.

        **Do not guess at what these repositories do, and do not edit them. Ask.** A question must say
        what you will do with each possible answer.

        """;

    /// <summary>
    /// How two workspace paths are compared for being the same directory.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on Windows and macOS, case-sensitive on Linux, because that is what the
    /// filesystem does. A single choice is wrong on one platform or the other: ordinal would let
    /// one Windows directory be claimed twice in two casings, and ignoring case would refuse a
    /// Linux manifest naming two directories that genuinely differ only by it.
    /// </remarks>
    public static StringComparer WorkspaceComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Says whether an agent name is one the registry will accept.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is well formed.</returns>
    public static bool IsWellFormedName(string? name) =>
        name is { Length: > 0 and <= NameLimit }
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>
    /// Finds everything wrong with a manifest, before any of it is written.
    /// </summary>
    /// <param name="entries">The repositories to enroll.</param>
    /// <param name="workspaceExists">Says whether a workspace is a directory that is there.</param>
    /// <returns>One line per problem, empty when the manifest is usable.</returns>
    /// <remarks>
    /// Whole-manifest rather than per-repository, and before any write, because a half-enrolled
    /// set is worse than none: the repository that did not get its entry is indistinguishable
    /// from one nobody meant to include, and the failure is silent from then on.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<EnrollmentEntry?> entries,
        Func<string, bool> workspaceExists)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(workspaceExists);

        var problems = new List<string>();
        var claimed = new Dictionary<string, int>(StringComparer.Ordinal);
        var occupied = new Dictionary<string, int>(WorkspaceComparer);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var where = $"agents[{i}]";

            if (entry is null)
            {
                // A null element in the array, which JSON allows and a hand-edited manifest
                // produces by deleting an object's body but not its comma. Caught here because
                // the alternative was a stack trace with no line number in it.
                problems.Add($"{where}: is empty; delete the row or fill it in");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Workspace) || !Path.IsPathRooted(entry.Workspace))
            {
                problems.Add($"{where}: workspace must be an absolute path, got '{entry.Workspace}'");
            }
            else if (!workspaceExists(entry.Workspace))
            {
                problems.Add($"{where}: workspace does not exist: {entry.Workspace}");
            }
            else if (occupied.TryGetValue(entry.Workspace, out var claimant))
            {
                // The mirror of the duplicate-name rule, and it bites harder. Two names over one
                // directory both write the same .mcp.json, so the second silently overwrites the
                // first — and both are reported enrolled. One agent exists, two were promised,
                // and nothing afterwards says which.
                problems.Add(
                    $"{where}: workspace is also claimed by agents[{claimant}]. One directory cannot " +
                    "be two agents: the second launch configuration would overwrite the first");
            }
            else
            {
                occupied[entry.Workspace] = i;
            }

            if (!IsWellFormedName(entry.Agent))
            {
                problems.Add(
                    $"{where}: agent '{entry.Agent}' must be lowercase letters, digits and hyphens, " +
                    $"starting with a letter, at most {NameLimit} characters");
            }
            else if (claimed.TryGetValue(entry.Agent, out var first))
            {
                // Not tidiness. Identity is the one thing Parley makes unforgeable, and it does
                // that by taking the name from the launch environment — which is worth nothing
                // if two servers claim one name, because either may then collect the other's
                // mail, and neither side of a message can tell.
                problems.Add(
                    $"{where}: agent '{entry.Agent}' is also used by agents[{first}]. Two repositories " +
                    "sharing a name means either may collect the other's mail");
            }
            else
            {
                claimed[entry.Agent] = i;
            }

            var owns = entry.Owns ?? string.Empty;

            // Asked of the same type the broker validates with, rather than checked again here.
            // A second copy of this rule would drift from the schema, and the schema is where it
            // is actually enforced — so anything this let through would be refused at
            // registration instead, and the symptom is an agent that never appears on a roster
            // with nothing said about why. The length is only the visible part of it: control
            // characters, U+2028, the bidirectional overrides and the tag block are all refused
            // too, because a roster entry that can break its own line can imitate the ones
            // around it.
            if (owns.Length > 0 && !OwnershipStatement.IsValid(owns, out var wrong))
            {
                problems.Add($"{where}: owns {wrong}");
            }

            // Condition 2 of the amendment to SPEC.md §17 question 10. A drafted line that
            // nobody has affirmed must not reach a launch configuration: what the registry
            // promises after that amendment is not that a person composed the line but that a
            // person read it and said yes, and a draft that ships unread would make the promise
            // vacuous. Refused rather than warned about, because a warning in a run that
            // succeeds is a warning nobody acts on.
            if (owns.Length > 0 && !entry.OwnsApproved)
            {
                problems.Add(
                    $"{where}: owns is filled in but not approved. Read it against its evidence, " +
                    "edit it if it is wrong, then set \"owns_approved\": true");
            }
        }

        return problems;
    }

    /// <summary>
    /// Works out what one repository needs.
    /// </summary>
    /// <param name="entry">The repository being enrolled.</param>
    /// <param name="launchConfiguration">Its current <c>.mcp.json</c>, or null when it has none.</param>
    /// <param name="instructions">Its current standing instructions, or null when the file is absent.</param>
    /// <param name="nodeAlreadyLists">Whether <c>node.json</c> already names this agent.</param>
    /// <param name="project">
    /// The project this repository is being made an agent of, or null to leave it unpinned.
    /// </param>
    /// <returns>What would change, and the contents that would be written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    /// <exception cref="System.Text.Json.JsonException">The existing launch configuration is not JSON.</exception>
    /// <exception cref="InvalidDataException">
    /// It is JSON, but a shape this cannot merge into without losing what is there — or it is
    /// already the agent of somewhere else.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><paramref name="project"/> is written as <c>PARLEY_PROJECT</c>, which is the name the
    /// adapter already reads.</b> <c>ServerOptions</c> resolves it through the manifest to the
    /// project's endpoint and refuses to guess when the name is unknown, on the grounds that
    /// connecting to another project's broker would put the agent in a roster it does not belong
    /// to. Pinning by name rather than by <c>PARLEY_BROKER</c> is what keeps it true after
    /// <c>parley project set</c> moves a port: the name is the stable half.
    /// </para>
    /// <para>
    /// <b>A repository that already belongs somewhere is refused rather than re-pointed.</b>
    /// Naming a second project would move an agent out of one deployment's roster and into
    /// another's, silently, from a surface whose person may not know the first exists. An entry
    /// pinned to a broker address is refused for the same reason and with less certainty — this
    /// cannot tell whether that address is the project it is being asked to join, and guessing
    /// wrong in the permissive direction is the move that loses an agent.
    /// </para>
    /// <para>
    /// An entry carrying neither is unpinned, which is what every repository enrolled before this
    /// existed looks like. Those are re-pointed and the note says so.
    /// </para>
    /// </remarks>
    public static RepositoryPlan Describe(
        EnrollmentEntry entry,
        string? launchConfiguration,
        string? instructions,
        bool nodeAlreadyLists,
        string? project = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var notes = new List<string>();
        var owns = (entry.Owns ?? string.Empty).Trim();

        // Merged into whatever is there rather than replacing it. A repository may already run
        // other MCP servers, and a setup step that quietly removed them would be discovered as
        // tooling that stopped working for a reason nobody would connect to Parley.
        var parsed = launchConfiguration is null ? null : JsonNode.Parse(launchConfiguration);

        if (parsed is not null and not JsonObject)
        {
            throw new InvalidDataException("the launch configuration is not a JSON object");
        }

        var root = parsed?.AsObject() ?? [];

        // Absent is the only shape that means "no servers yet". A wrong-typed `mcpServers` —
        // an array, a string — is somebody's file in a state this does not understand, and
        // casting it away replaces it: two server entries went missing this way, silently,
        // with the run reporting a successful enrollment. Third instance of that one class of
        // bug in this feature, which is why it is refused here rather than tidied up.
        if (root["mcpServers"] is not null and not JsonObject)
        {
            throw new InvalidDataException("\"mcpServers\" is not an object");
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = [];
            root["mcpServers"] = servers;
        }

        var existingParley = servers["parley"] as JsonObject;
        var existingEnv = existingParley?["env"] as JsonObject;
        var pinnedTo = Text(existingEnv, ProjectVariable);
        var pinnedAt = Text(existingEnv, BrokerVariable);

        if (project is not null)
        {
            if (pinnedTo is not null && !string.Equals(pinnedTo, project, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"it is already the agent of project '{pinnedTo}'");
            }

            if (pinnedTo is null && pinnedAt is not null)
            {
                throw new InvalidDataException(
                    $"it is already pinned to the broker at '{pinnedAt}'");
            }
        }

        var env = new JsonObject { [AgentVariable] = entry.Agent };

        if (project is not null)
        {
            env[ProjectVariable] = project;
        }

        if (owns.Length > 0)
        {
            env["PARLEY_OWNS"] = owns;
        }

        // The ${VAR:-default} form is environment-variable expansion, which .mcp.json supports.
        // The file is usually committed and the path is machine-specific, so the default is what
        // this machine uses and a teammate overrides it by setting the variable.
        env["PARLEY_WORKSPACE"] = "${PARLEY_WORKSPACE:-" + entry.Workspace.Replace('\\', '/') + "}";

        var desired = new JsonObject { ["command"] = "parley-mcp", ["env"] = env };
        var existing = servers["parley"];
        var already = existing is not null
            && JsonNode.DeepEquals(existing, desired);

        string? launchOutput = null;

        if (already)
        {
            notes.Add(".mcp.json: already correct");
        }
        else
        {
            var others = servers.Count(pair => !string.Equals(pair.Key, "parley", StringComparison.Ordinal));
            servers["parley"] = desired;
            launchOutput = root.ToJsonString(SerialisationOptions.Indented) + "\n";
            notes.Add(others > 0
                ? $".mcp.json: {(existing is null ? "create" : "update")} parley entry, leaving {others} other server(s)"
                : $".mcp.json: {(existing is null ? "create" : "update")} parley entry");
        }

        if (project is not null && pinnedTo is null && existingParley is not null)
        {
            // Said because it is a change of allegiance and the file did not previously record
            // one. A repository enrolled before projects existed names no project at all, so this
            // is the ordinary case rather than an alarming one — but somebody reading the run
            // should see which deployment their agent just joined.
            notes.Add($".mcp.json: named no project; it becomes an agent of '{project}'");
        }

        if (owns.Length == 0)
        {
            // Said out loud rather than left to be noticed on a roster. An agent with no line is
            // listed with none and askers fall back to its name, which is a real fallback and a
            // worse one than a sentence somebody meant.
            notes.Add(".mcp.json: no owns line; peers will see only the name");
        }

        string? instructionsOutput = null;

        if (instructions is null)
        {
            instructionsOutput = Frame;
            notes.Add("instructions: create with the peers frame");
        }
        else if (instructions.Contains(PeersHeading, StringComparison.Ordinal))
        {
            // Never rewritten. The block holds three kinds of line and only two of them belong
            // to any tool: the frame, the roster, and whatever the project has added about how
            // the roster is to be read. Nothing here can tell the third from the second, and the
            // thing that cannot tell is the thing about to delete it.
            notes.Add("instructions: already has a ## Peers section; left untouched");
        }
        else
        {
            // Matched to the file rather than written as LF. Appending a block in the other
            // ending leaves a file mixing both, which shows up as a whole-file diff the next
            // time anything normalises it — and line endings are a trap this repository has
            // already been caught by once, in the spec suite.
            var crlf = instructions.Contains("\r\n", StringComparison.Ordinal);
            var frame = crlf ? Frame.ReplaceLineEndings("\r\n") : Frame;
            var newline = crlf ? "\r\n" : "\n";

            var separator = instructions.EndsWith(newline + newline, StringComparison.Ordinal)
                ? string.Empty
                : instructions.EndsWith(newline, StringComparison.Ordinal) ? newline : newline + newline;

            instructionsOutput = instructions + separator + frame;
            notes.Add("instructions: append the peers frame");
        }

        var addsNode = entry.Unattended && !nodeAlreadyLists;

        if (entry.Unattended)
        {
            notes.Add(nodeAlreadyLists
                ? "node.json: already listed"
                : "node.json: add (consent to run unattended)");
        }

        return new RepositoryPlan(notes, launchOutput, instructionsOutput, addsNode);
    }

    /// <summary>Reads one environment variable out of a launch entry, when it is a string.</summary>
    /// <param name="env">The <c>env</c> object, or null when the entry has none.</param>
    /// <param name="name">The variable to read.</param>
    /// <returns>Its value, or null when it is absent, empty, or not a string.</returns>
    /// <remarks>
    /// A non-string value is treated as absent rather than refused. This reads somebody's file to
    /// find out whether it already belongs somewhere, and a number where a name should be answers
    /// that question with "not usefully" — the merge itself is guarded separately, by the shape
    /// checks above.
    /// </remarks>
    private static string? Text(JsonObject? env, string name) =>
        env?[name] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
