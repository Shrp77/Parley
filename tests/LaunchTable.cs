namespace Parley.Tests;

/// <summary>One launch configuration and what one machine should make of it (SPEC.md §4.1.2).</summary>
/// <param name="Name">What the case is.</param>
/// <param name="File">The file's text.</param>
/// <param name="Machine">The machine asking.</param>
/// <param name="Key">The entry asked about.</param>
/// <param name="Owns">The ownership line expected from the block, or null when it gives none.</param>
/// <param name="Broker">The address expected from the block, or null when it gives none.</param>
/// <param name="Problem">Text the refusal must carry, or null when the file is readable.</param>
internal sealed record LaunchCase(
    string Name,
    string File,
    string Machine,
    string Key,
    string? Owns,
    string? Broker,
    string? Problem)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>The one table every reader of a launch configuration is held to.</summary>
/// <remarks>
/// One table, because the requirement is that readers agree. A copy per reader is a place for them to come apart.
/// </remarks>
internal static class LaunchTable
{
    private const string Servers =
        """
        "mcpServers": {
          "parley-acme": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme", "PARLEY_OWNS": "Shared line." } },
          "parley-beta": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "beta" } }
        }
        """;

    private static string With(string section) => "{ " + Servers + ", \"parleyMachines\": " + section + " }";

    /// <summary>Every case.</summary>
    public static IReadOnlyList<LaunchCase> Cases { get; } =
    [
        new("no section", "{ " + Servers + " }", "laptop", "parley-acme", null, null, null),
        new(
            "this machine's block",
            With("""{ "laptop": { "parley-acme": { "PARLEY_OWNS": "Answers.", "PARLEY_BROKER": "http://127.0.0.1:19100" } } }"""),
            "laptop", "parley-acme", "Answers.", "http://127.0.0.1:19100", null),
        new(
            "another machine's block only",
            With("""{ "server": { "parley-acme": { "PARLEY_OWNS": "Changes.", "PARLEY_BROKER": "http://127.0.0.1:19100" } } }"""),
            "laptop", "parley-acme", null, null, null),
        new(
            "an empty block",
            With("""{ "laptop": { "parley-acme": { } } }"""),
            "laptop", "parley-acme", null, null, null),
        new(
            "another project's block",
            With("""{ "laptop": { "parley-beta": { "PARLEY_OWNS": "Beta's." } } }"""),
            "laptop", "parley-acme", null, null, null),
        new(
            "each project's own",
            With("""{ "laptop": { "parley-acme": { "PARLEY_OWNS": "Acme's." }, "parley-beta": { "PARLEY_OWNS": "Beta's." } } }"""),
            "laptop", "parley-beta", "Beta's.", null, null),
        new(
            "a role in a block",
            With("""{ "laptop": { "parley-acme": { "PARLEY_AGENT": "other" } } }"""),
            "laptop", "parley-acme", null, null, "PARLEY_AGENT"),
        new(
            "a workspace in a block",
            With("""{ "laptop": { "parley-acme": { "PARLEY_WORKSPACE": "/srv/infra" } } }"""),
            "laptop", "parley-acme", null, null, "where this file is"),
        new(
            "a mistake in another machine's block",
            With("""{ "server": { "parley-acme": { "PARLEY_MACHINE": "laptop" } } }"""),
            "laptop", "parley-acme", null, null, "PARLEY_MACHINE"),
        new(
            "a machine name that can never be one",
            With("""{ "LAPTOP_1": { "parley-acme": { } } }"""),
            "laptop", "parley-acme", null, null, "laptop-1"),
        new(
            "a block for no entry",
            With("""{ "laptop": { "parley-gone": { } } }"""),
            "laptop", "parley-acme", null, null, "parley-gone"),
        new(
            "a line that breaks one",
            With("""{ "laptop": { "parley-acme": { "PARLEY_OWNS": "two\nlines" } } }"""),
            "laptop", "parley-acme", null, null, "PARLEY_OWNS"),
        new(
            "an address that is not one",
            With("""{ "laptop": { "parley-acme": { "PARLEY_BROKER": "not an address" } } }"""),
            "laptop", "parley-acme", null, null, "PARLEY_BROKER"),
        new(
            "an empty value",
            With("""{ "laptop": { "parley-acme": { "PARLEY_OWNS": "" } } }"""),
            "laptop", "parley-acme", null, null, "Leave it out"),
        new("a section that is not one", With("[]"), "laptop", "parley-acme", null, null, "parleyMachines"),
        new(
            "an address with more than a host and port",
            With("""{ "laptop": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19100/a" } } }"""),
            "laptop", "parley-acme", null, null, "not an address"),
        new(
            "an address hiding a control character",
            With("""{ "laptop": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19100/\u001b]0;x\u0007" } } }"""),
            "laptop", "parley-acme", null, null, "not an address"),
    ];
}
