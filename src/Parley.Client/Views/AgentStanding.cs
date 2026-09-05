using Parley.Client.Rendering;
using Parley.Core;

namespace Parley.Client.Views;

/// <summary>One agent, as a project view shows it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Standing">The one phrase pairing what it has done with what can be done to it.</param>
/// <param name="Running">Whether the broker holds an open connection for it right now.</param>
/// <param name="EverRun">Whether anything has ever registered under this name.</param>
/// <param name="StartableHere">Whether this project's catalogue permits starting it.</param>
/// <param name="Retired">Whether an operator has withdrawn it.</param>
/// <param name="Tier">Its tier, or a mark when only the catalogue knows it.</param>
/// <param name="Owns">What it says it is for, or that it has said nothing.</param>
/// <param name="Workspace">Where it runs, as the registry or the catalogue records it.</param>
/// <param name="Seat">Whether this is a person's seat rather than an agent.</param>
/// <remarks>
/// <see cref="Seat"/> is carried rather than derived again by each caller because it decides what
/// may be done to a row: a seat is never offered for retiring or purging, whoever holds it.
/// </remarks>
public sealed record AgentStanding(
    string Name,
    string Standing,
    bool Running,
    bool EverRun,
    bool StartableHere,
    bool Retired,
    string Tier,
    string Owns,
    string Workspace,
    bool Seat);

/// <summary>
/// What an agent's row says, in words somebody can act on without reading the specification.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces <c>catalogued, not yet registered</c> and <c>not in this catalogue</c>.</b> The
/// owner read both on screen and could not say what either meant, which is a fair verdict: they
/// name the mechanism rather than the fact, and the mechanism is two files a person did not write.
/// </para>
/// <para>
/// There are exactly two facts to convey, and every phrase below is one or both of them:
/// </para>
/// <list type="bullet">
/// <item><description>has anything ever run under this name;</description></item>
/// <item><description>can anything on this machine start it.</description></item>
/// </list>
/// <para>
/// <b>The second is not the same as "is it startable".</b> The registry's own presence says a node
/// is connected, and a node starts only the roles its catalogue names — so an agent can read as
/// present-and-idle to the registry while nothing here is permitted to start it. That gap is
/// exactly what <c>not in this catalogue</c> was trying to say and failing to.
/// </para>
/// <para>
/// <b>Retirement outranks everything and is said alone.</b> It is a decision rather than an
/// observation, and an agent that is both retired and startable is not a case anybody needs the
/// detail of: nothing will be routed to it either way.
/// </para>
/// </remarks>
public static class Standing
{
    /// <summary>What a person's seat says instead of a state.</summary>
    public const string Seat = "your seat";

    /// <summary>An operator has withdrawn it.</summary>
    public const string Retired = "retired";

    /// <summary>It is attached to the broker now.</summary>
    public const string Running = "running";

    /// <summary>Registered before, not attached now, and this machine may start it.</summary>
    public const string Idle = "not running, can be started";

    /// <summary>Registered before, not attached now, and nothing here may start it.</summary>
    /// <remarks>
    /// The honest reading of an agent the registry knows and the catalogue does not: it exists
    /// because somebody opened its repository once, and it will exist again when somebody does.
    /// Naming the catalogue here would be naming the mechanism again.
    /// </remarks>
    public const string OnlyWhenOpened = "not running, and only runs when somebody opens it";

    /// <summary>Registered before, and its machine is not reachable.</summary>
    public const string NoMachine = "not running, and its machine is not connected";

    /// <summary>Nothing has ever run under this name, but this machine may start it.</summary>
    public const string NeverRun = "never run yet, can be started";

    /// <summary>Nothing has ever run under this name and nothing here may start it.</summary>
    /// <remarks>
    /// Reachable only from a hand-edited catalogue naming a role it does not hold a workspace for,
    /// which is why it says what to do rather than only what is true.
    /// </remarks>
    public const string NeverRunStuck = "never run yet, and nothing here can start it";

    /// <summary>
    /// Joins a registry to a catalogue's roles, keyed by agent name.
    /// </summary>
    /// <param name="registry">What the broker said, or null when it could not be asked.</param>
    /// <param name="catalogued">The roles this project's catalogue names.</param>
    /// <param name="seatAdapter">The adapter a person's seat registers under.</param>
    /// <returns>One row per name in either, ordered by name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalogued"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Takes the roles as plain strings rather than a catalogue object, because
    /// <c>NodeCatalogue</c> lives in an assembly this one does not reference and must not: the
    /// console reads this too, and a route from a web page to anything that opens files is the
    /// dependency <c>GraphTests</c> exists to refuse.
    /// </para>
    /// <para>
    /// A catalogue name that is not a well-formed agent name still gets a row, saying so. Dropping
    /// it would hide a hand-edited file's mistake in the one place somebody would look for it, and
    /// the node will refuse to start it for the same reason.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AgentStanding> Join(
        IReadOnlyList<AgentRecord>? registry,
        IReadOnlySet<string> catalogued,
        string seatAdapter)
    {
        ArgumentNullException.ThrowIfNull(catalogued);

        var rows = new List<AgentStanding>();

        foreach (var agent in registry ?? [])
        {
            var seat = string.Equals(agent.Adapter, seatAdapter, StringComparison.Ordinal);
            var startable = catalogued.Contains(agent.Name.Value);
            var retired = agent.Status == AgentStatus.Retired;
            var running = agent.Presence == AgentPresence.Running;

            rows.Add(new AgentStanding(
                agent.Name.Value,
                seat ? Seat : Phrase(retired, running, agent.Presence, startable, everRun: true),
                running,
                EverRun: true,
                startable,
                retired,
                agent.Tier.ToString().ToLowerInvariant(),
                Roster.HasStatedWhatItOwns(agent) ? agent.Owns ?? string.Empty : "not stated",
                agent.Workspace ?? "—",
                seat));
        }

        var registered = new HashSet<string>(
            (registry ?? []).Select(a => a.Name.Value), StringComparer.Ordinal);

        foreach (var role in catalogued.Where(role => !registered.Contains(role)))
        {
            rows.Add(new AgentStanding(
                role,
                NeverRun,
                Running: false,
                EverRun: false,
                StartableHere: true,
                Retired: false,
                "—",
                "not stated",
                "—",
                Seat: false));
        }

        return [.. rows.OrderBy(row => row.Name, StringComparer.Ordinal)];
    }

    /// <summary>The phrase for one agent.</summary>
    /// <param name="retired">Whether an operator withdrew it.</param>
    /// <param name="running">Whether it is attached now.</param>
    /// <param name="presence">What the broker observes about it.</param>
    /// <param name="startable">Whether this project's catalogue names its role.</param>
    /// <param name="everRun">Whether anything has ever registered under the name.</param>
    /// <returns>The phrase.</returns>
    internal static string Phrase(
        bool retired, bool running, AgentPresence presence, bool startable, bool everRun)
    {
        if (retired)
        {
            return Retired;
        }

        if (running)
        {
            return Running;
        }

        if (!everRun)
        {
            return startable ? NeverRun : NeverRunStuck;
        }

        // Order matters: the catalogue is checked before the machine, because "nothing here can
        // start it" is true whether or not a node happens to be connected, and telling somebody
        // their machine is disconnected when connecting it would change nothing sends them to fix
        // the wrong thing.
        if (!startable)
        {
            return OnlyWhenOpened;
        }

        return presence == AgentPresence.Stranded ? NoMachine : Idle;
    }
}
