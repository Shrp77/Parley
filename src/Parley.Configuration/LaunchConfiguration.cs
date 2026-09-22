using System.Text.Json;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>What one project calls the agent in a repository.</summary>
/// <param name="Project">The project, or <see langword="null"/> for an entry that names none.</param>
/// <param name="Name">The role it answers to there — the agent half of its name.</param>
public sealed record LaunchMembership(string? Project, string Name);

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

    private LaunchConfiguration(AgentIdentity identity, IReadOnlyList<LaunchMembership> memberships)
    {
        Identity = identity;
        Memberships = memberships;
    }

    /// <summary>Which agent this repository is, or the default when it says nothing.</summary>
    public AgentIdentity Identity { get; }

    /// <summary>What each project calls it.</summary>
    public IReadOnlyList<LaunchMembership> Memberships { get; }

    /// <summary>Nothing: a folder with no launch configuration, which is most folders.</summary>
    public static LaunchConfiguration None { get; } = new(default, []);

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
    public static bool TryRead(string? workspace, out LaunchConfiguration configuration, out string? problem)
    {
        configuration = None;
        problem = null;

        if (string.IsNullOrWhiteSpace(workspace))
        {
            return true;
        }

        var path = Path.Combine(workspace, FileName);

        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = $"{path} is not an object.";

                return false;
            }

            var identity = root.TryGetProperty(IdentityMember, out var written)
                && written.ValueKind == JsonValueKind.String
                && AgentIdentity.TryRead(written.GetString(), out var read)
                    ? read
                    : default;

            configuration = new LaunchConfiguration(identity, Declared(root));

            return true;
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = failure.Message;

            return false;
        }
    }

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
                Text(environment, "PARLEY_PROJECT") ?? LaunchKey.ProjectOf(server.Name), name));
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
