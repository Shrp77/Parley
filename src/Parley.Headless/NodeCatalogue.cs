using System.Globalization;
using System.Text.Json;
using Parley.Core;

namespace Parley.Headless;

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
/// </remarks>
public sealed class NodeCatalogue
{
    /// <summary>The environment variable naming the catalogue file.</summary>
    public const string PathVariable = "PARLEY_NODE_CONFIG";

    /// <summary>The file name looked for beside the store when nothing names one.</summary>
    public const string DefaultFileName = "node.json";

    private readonly Dictionary<string, Entry> _agents;

    private NodeCatalogue(Dictionary<string, Entry> agents) => _agents = agents;

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
    private static NodeCatalogue ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

            foreach (var entry in agents.EnumerateObject())
            {
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

                    entries[entry.Name] = new Entry(text, workspace);
                }
            }

            return new NodeCatalogue(entries);
        }
        catch (Exception ex) when (ex is JsonException or IOException
                                      or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// How to start one agent, if this machine knows.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its launch command, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// Keyed by role, and the machine half is checked rather than ignored. A catalogue describes
    /// one machine, so an entry for <c>infra</c> here means <c>infra@thisMachine</c> and nothing
    /// else — quietly matching <c>infra@server-b</c> would have this node start a process for an
    /// agent that lives somewhere else, under a name already taken.
    /// </para>
    /// </remarks>
    public string? CommandFor(AgentName agent) => Find(agent)?.Command;

    /// <summary>
    /// Where to start one agent, if this machine says.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its directory, or <see langword="null"/> if the catalogue does not say.</returns>
    /// <remarks>
    /// Local configuration, like the command beside it. See the note where it is read for why it
    /// is not taken from the registry.
    /// </remarks>
    public string? WorkspaceFor(AgentName agent) => Find(agent)?.Workspace;

    private Entry? Find(AgentName agent) =>
        string.Equals(agent.Machine, Machine, StringComparison.Ordinal)
            && _agents.TryGetValue(agent.Role, out var entry)
                ? entry
                : null;

    /// <summary>The roles this machine can start.</summary>
    public IReadOnlyCollection<string> Roles => _agents.Keys;

    /// <summary>Describes this catalogue, for a supervisor's start-up line.</summary>
    /// <returns>The machine and how many agents it can start.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Machine}: {_agents.Count} agent(s)");
}
