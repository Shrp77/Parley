namespace Parley.Cli;

/// <summary>
/// What the supervisor entry on this machine was registered to run, read back from it.
/// </summary>
/// <param name="Store">The store an explicit entry serves, or null for one that serves the manifest.</param>
/// <param name="Listen">Where it was told to listen, or null for the default.</param>
/// <param name="NodeConfig">The catalogue an explicit entry hosts a node from, or null.</param>
/// <param name="Console">Where the console listens, or null for an entry that hosts none.</param>
/// <param name="Manifest">The manifest an entry serves, or null for an explicit one.</param>
/// <remarks>
/// <para>
/// <b>The entry is the one place a deployment's paths are written down on a machine that names no
/// projects.</b> Everything that used to ask a person for them — the installer's store prompt, the
/// terminal's naming form — was asking for values already sitting in a scheduled task or a unit
/// file, and the person answering had to go and look them up there. Reading the entry back is
/// what lets those questions be pre-filled instead of asked.
/// </para>
/// <para>
/// Parsed from the argument string <see cref="AutostartPlan"/> composed, with the same
/// <see cref="AutostartPlan.Split"/> that takes it apart for launchd. One writer and one reader,
/// so a path with a space in it survives the round trip the same way on every platform.
/// </para>
/// <para>
/// <see cref="Serves"/> is the distinction that matters to a reader. An entry with a store serves
/// one deployment by explicit paths, exactly as before manifests existed; one with none serves
/// every project the manifest names, and a project added later comes up within seconds without
/// the entry changing, because the broker watches the manifest.
/// </para>
/// </remarks>
public sealed record InstalledEntry(
    string? Store,
    string? Listen,
    string? NodeConfig,
    string? Console,
    string? Manifest)
{
    /// <summary>Whether this entry serves the manifest rather than one explicit store.</summary>
    public bool ServesManifest => Store is null;

    /// <summary>What the entry serves, in words for a status line.</summary>
    public string Serves =>
        ServesManifest
            ? $"every project in {Manifest ?? "this machine's manifest"}"
            : $"the store at {Store}";

    /// <summary>Reads the entry back from the argument string it was registered with.</summary>
    /// <param name="arguments">The arguments, as <see cref="AutostartPlan.Arguments"/> rendered them.</param>
    /// <returns>The entry, or null when the arguments are not a <c>serve</c> command line.</returns>
    public static InstalledEntry? Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var parts = AutostartPlan.Split(arguments);

        if (parts.Count == 0 || !string.Equals(parts[0], "serve", StringComparison.Ordinal))
        {
            return null;
        }

        string? store = null;
        string? listen = null;
        string? node = null;
        string? console = null;
        string? manifest = null;

        for (var i = 1; i < parts.Count; i++)
        {
            // An option's value is the next part, and an option at the very end has none. Each
            // is read once and the last occurrence wins, which is what the broker does with them.
            var value = i + 1 < parts.Count ? parts[i + 1] : null;

            switch (parts[i])
            {
                case "--db":
                    store = value;
                    i++;
                    break;

                case "--listen":
                    listen = value;
                    i++;
                    break;

                case "--node-config":
                    node = value;
                    i++;
                    break;

                case "--console":
                    console = value;
                    i++;
                    break;

                case "--config":
                    manifest = value;
                    i++;
                    break;

                case "--log":
                    i++;
                    break;

                default:
                    // --with-node, --with-console, and anything a later build adds: flags carry
                    // nothing this needs to read back.
                    break;
            }
        }

        return new InstalledEntry(store, listen, node, console, manifest);
    }

    /// <summary>Reads the entry this machine's supervisor holds, if there is one.</summary>
    /// <returns>The entry, or null when nothing is registered or it cannot be read.</returns>
    /// <remarks>
    /// Null rather than an exception for every way of not having one — no entry, a scheduler that
    /// will not answer, a unit file somebody edited into something else — because every caller
    /// treats "unknown" and "none" alike: they fall back to asking. A caller that needs to tell
    /// them apart is a caller that should be looking at <c>autostart status</c>.
    /// </remarks>
    public static InstalledEntry? Installed()
    {
        try
        {
            var plan = AutostartPlan.Describe("placeholder.db");

            return plan.Supervisor switch
            {
                Supervisor.ScheduledTask when OperatingSystem.IsWindows() =>
                    Parse(ScheduledTasks.Arguments(plan.Label)),

                Supervisor.SystemdUser => Parse(FromUnit(plan.UnitPath)),

                Supervisor.LaunchAgent => Parse(FromPlist(plan.UnitPath)),

                _ => null,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The arguments from a systemd unit's <c>ExecStart</c> line.</summary>
    /// <param name="unitPath">The unit file.</param>
    /// <returns>Everything after the executable, or null.</returns>
    private static string? FromUnit(string? unitPath)
    {
        if (unitPath is null || !File.Exists(unitPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(unitPath))
        {
            if (!line.StartsWith("ExecStart=", StringComparison.Ordinal))
            {
                continue;
            }

            var command = line["ExecStart=".Length..].Trim();
            var parts = AutostartPlan.Split(command);

            // The executable is the first part and is not an argument. Re-quoted on the way back
            // so that Parse sees exactly what AutostartPlan rendered.
            return string.Join(' ', parts.Skip(1).Select(AutostartPlan.Quote));
        }

        return null;
    }

    /// <summary>The arguments from a launch agent's <c>ProgramArguments</c> array.</summary>
    /// <param name="plistPath">The property list.</param>
    /// <returns>Everything after the executable, or null.</returns>
    /// <remarks>
    /// Read by the shape <see cref="AutostartPlan.LaunchAgentPlist"/> writes — one
    /// <c>&lt;string&gt;</c> per line inside the array — rather than through an XML parser,
    /// because the file is this tool's own and a general parser would be defending against a
    /// hand-edit no other reader of the file makes either.
    /// </remarks>
    private static string? FromPlist(string? plistPath)
    {
        if (plistPath is null || !File.Exists(plistPath))
        {
            return null;
        }

        var parts = new List<string>();
        var inside = false;

        foreach (var raw in File.ReadLines(plistPath))
        {
            var line = raw.Trim();

            if (line.StartsWith("<key>ProgramArguments</key>", StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (!inside)
            {
                continue;
            }

            if (line.StartsWith("</array>", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("<string>", StringComparison.Ordinal)
                && line.EndsWith("</string>", StringComparison.Ordinal))
            {
                parts.Add(System.Net.WebUtility.HtmlDecode(
                    line["<string>".Length..^"</string>".Length]));
            }
        }

        return parts.Count == 0
            ? null
            : string.Join(' ', parts.Skip(1).Select(AutostartPlan.Quote));
    }
}
