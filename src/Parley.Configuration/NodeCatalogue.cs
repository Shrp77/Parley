using System.Globalization;
using System.Text.Json;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// What this machine knows about the agents on it: which are here, and how to start them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The launch command lives here rather than in the store, and that is the point.</b> It is
/// local configuration on the machine it applies to, so the broker holds no executable command
/// line at all — a smaller trusted surface than any arrangement where it does, and one that
/// stays smaller as the deployment spreads across machines. See SPEC.md §4.
/// </para>
/// <para>
/// It also removes a chicken-and-egg the previous design never escaped. The command used to be
/// written into the registry by the agent's own server, which meant an agent that had never run
/// had no record of how to start it — the one case where knowing would have mattered. A file on
/// the machine has no such problem: it describes agents that are not running, because that is
/// the only state it is ever read in.
/// </para>
/// <para>
/// <b>Listing an agent here is the consent to start it.</b> There is no second flag, and there
/// was one: recording *how* to start a repository had been read as consent to run it unwatched,
/// so the two had to be prised apart. When starting is local configuration the question does not
/// arise — somebody put this file here and named the repositories in it.
/// </para>
/// <para>
/// <b>It lives in Configuration rather than Headless, and that move fixed a defect.</b> It sat
/// beside the node because the node reads it, but the terminal reads it too — and
/// <c>Parley.Headless</c> references <c>Parley.Store</c> for <c>AgentFleet</c>, so the terminal
/// had a route to a store. No check could catch that: <c>GraphTests</c> walks project references,
/// and the terminal had no project of its own, living inside the one assembly that owns the store
/// legitimately.
/// </para>
/// <para>
/// This class never needed to be there. It uses <c>System.Text.Json</c> and <c>Parley.Core</c> and
/// nothing else in that assembly. Here it is what this project already exists for: a per-machine
/// file that two programs read and cannot otherwise share a reader for, knowing nothing of
/// brokers — the same shape as the project manifest beside it.
/// </para>
/// </remarks>
public sealed class NodeCatalogue
{
    /// <summary>The environment variable naming the catalogue file.</summary>
    public const string PathVariable = "PARLEY_NODE_CONFIG";

    /// <summary>The file name looked for beside the store when nothing names one.</summary>
    public const string DefaultFileName = "node.json";

    private readonly Dictionary<AgentIdentity, Entry> _agents;

    private NodeCatalogue(Dictionary<AgentIdentity, Entry> agents) => _agents = agents;

    /// <summary>Keys in the file that are not identities, for a node to report rather than swallow.</summary>
    public IReadOnlyList<string> Unreadable { get; private init; } = [];

    /// <summary>One agent this machine can start.</summary>
    /// <param name="Command">How to start it.</param>
    /// <param name="Workspace">Where to start it, or null to use the node's own directory.</param>
    private sealed record Entry(string Command, string? Workspace);

    /// <summary>The machine this catalogue describes.</summary>
    /// <remarks>
    /// Taken from <see cref="MachineName"/> rather than detected again here, because the adapter
    /// builds its own name from the same place and the two must not normalise differently -- two
    /// detections that disagreed would produce an agent this node could never match to a
    /// catalogue entry, and therefore never start.
    /// </remarks>
    public static string Machine => MachineName.Current;

    /// <summary>An empty catalogue: a machine that has been told about no agents.</summary>
    /// <remarks>
    /// Not an error. A deployment with no supervision is the ordinary state of a machine
    /// somebody has only registered agents on, and it must behave as "starts nothing" rather
    /// than refusing to run.
    /// </remarks>
    public static NodeCatalogue Empty { get; } = new([]);

    /// <summary>
    /// Reads the catalogue from one named file.
    /// </summary>
    /// <param name="path">The file, already resolved by the caller.</param>
    /// <returns>The catalogue, or <see cref="Empty"/> if there is no file or it cannot be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// The node has no database path to look beside, because it does not know where the store is
    /// and has no business knowing: SPEC.md §4 has the broker own the file, and a node may be on
    /// a machine that cannot see it at all. So somebody has to name the file — but not this
    /// method.
    /// </para>
    /// <para>
    /// <b>The path is a parameter rather than something this class works out, and that is the
    /// fix rather than a tidying.</b> This replaced a reader that consulted
    /// <see cref="PathVariable"/> itself, which made the catalogue a setting resolved in two
    /// places: <c>--node-config</c> was taken centrally and honoured by <c>enroll</c>, while the
    /// node ignored it and read the variable again. The two disagreed exactly when it mattered —
    /// under a supervisor, which starts a process with no shell environment — and the failure was
    /// silent in the worst way, because a missing file reads as a machine that can start nothing
    /// and joins successfully to say so.
    /// </para>
    /// </remarks>
    public static NodeCatalogue ReadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ReadFile(path);
    }

    /// <summary>
    /// Reads the catalogue for this machine.
    /// </summary>
    /// <param name="databasePath">The store, used only to find the file beside it.</param>
    /// <returns>The catalogue, or <see cref="Empty"/> if there is no file or it cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// A malformed file yields an empty catalogue rather than an exception, and that is the
    /// safe direction: the failure is "nothing is started automatically", which an operator
    /// notices, rather than "the supervisor will not run", which takes the whole deployment
    /// down over one stray comma.
    /// </para>
    /// </remarks>
    public static NodeCatalogue Read(string databasePath)
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);

        if (string.IsNullOrWhiteSpace(path))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));

            path = directory is null ? DefaultFileName : Path.Combine(directory, DefaultFileName);
        }

        return ReadFile(path);
    }

    /// <summary>Reads one catalogue file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The catalogue, or <see cref="Empty"/>.</returns>
    private static NodeCatalogue ReadFile(string path) =>
        TryReadFrom(path, out var catalogue, out _) ? catalogue : Empty;

    /// <summary>
    /// Reads a catalogue, saying whether it could be read at all.
    /// </summary>
    /// <param name="path">The file, already resolved by the caller.</param>
    /// <param name="catalogue">What was read, or <see cref="Empty"/> when it could not be.</param>
    /// <param name="problem">Why it could not be read, or null when it could.</param>
    /// <returns>Whether the file was readable.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// <b>The distinction exists because the safe direction changes once a node is running.</b>
    /// At startup a malformed file yielding an empty catalogue is right, for the reason
    /// <see cref="Read"/> records: the failure becomes "nothing starts automatically", which
    /// somebody notices, rather than "the supervisor will not run", which takes a deployment down
    /// over a stray comma.
    /// </para>
    /// <para>
    /// A node watching the file cannot use that reading. Somebody half-way through saving leaves
    /// a moment of invalid JSON, and treating it as empty makes the node announce that it can
    /// start nothing — dropping roles it had already announced, because of a file that is about to
    /// be valid again. Keeping what it had is strictly better there, and it needs to know the
    /// difference between an empty catalogue and an unreadable one to do it.
    /// </para>
    /// <para>
    /// An absent file is readable and empty rather than unreadable. Deleting a catalogue is a
    /// deliberate act with an obvious meaning; a half-written one is an accident of timing.
    /// </para>
    /// </remarks>
    public static bool TryReadFrom(string path, out NodeCatalogue catalogue, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        catalogue = Empty;
        problem = null;

        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var entries = new Dictionary<AgentIdentity, Entry>();
            var strange = new List<string>();

            foreach (var entry in agents.EnumerateObject())
            {
                // **The key is an identity, and one that is not is reported rather than dropped.** A
                // hand-edited catalogue, or one written by a version that keyed by role, would otherwise
                // read as a machine that can start nothing — and a machine that can start nothing joins
                // successfully to say so, which is the silence this whole feature exists to remove.
                if (!AgentIdentity.TryRead(entry.Name, out var identity))
                {
                    strange.Add(entry.Name);

                    continue;
                }

                if (entry.Value.ValueKind == JsonValueKind.Object
                    && entry.Value.TryGetProperty("command", out var command)
                    && command.GetString() is { Length: > 0 } text)
                {
                    // The workspace is read here rather than taken from the registry, and that
                    // is the correction the node process forced. The registry's workspace is
                    // what an agent's adapter reported about itself, which is a description of
                    // where it ran last -- not an instruction about where to run it. A node that
                    // started a process in a directory a remote registration named would be
                    // taking a path from the network, which §13's trust direction refuses.
                    var workspace = entry.Value.TryGetProperty("workspace", out var directory)
                        ? directory.GetString()
                        : null;

                    entries[identity] = new Entry(text, workspace);
                }
            }

            catalogue = new NodeCatalogue(entries) { Unreadable = strange };

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException
                                      or UnauthorizedAccessException)
        {
            problem = ex.Message;

            return false;
        }
    }

    /// <summary>
    /// How to start one agent, if this machine knows.
    /// </summary>
    /// <param name="identity">Which agent.</param>
    /// <returns>Its launch command, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>Keyed by identity, where this was keyed by role.</b> A role is what one project calls an agent, and
    /// a checkout enrolled in two projects answers to a different one in each — so keyed by role, the same
    /// checkout was startable in the project whose catalogue named it and unstartable in the other. The
    /// machine half went with it: a catalogue is one machine's file, and an identity says nothing about
    /// machines, so there is no longer anything to check.
    /// </remarks>
    public string? CommandFor(AgentIdentity identity) =>
        _agents.TryGetValue(identity, out var entry) ? entry.Command : null;

    /// <summary>
    /// Where to start one agent, if this machine says.
    /// </summary>
    /// <param name="identity">Which agent.</param>
    /// <returns>Its directory, or <see langword="null"/> if the catalogue does not say.</returns>
    /// <remarks>
    /// Local configuration, like the command beside it. See the note where it is read for why it
    /// is not taken from the registry.
    /// </remarks>
    public string? WorkspaceFor(AgentIdentity identity) =>
        _agents.TryGetValue(identity, out var entry) ? entry.Workspace : null;

    /// <summary>Every agent this machine can start.</summary>
    public IReadOnlyCollection<AgentIdentity> Identities => _agents.Keys;

    /// <summary>
    /// What each catalogued workspace says it is called, in each project it answers in.
    /// </summary>
    /// <returns>One entry per identity, with what its repository declares and why it could not be read.</returns>
    /// <remarks>
    /// <para>
    /// <b>The names come from the repositories, not from here</b> (018 R3). A catalogue entry says how to
    /// start a checkout and nothing about projects, so the node asks each workspace what it answers to — and
    /// because that travels in the folder, a moved or renamed checkout needs no repair beyond repointing the
    /// one entry.
    /// </para>
    /// <para>
    /// Read on demand rather than held, because the node re-reads when the catalogue changes and a cached
    /// answer would be a second thing to keep in step with the files it was read from.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CataloguedWorkspace> Workspaces()
    {
        var found = new List<CataloguedWorkspace>();

        foreach (var (identity, entry) in _agents)
        {
            var read = LaunchConfiguration.TryRead(entry.Workspace, out var declared, out var problem);

            found.Add(new CataloguedWorkspace(
                identity,
                entry.Workspace,
                read ? declared.Memberships : [],
                problem));
        }

        return found;
    }

    /// <summary>
    /// Which agent a project means by a name.
    /// </summary>
    /// <param name="role">The agent half of the name, as the broker asks for it.</param>
    /// <param name="project">The project asking, or null to accept any.</param>
    /// <param name="identity">Which agent, when this returns true.</param>
    /// <param name="ambiguous">The workspaces that both claim it, when two do.</param>
    /// <returns><see langword="true"/> when exactly one catalogued workspace claims that name.</returns>
    /// <remarks>
    /// <para>
    /// <b>Names resolve to identities and never the other way.</b> A name is a project's word for something
    /// and this file has no projects, so nothing here is keyed by one — the names come from the repositories
    /// (018 R3) and this is where they are turned back into the thing that can be started.
    /// </para>
    /// <para>
    /// <b>Two claimants is refused rather than resolved</b> (018 R4). It is either a copied repository or an
    /// enrolment mistake, and an agent's name is the one thing Parley makes unforgeable — starting either
    /// would be guessing at identity, which Principle III exists to prevent. "First one wins" was rejected
    /// because the winner would depend on the order a dictionary was walked.
    /// </para>
    /// <para>
    /// A null project accepts a claim from any, which is what a supervisor with no project of its own needs.
    /// It narrows nothing else: two workspaces claiming one name are still two claimants.
    /// </para>
    /// </remarks>
    public bool TryResolve(
        string role,
        string? project,
        out AgentIdentity identity,
        out IReadOnlyList<string> ambiguous)
    {
        var claimed = Workspaces()
            .Where(workspace => workspace.Memberships.Any(membership =>
                string.Equals(membership.Name, role, StringComparison.Ordinal)
                && (project is null
                    || membership.Project is null
                    || string.Equals(membership.Project, project, StringComparison.Ordinal))))
            .ToList();

        if (claimed.Count == 1)
        {
            identity = claimed[0].Identity;
            ambiguous = [];

            return true;
        }

        identity = default;
        ambiguous = [.. claimed.Select(workspace => workspace.Workspace ?? workspace.Identity.ToString())];

        return false;
    }

    /// <summary>Every name this catalogue can start for a project, and what is in the way.</summary>
    /// <param name="project">The project, or null for any.</param>
    /// <returns>The names, and the workspaces that could not be read or that claim one name twice.</returns>
    /// <remarks>
    /// What a node announces. A name claimed by two workspaces is left out of the names and reported, so a
    /// person addressing it is told rather than started against whichever entry was walked first.
    /// </remarks>
    public (IReadOnlyList<string> Names, IReadOnlyList<string> Contested, IReadOnlyList<CataloguedWorkspace> Unread)
        Announceable(string? project)
    {
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        var unread = new List<CataloguedWorkspace>();

        foreach (var workspace in Workspaces())
        {
            if (workspace.Problem is not null)
            {
                unread.Add(workspace);

                continue;
            }

            foreach (var membership in workspace.Memberships)
            {
                if (project is not null
                    && membership.Project is not null
                    && !string.Equals(membership.Project, project, StringComparison.Ordinal))
                {
                    continue;
                }

                claims[membership.Name] = claims.GetValueOrDefault(membership.Name) + 1;
            }
        }

        return (
            [.. claims.Where(claim => claim.Value == 1).Select(claim => claim.Key).Order(StringComparer.Ordinal)],
            [.. claims.Where(claim => claim.Value > 1).Select(claim => claim.Key).Order(StringComparer.Ordinal)],
            unread);
    }

    /// <summary>Describes this catalogue, for a supervisor's start-up line.</summary>
    /// <returns>The machine and how many agents it can start.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Machine}: {_agents.Count} agent(s)");
}
