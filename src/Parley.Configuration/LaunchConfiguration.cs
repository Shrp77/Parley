using System.Text.Json;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>What one project calls the agent in a repository.</summary>
/// <param name="Project">The project, or <see langword="null"/> for an entry that names none.</param>
/// <param name="Name">The role it answers to there — the agent half of its name.</param>
/// <param name="Key">The key its entry sits under, which is what a machine's block is filed by.</param>
public sealed record LaunchMembership(string? Project, string Name, string Key);

/// <summary>
/// What a repository says it is to Parley: its identity, and what each project calls it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The repository declares its own membership, and this reads it.</b> A catalogue entry says how to start
/// a checkout and deliberately says nothing about projects: two places recording one thing is the drift that
/// let a checkout be startable in one project and not another. So the node asks each catalogued workspace
/// what it answers to, and the answer travels in the folder — a moved or renamed checkout needs no repair.
/// </para>
/// <para>
/// <b>Here rather than in <c>Parley.Setup</c>, for the reason the manifest is here.</b> Two programs read
/// this file and cannot otherwise share one reader: the node reads it to know what to announce, and
/// enrolment reads and writes it. Setup keeps the writing, which is the half with refusals and merges in it.
/// </para>
/// <para>
/// <b>Read-only, and forgiving in one direction only.</b> A file that cannot be read yields nothing and says
/// why; a file that is readable but names no Parley server yields nothing and does not. A repository running
/// other tooling's servers is the ordinary case and none of this program's business.
/// </para>
/// </remarks>
public sealed record LaunchConfiguration
{
    /// <summary>The file, inside a repository, that declares what it is to Parley.</summary>
    public const string FileName = ".mcp.json";

    /// <summary>The member carrying the repository's own identity.</summary>
    /// <remarks>
    /// Beside <c>mcpServers</c> rather than inside one of its entries, because it belongs to the repository
    /// and not to any one project's server — a checkout answering in two projects has one identity and two
    /// entries.
    /// </remarks>
    public const string IdentityMember = "parleyAgentId";

    /// <summary>The member holding what differs per machine (SPEC.md §4.1.2).</summary>
    public const string MachinesMember = "parleyMachines";

    /// <summary>The setting holding an ownership line, in an entry's environment or a block.</summary>
    public const string OwnsSetting = DeploymentEnvironment.OwnsVariable;

    private LaunchConfiguration(
        AgentIdentity identity,
        IReadOnlyList<LaunchMembership> memberships,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MachineBlock>>? machines)
    {
        Identity = identity;
        Memberships = memberships;
        Machines = machines;
    }

    /// <summary>Which agent this repository is, or the default when it says nothing.</summary>
    public AgentIdentity Identity { get; }

    /// <summary>What each project calls it.</summary>
    public IReadOnlyList<LaunchMembership> Memberships { get; }

    /// <summary>Nothing: a folder with no launch configuration, which is most folders.</summary>
    public static LaunchConfiguration None { get; } = new(default, [], null);

    /// <summary>What each machine says differently, by machine and then by key, or null when the file has no section.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MachineBlock>>? Machines { get; }

    /// <summary>A machine's block for an entry.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>The block, or null when it has none.</returns>
    public MachineBlock? BlockFor(string machine, string key) =>
        Machines is not null
        && Machines.TryGetValue(machine, out var blocks)
        && blocks.TryGetValue(key, out var block)
            ? block
            : null;

    /// <summary>Whether a machine answers through an entry.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>
    /// <see langword="true"/> when it has a block for it, or when no machine has: an entry from before blocks belongs to whichever
    /// machine opens it. An entry only other machines hold blocks for is theirs, so a machine that withdrew stops offering it.
    /// </returns>
    public bool AnswersHere(string machine, string key) =>
        BlockFor(machine, key) is not null
        || Machines is null
        || !Machines.Values.Any(blocks => blocks.ContainsKey(key));

    /// <summary>The entry a launch is, when exactly one fits.</summary>
    /// <param name="agent">What the launch's <c>PARLEY_AGENT</c> holds: a role, or a whole name.</param>
    /// <param name="project">What its <c>PARLEY_PROJECT</c> holds, if anything.</param>
    /// <returns>The entry, or null.</returns>
    public LaunchMembership? EntryFor(string agent, string? project)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var fits = Fitting(agent, project);

        if (fits.Count == 1)
        {
            return fits[0];
        }

        // A launch naming no project is the entry that names none, whatever else this role answers in.
        var unpinned = string.IsNullOrWhiteSpace(project) ? fits.Where(fit => fit.Project is null).ToList() : [];

        return unpinned.Count == 1 ? unpinned[0] : null;
    }

    /// <summary>Whether more than one entry could be a launch, so that which block applies cannot be told.</summary>
    /// <param name="agent">What the launch's <c>PARLEY_AGENT</c> holds.</param>
    /// <param name="project">What its <c>PARLEY_PROJECT</c> holds, if anything.</param>
    /// <returns><see langword="true"/> when several fit and none is the one.</returns>
    public bool IsAmbiguous(string agent, string? project) =>
        EntryFor(agent, project) is null && Fitting(agent, project).Count > 1;

    private List<LaunchMembership> Fitting(string agent, string? project)
    {
        var role = RoleOf(agent);

        return [.. Memberships
            .Where(membership => string.Equals(RoleOf(membership.Name), role, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(project)
                    || string.Equals(membership.Project, project.Trim(), StringComparison.Ordinal)))];
    }

    private static string RoleOf(string name)
    {
        var at = name.IndexOf('@', StringComparison.Ordinal);

        return at < 0 ? name : name[..at];
    }

    /// <summary>Reads what a workspace declares.</summary>
    /// <param name="workspace">The repository's directory.</param>
    /// <param name="configuration">What it declares, or <see cref="None"/>.</param>
    /// <param name="problem">Why it could not be read, or null.</param>
    /// <returns><see langword="true"/> when the folder could be looked at, whatever it held.</returns>
    /// <remarks>
    /// <b>"Could not be read" and "says nothing" are different answers</b>, and a node reports the first.
    /// A half-saved file that read as an empty one would drop every name a repository answers to, and the
    /// symptom is an agent that cannot be addressed for a reason nothing states.
    /// </remarks>
    public static bool TryRead(string? workspace, out LaunchConfiguration configuration, out string? problem) =>
        TryRead(workspace, out configuration, out problem, out _);

    /// <summary>Reads what a workspace declares, and says when only its machine section is what stopped it.</summary>
    /// <param name="workspace">The repository's directory.</param>
    /// <param name="configuration">What it declares, or <see cref="None"/>.</param>
    /// <param name="problem">Why it could not be read, or null.</param>
    /// <param name="machinesBroken">Whether the file is readable and its machine section breaks the rules.</param>
    /// <returns><see langword="true"/> when the folder could be looked at, whatever it held.</returns>
    public static bool TryRead(
        string? workspace,
        out LaunchConfiguration configuration,
        out string? problem,
        out bool machinesBroken)
    {
        configuration = None;
        problem = null;
        machinesBroken = false;

        if (string.IsNullOrWhiteSpace(workspace))
        {
            return true;
        }

        var path = Path.Combine(workspace, FileName);

        // **Walked before it is looked up** (lens 2 run 125): the node reads every catalogued repository's file on each
        // join and each start, and one replaced by a link to a share made it sign in to that host, as the person, every
        // time. Refused as unreadable, which a node reports, rather than read as saying nothing.
        if (OffMachineLinks.LeadsOffThisMachine(path))
        {
            problem = $"{path} leads to another machine, and Parley does not look up a path there.";

            return false;
        }

        if (!File.Exists(path))
        {
            return true;
        }

        string? text = null;

        try
        {
            text = StrictJson.ReadFile(path);

            return TryParse(text, path, out configuration, out problem, out machinesBroken);
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = failure.Message;

            // A member named twice is refused while parsing, before the section can be looked at. A file holding one that
            // cannot be parsed has a section nobody can read, and a file that could not be read at all may have one: neither
            // is the same as saying nothing, which would quietly run this agent with none of it.
            machinesBroken = text is null || text.Contains($"\"{MachinesMember}\"", StringComparison.Ordinal);

            return false;
        }
    }

    /// <summary>Reads what the text of a launch configuration declares.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="path">What to call it in a problem.</param>
    /// <param name="configuration">What it declares, or <see cref="None"/>.</param>
    /// <param name="problem">Why it could not be read, or null.</param>
    /// <param name="machinesBroken">Whether it is readable and its machine section breaks the rules.</param>
    /// <returns><see langword="true"/> when it could be read.</returns>
    /// <exception cref="JsonException">It is not JSON.</exception>
    /// <remarks>
    /// <b>A section that breaks the rules stops the whole file, whichever machine it concerns.</b> Every reader must reach
    /// the same answer, and a mistake in one machine's block must not wait for the day that machine restarts.
    /// </remarks>
    public static bool TryParse(
        string text,
        string path,
        out LaunchConfiguration configuration,
        out string? problem,
        out bool machinesBroken)
    {
        ArgumentNullException.ThrowIfNull(text);

        configuration = None;
        problem = null;
        machinesBroken = false;

        using var document = StrictJson.Document(text);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = $"{path} is not an object.";

            return false;
        }

        if (!TryMachines(root, path, out var machines, out problem))
        {
            machinesBroken = true;

            return false;
        }

        var identity = root.TryGetProperty(IdentityMember, out var written)
            && written.ValueKind == JsonValueKind.String
            && AgentIdentity.TryRead(written.GetString(), out var read)
                ? read
                : default;

        configuration = new LaunchConfiguration(identity, Declared(root), machines);

        return true;
    }

    /// <summary>Reads and checks the machine section.</summary>
    /// <param name="root">The file.</param>
    /// <param name="path">What to call it in a problem.</param>
    /// <param name="machines">The section, or null when the file has none.</param>
    /// <param name="problem">What is wrong with it, or null.</param>
    /// <returns><see langword="true"/> when it is absent or keeps the rules.</returns>
    private static bool TryMachines(
        JsonElement root,
        string path,
        out IReadOnlyDictionary<string, IReadOnlyDictionary<string, MachineBlock>>? machines,
        out string? problem)
    {
        machines = null;
        problem = null;

        if (!root.TryGetProperty(MachinesMember, out var section))
        {
            return true;
        }

        if (section.ValueKind != JsonValueKind.Object)
        {
            problem = $"{path}: \"{MachinesMember}\" is not an object.";

            return false;
        }

        var servers = root.TryGetProperty("mcpServers", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared
            : default;

        var found = new Dictionary<string, IReadOnlyDictionary<string, MachineBlock>>(StringComparer.Ordinal);

        foreach (var machine in section.EnumerateObject())
        {
            if (machine.Name.Length == 0 || !string.Equals(MachineName.Sanitise(machine.Name), machine.Name, StringComparison.Ordinal))
            {
                problem = $"{path}: \"{Printable(machine.Name)}\" under \"{MachinesMember}\" can never be a machine's name. " +
                          $"It would have to be written \"{MachineName.Sanitise(machine.Name)}\".";

                return false;
            }

            if (machine.Value.ValueKind != JsonValueKind.Object)
            {
                problem = $"{path}: machine '{machine.Name}' under \"{MachinesMember}\" is not an object.";

                return false;
            }

            var blocks = new Dictionary<string, MachineBlock>(StringComparer.Ordinal);

            foreach (var entry in machine.Value.EnumerateObject())
            {
                if (servers.ValueKind != JsonValueKind.Object
                    || !LaunchKey.IsOurs(entry.Name)
                    || !servers.TryGetProperty(entry.Name, out _))
                {
                    problem = $"{path}: machine '{machine.Name}' has a block for \"{Printable(entry.Name)}\", " +
                              "and the file has no Parley server by that name.";

                    return false;
                }

                if (!TryBlock(entry.Value, $"{path}: machine '{machine.Name}', \"{entry.Name}\"", out var block, out problem))
                {
                    return false;
                }

                blocks[entry.Name] = block!;
            }

            found[machine.Name] = blocks;
        }

        machines = found;

        return true;
    }

    private static bool TryBlock(JsonElement block, string where, out MachineBlock? read, out string? problem)
    {
        read = null;
        problem = null;

        if (block.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} is not an object.";

            return false;
        }

        string? owns = null;
        string? broker = null;

        foreach (var setting in block.EnumerateObject())
        {
            var isOwns = string.Equals(setting.Name, OwnsSetting, StringComparison.Ordinal);

            if (!isOwns && !string.Equals(setting.Name, BrokerEndpoint.Variable, StringComparison.Ordinal))
            {
                problem = $"{where} holds \"{Printable(setting.Name)}\". A block may hold only {OwnsSetting} and " +
                          $"{BrokerEndpoint.Variable}." +
                          (string.Equals(setting.Name, "PARLEY_WORKSPACE", StringComparison.Ordinal)
                              ? " Each machine works its folder out from where this file is."
                              : string.Empty);

                return false;
            }

            if (setting.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(setting.Value.GetString()))
            {
                problem = $"{where}: {setting.Name} must be text with something in it. Leave it out to give none.";

                return false;
            }

            var value = setting.Value.GetString()!;

            if (isOwns)
            {
                if (!OwnershipStatement.IsValid(value, out var invalid) || !OwnershipStatement.IsLaunchable(value, out invalid))
                {
                    problem = $"{where}: {OwnsSetting} cannot be used: {invalid}";

                    return false;
                }

                owns = value;
            }
            else
            {
                try
                {
                    broker = BrokerEndpoint.Read(_ => value);
                }
                catch (InvalidOperationException invalid)
                {
                    problem = $"{where}: {invalid.Message}";

                    return false;
                }
            }
        }

        read = new MachineBlock(owns, broker);

        return true;
    }

    // A name in this file is somebody's text, and it reaches a terminal in a problem.
    private static string Printable(string text) =>
        new([.. text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator ? '?' : c)]);

    /// <summary>Every project this repository answers in, and under what name.</summary>
    /// <param name="root">The file.</param>
    /// <returns>One entry per Parley server it declares.</returns>
    /// <remarks>
    /// The project is taken from the entry's environment first and its key second, because the environment
    /// is what the server is actually launched with and the key is how this tooling finds it. Everything
    /// that is not this tooling's is skipped rather than reported as damaged.
    /// </remarks>
    private static IReadOnlyList<LaunchMembership> Declared(JsonElement root)
    {
        if (!root.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var found = new List<LaunchMembership>();

        foreach (var server in servers.EnumerateObject())
        {
            if (!LaunchKey.IsOurs(server.Name) || server.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var environment = server.Value.TryGetProperty("env", out var env)
                && env.ValueKind == JsonValueKind.Object
                    ? env
                    : default;

            if (Text(environment, "PARLEY_AGENT") is not { Length: > 0 } name)
            {
                continue;
            }

            found.Add(new LaunchMembership(
                Text(environment, "PARLEY_PROJECT") ?? LaunchKey.ProjectOf(server.Name), name, server.Name));
        }

        return found;
    }

    private static string? Text(JsonElement environment, string member) =>
        environment.ValueKind == JsonValueKind.Object
        && environment.TryGetProperty(member, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One catalogued checkout, and what it says it is called.</summary>
/// <param name="Identity">Which agent it is.</param>
/// <param name="Workspace">Where it is, or null when the catalogue does not say.</param>
/// <param name="Memberships">What each project calls it, empty when it declares nothing or could not be read.</param>
/// <param name="Problem">Why its launch configuration could not be read, or null.</param>
/// <remarks>
/// <b>The problem travels with the entry rather than being thrown.</b> One unreadable repository must not
/// stop a node announcing the rest, and it must not vanish either: an agent that cannot be addressed for a
/// reason nothing states is the shape of defect this feature exists to remove.
/// </remarks>
public sealed record CataloguedWorkspace(
    AgentIdentity Identity,
    string? Workspace,
    IReadOnlyList<LaunchMembership> Memberships,
    string? Problem);
