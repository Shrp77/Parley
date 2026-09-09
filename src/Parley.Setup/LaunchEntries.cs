using System.Text.Json.Nodes;

namespace Parley.Setup;

/// <summary>One parley server in a repository's launch configuration.</summary>
/// <param name="Key">The name it sits under.</param>
/// <param name="Project">The project it answers in, or null when it names none.</param>
/// <param name="Pinned">The project its <i>environment</i> names, or null.</param>
/// <param name="Agent">The role it speaks for, or null when it names nobody.</param>
/// <param name="Owns">What it tells peers it owns, or null.</param>
/// <param name="Broker">The address it is pinned to, or null.</param>
/// <param name="Command">What it runs, or null when it names nothing.</param>
/// <param name="Mismatched">Whether its key and its environment name different projects.</param>
/// <remarks>
/// <para>
/// <b><paramref name="Project"/> is taken from the environment first and the key second, because the
/// environment is what the adapter actually reads.</b> The key is how a client namespaces the server's
/// tools and how this tooling decides what it may write; it is not what decides which broker the agent
/// reaches. Where the two disagree, the running agent follows the environment, so anything reasoning
/// about which project an entry serves must follow it too.
/// </para>
/// <para>
/// <b><paramref name="Mismatched"/> exists so that disagreement is refused rather than resolved.</b>
/// It can only arise from a hand edit, and this file is edited by hand often — but the two readings
/// send a withdrawal to two different entries, and picking one silently is how the wrong project's
/// configuration gets deleted.
/// </para>
/// </remarks>
public sealed record LaunchEntry(
    string Key,
    string? Project,
    string? Pinned,
    string? Agent,
    string? Owns,
    string? Broker,
    string? Command,
    bool Mismatched)
{
    /// <summary>Whether this is an entry this tooling wrote, rather than somebody else's server.</summary>
    /// <remarks>
    /// <para>
    /// <b>Broader than "names an agent", and the breadth is the point.</b> An entry carrying
    /// <c>PARLEY_PROJECT</c> or <c>PARLEY_BROKER</c> and no <c>PARLEY_AGENT</c> is this tooling's own
    /// entry with a name cleared out of it — the shape somebody leaves behind editing the file — and
    /// refusing to write there would refuse the repair.
    /// </para>
    /// <para>
    /// <b>A different question from whether it may be removed.</b> Withdrawing asks
    /// <c>Enrolment.NamedBy</c>, which requires the name, because deleting the wrong entry is worse
    /// than declining to write one. This asks only whether writing here would destroy something that
    /// was never ours.
    /// </para>
    /// <para>
    /// <b>It reads <see cref="Pinned"/> and never <see cref="Project"/>, which is the whole of what
    /// makes it work.</b> <c>Project</c> falls back to the key, so a stranger's server sitting at
    /// <c>parley-alpha</c> has a project of <c>alpha</c> merely by being there — and asking that would
    /// have declared every stranger ours and written straight over them. Caught by
    /// <c>StrangerKeyTests</c>, which is what it was written for.
    /// </para>
    /// </remarks>
    public bool Ours =>
        Agent is not null
        || Pinned is not null
        || Broker is not null
        || Owns is not null
        || string.Equals(Command, EnrollmentPlan.AdapterCommand, StringComparison.Ordinal);
}

/// <summary>
/// Reads the parley servers out of a launch configuration, leaving everything else alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repository has as many enrolments as it has entries, and "the first one" is never the right
/// answer to what a repository is.</b> Before this existed, two readers took
/// <c>mcpServers.parley.env</c> by literal key — so a repository enrolled under any other key read as
/// enrolled nowhere, which is how one comes to be enrolled twice in one project.
/// </para>
/// <para>
/// Everything that is not this tooling's is skipped rather than reported as damaged. A repository may
/// run any number of other MCP servers, and none of them is this program's business.
/// </para>
/// </remarks>
public static class LaunchEntries
{
    /// <summary>Every parley server in an <c>mcpServers</c> object.</summary>
    /// <param name="servers">The object, or null when the file names no servers.</param>
    /// <returns>The entries this tooling owns, in the order the file holds them.</returns>
    /// <exception cref="InvalidDataException">Two servers share one name.</exception>
    /// <remarks>
    /// <b>A duplicated member is refused here rather than crashing three call sites.</b>
    /// <c>{"parley-alpha": {…}, "parley-alpha": {…}}</c> is legal JSON and is what a careless merge
    /// produces; <c>JsonNode.Parse</c> accepts it and throws <c>ArgumentException</c> later, out of
    /// the enumerator, where nothing was catching it — so a scan crashed, a withdrawal crashed, and
    /// <c>enroll apply</c> crashed part-way through a run, leaving the half-applied set that command
    /// refuses by design. Translated to the exception this codebase already means by "somebody's
    /// file is in a shape I will not merge into", which every caller of that path already handles.
    /// </remarks>
    public static IReadOnlyList<LaunchEntry> Read(JsonObject? servers)
    {
        if (servers is null)
        {
            return [];
        }

        var found = new List<LaunchEntry>();

        foreach (var (key, node) in Members(servers))
        {
            if (!LaunchKey.IsOurs(key))
            {
                continue;
            }

            var env = node?["env"] as JsonObject;
            var fromKey = LaunchKey.ProjectOf(key);
            var fromEnv = Text(env, EnrollmentPlan.ProjectVariable);

            found.Add(new LaunchEntry(
                key,
                fromEnv ?? fromKey,
                fromEnv,
                Text(env, EnrollmentPlan.AgentVariable),
                Text(env, EnrollmentPlan.OwnsVariable),
                Text(env, EnrollmentPlan.BrokerVariable),
                node?["command"] is JsonValue command
                && command.TryGetValue<string>(out var name)
                    ? name
                    : null,
                fromKey is not null
                    && fromEnv is not null
                    && !string.Equals(fromKey, fromEnv, StringComparison.Ordinal)));
        }

        return found;
    }

    /// <summary>Where a project's enrolment lands, and what is already there.</summary>
    /// <param name="servers">The <c>mcpServers</c> object, or null.</param>
    /// <param name="project">The project being enrolled into, or null when none is named.</param>
    /// <returns>The key to write at, and the entry it would update, or null when it would be new.</returns>
    /// <remarks>
    /// <para>
    /// <b>The order is the feature.</b> Without one, a repository already in <c>alpha</c> matches both
    /// "update the legacy entry" and "add a project-keyed entry", and the difference between them is
    /// whether a working agent's tool names change underneath it.
    /// </para>
    /// <list type="number">
    /// <item>The derived key, if something is there — <b>whatever it says about itself</b>. An entry at
    /// the key this would write at is the caller's problem to refuse or update, and hiding it here
    /// would mean writing over it.</item>
    /// <item>The legacy key, when its environment pins this project. <b>Updated where it stands and
    /// never renamed</b>: the key is a component of every tool name the agent already sees, so a
    /// rename edits an agent's vocabulary to achieve nothing.</item>
    /// <item>The legacy key, when it pins no project at all and one is being named — every repository
    /// enrolled before projects existed. This is the re-point that already happens today, and it comes
    /// after the case above so that an unpinned entry cannot swallow an enrolment meant to be a second
    /// one.</item>
    /// <item>Nothing, and the derived key is free.</item>
    /// </list>
    /// </remarks>
    public static (string Key, LaunchEntry? Existing) Landing(JsonObject? servers, string? project)
    {
        var key = LaunchKey.For(project);
        var entries = Read(servers);

        if (entries.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal))
            is { } atKey)
        {
            return (key, atKey);
        }

        if (project is not null
            && entries.FirstOrDefault(entry =>
                string.Equals(entry.Key, LaunchKey.Unnamed, StringComparison.Ordinal)
                && string.Equals(entry.Project, project, StringComparison.Ordinal)) is { } pinnedHere)
        {
            return (pinnedHere.Key, pinnedHere);
        }

        if (project is not null
            && entries.FirstOrDefault(entry =>
                string.Equals(entry.Key, LaunchKey.Unnamed, StringComparison.Ordinal)
                && entry.Project is null) is { } unpinned)
        {
            return (unpinned.Key, unpinned);
        }

        return (key, null);
    }

    /// <summary>The object's members, refusing a file that names one server twice.</summary>
    /// <param name="servers">The object.</param>
    /// <returns>Every member, once.</returns>
    /// <exception cref="InvalidDataException">Two servers share one name.</exception>
    /// <remarks>
    /// The enumeration is materialised on purpose: the throw happens on the first move, and a lazy
    /// sequence would carry it into the middle of a caller's loop — which is exactly where an
    /// unhandled one did the damage this exists to stop.
    /// </remarks>
    private static IReadOnlyList<KeyValuePair<string, JsonNode?>> Members(JsonObject servers)
    {
        try
        {
            return [.. servers];
        }
        catch (ArgumentException duplicate)
        {
            throw new InvalidDataException(
                "\"mcpServers\" names one server twice, so this cannot tell which entry is which",
                duplicate);
        }
    }

    /// <summary>One environment variable, when it holds text.</summary>
    /// <param name="env">The entry's environment, or null.</param>
    /// <param name="name">The variable.</param>
    /// <returns>Its value, or null when it is absent, empty or not a string.</returns>
    private static string? Text(JsonObject? env, string name) =>
        env?[name] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
