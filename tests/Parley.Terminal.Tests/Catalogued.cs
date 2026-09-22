using Parley.Configuration;
using Parley.Core;

namespace Parley.Terminal.Tests;

/// <summary>
/// A machine that has been told it can start some checkouts, built the way enrolment builds one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two files, because 018 split what they say.</b> A catalogue entry says how to start a checkout and
/// where, keyed by the agent's identity; what any project calls that checkout is declared by the checkout
/// itself. A fixture that wrote only the catalogue would describe a machine that can start something
/// nothing can name — which reads as an empty catalogue to everything that asks, so every assertion about
/// what it offers would pass by having nothing to offer.
/// </para>
/// <para>
/// <b>The directories have to outlive the catalogue object.</b> The names are read on demand rather than
/// held, so a helper that deleted its temp files before the test asked would answer for a machine whose
/// repositories had all been removed. It is disposable and the test class owns it.
/// </para>
/// </remarks>
internal sealed class Catalogued : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-catalogued-" + Path.GetRandomFileName());

    /// <summary>The project every checkout here answers in.</summary>
    public const string Project = "alpha";

    /// <summary>Builds a machine that can start one checkout per name given.</summary>
    /// <param name="roles">What each checkout answers to in <see cref="Project"/>.</param>
    /// <returns>The machine.</returns>
    public static Catalogued Offering(params string[] roles)
    {
        var machine = new Catalogued();

        machine.Write(roles);

        return machine;
    }

    /// <summary>What this machine can start.</summary>
    public NodeCatalogue Catalogue { get; private set; } = NodeCatalogue.Empty;

    /// <summary>Which agent each name belongs to.</summary>
    public IReadOnlyDictionary<string, AgentIdentity> Identities { get; private set; } =
        new Dictionary<string, AgentIdentity>(StringComparer.Ordinal);

    /// <summary>Where each checkout is.</summary>
    public IReadOnlyDictionary<string, string> Workspaces { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    /// <summary>Writes the catalogue and every checkout it names.</summary>
    /// <param name="roles">What each checkout answers to.</param>
    private void Write(IReadOnlyList<string> roles)
    {
        var identities = new Dictionary<string, AgentIdentity>(StringComparer.Ordinal);
        var workspaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<string>();

        Directory.CreateDirectory(_root);

        foreach (var role in roles)
        {
            var identity = AgentIdentity.Mint();
            var workspace = Path.Combine(_root, "ws-" + identity);

            Directory.CreateDirectory(workspace);

            File.WriteAllText(
                Path.Combine(workspace, LaunchConfiguration.FileName),
                $$"""
                {
                  "mcpServers": {
                    "parley-{{Project}}": {
                      "command": "parley-mcp",
                      "env": { "PARLEY_AGENT": {{System.Text.Json.JsonSerializer.Serialize(role)}} }
                    }
                  },
                  "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
                }
                """);

            identities[role] = identity;
            workspaces[role] = workspace;

            entries.Add(
                $"    \"{identity}\": {{ \"command\": \"claude\", "
                + $"\"workspace\": {System.Text.Json.JsonSerializer.Serialize(workspace)} }}");
        }

        var path = Path.Combine(_root, "node.json");

        File.WriteAllText(path, "{\n  \"agents\": {\n" + string.Join(",\n", entries) + "\n  }\n}\n");

        Catalogue = NodeCatalogue.ReadFrom(path);
        Identities = identities;
        Workspaces = workspaces;
    }
}
