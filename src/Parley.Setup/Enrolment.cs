using System.Text;
using System.Text.Json.Nodes;
using Parley.Configuration;

namespace Parley.Setup;

/// <summary>
/// Making one repository an agent of a project, and unmaking it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One writer, because two would disagree.</b> The files that make a repository an agent are
/// its launch configuration and its line in a project's catalogue, and until this existed only
/// <c>parley enroll apply</c> wrote them — driven by a manifest, in bulk. The terminal adds one
/// repository at a time and has no manifest, and reimplementing the writes there would have been
/// the second writer FR-008 forbids. So the writing lives here and both callers use it.
/// </para>
/// <para>
/// <b><see cref="WithdrawAsync"/> is written beside <see cref="AddAsync"/> so the two cannot drift.</b>
/// A withdrawal that leaves a launch configuration behind produces a repository that still
/// announces itself as an agent of a project it was removed from, which is worse than either
/// state on its own. Keeping the inverse next to the original is the cheapest way to notice when
/// one of them grows a step.
/// </para>
/// <para>
/// <b>The instructions file is never rewritten, and withdrawing never touches it at all.</b>
/// Adding appends the peers frame when the file has no <c>## Peers</c> section, and does nothing
/// to it when it has one — the frame belongs to the project, the roster block inside it is written
/// by <c>/parley:write-peers</c>, and nothing here can tell a line the project added from a line a
/// tool did, so a section that exists is left exactly as it is. Withdrawing declines even the
/// append, and says so rather than leaving somebody to wonder.
/// </para>
/// <para>
/// <b>That paragraph used to say the file was never touched by either half, which was not true of
/// adding.</b> Recorded rather than quietly corrected, because a comment claiming a program does
/// not write a file in somebody's repository is the kind of wrong that is believed: it is read by
/// whoever is deciding whether this surface is safe to point at a checkout.
/// </para>
/// </remarks>
public static class Enrolment
{
    /// <summary>The launch configuration's name, in every repository.</summary>
    public const string LaunchFile = ".mcp.json";

    /// <summary>Text without a byte-order mark.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Text with one, for a file that already had one.</summary>
    private static readonly UTF8Encoding Utf8WithMark = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The launch configuration of a repository.</summary>
    /// <param name="workspace">The repository directory.</param>
    /// <returns>The path, whether or not anything is there.</returns>
    public static string LaunchPath(string workspace) => Path.Combine(workspace, LaunchFile);

    /// <summary>
    /// Writes what a plan decided, and says which files changed.
    /// </summary>
    /// <param name="plan">What <c>EnrollmentPlan.Describe</c> decided.</param>
    /// <param name="workspace">The repository the plan is for.</param>
    /// <param name="instructionsPath">Where its standing instructions live.</param>
    /// <param name="cancellationToken">Stops the writes.</param>
    /// <returns>Every path written, in the order written.</returns>
    /// <remarks>
    /// The byte-order mark a file already had is kept. <c>ReadAllText</c> strips one and
    /// <c>WriteAllText</c> does not put it back, so appending a block to a file that had one
    /// rewrites every byte of it — a whole-file diff for a twelve-line addition, in the file a
    /// project reads on every turn.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> WriteAsync(
        RepositoryPlan plan,
        string workspace,
        string instructionsPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var written = new List<string>();

        if (plan.LaunchConfiguration is { } launch)
        {
            var path = LaunchPath(workspace);

            await File.WriteAllTextAsync(path, launch, cancellationToken).ConfigureAwait(false);
            written.Add(path);
        }

        if (plan.Instructions is { } instructions)
        {
            await File.WriteAllTextAsync(
                instructionsPath,
                instructions,
                HadByteOrderMark(instructionsPath) ? Utf8WithMark : Utf8,
                cancellationToken).ConfigureAwait(false);

            written.Add(instructionsPath);
        }

        return written;
    }

    /// <summary>Puts an agent in a catalogue, so a node may start it.</summary>
    /// <param name="catalogue">The catalogue's <c>agents</c> object.</param>
    /// <param name="agent">The role the node would start.</param>
    /// <param name="workspace">Where it would be started.</param>
    public static void List(JsonObject catalogue, string agent, string workspace)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        catalogue[agent] = new JsonObject
        {
            ["command"] = "claude",
            ["workspace"] = workspace,
        };
    }

    /// <summary>Which agent a launch configuration's parley entry says it starts, or null.</summary>
    /// <param name="entry">The <c>mcpServers.parley</c> node, whatever shape it is in.</param>
    /// <returns>The name, or null when the entry names nobody.</returns>
    /// <remarks>
    /// Null and "somebody else" are deliberately different answers. An entry that names nobody
    /// cannot be another agent's, because nothing identifies it as anybody's — every entry this
    /// program writes carries the variable — so a withdrawal proceeds on one and declines on the
    /// other. Reading the name back is the only thing here that can tell them apart: SPEC.md §16
    /// makes the launch environment the whole of who an agent is.
    /// </remarks>
    public static string? NamedBy(JsonNode? entry) =>
        entry?["env"]?[EnrollmentPlan.AgentVariable] is JsonValue value
        && value.TryGetValue<string>(out var name)
        && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    /// <summary>Takes an agent out of a catalogue.</summary>
    /// <param name="catalogue">The catalogue's <c>agents</c> object.</param>
    /// <param name="agent">The role to remove.</param>
    /// <returns>Whether it was there to remove.</returns>
    public static bool Unlist(JsonObject catalogue, string agent)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        return catalogue.Remove(agent);
    }

    /// <summary>
    /// Makes one repository an agent of a project.
    /// </summary>
    /// <param name="entry">The repository, its role, and whether it may run unattended.</param>
    /// <param name="project">The project it becomes an agent of.</param>
    /// <param name="catalogueFile">The project's catalogue.</param>
    /// <param name="instructionsPath">Its standing instructions, read but never written by this.</param>
    /// <param name="cancellationToken">Stops the reads and writes.</param>
    /// <returns>What was decided and what was written.</returns>
    /// <exception cref="InvalidDataException">
    /// A file is in a shape this cannot merge into, or the repository already belongs somewhere.
    /// </exception>
    /// <remarks>
    /// The catalogue is read again here rather than taken from the caller, because the surface
    /// draws from it continuously and somebody may have edited it by hand between the draw and
    /// the confirmation. Reading it at the moment of writing is what makes the write true.
    /// </remarks>
    public static async Task<Outcome> AddAsync(
        EnrollmentEntry entry,
        string project,
        string catalogueFile,
        string instructionsPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueFile);

        var catalogue = await ReadCatalogueAsync(catalogueFile, cancellationToken).ConfigureAwait(false);
        var agents = AgentsOf(catalogue, catalogueFile);
        var launchPath = LaunchPath(entry.Workspace);

        var plan = EnrollmentPlan.Describe(
            entry,
            File.Exists(launchPath)
                ? await File.ReadAllTextAsync(launchPath, cancellationToken).ConfigureAwait(false)
                : null,
            File.Exists(instructionsPath)
                ? await File.ReadAllTextAsync(instructionsPath, cancellationToken).ConfigureAwait(false)
                : null,
            agents.ContainsKey(entry.Agent),
            project);

        var written = new List<string>(
            await WriteAsync(plan, entry.Workspace, instructionsPath, cancellationToken).ConfigureAwait(false));

        if (plan.AddsNodeEntry)
        {
            List(agents, entry.Agent, entry.Workspace);
            await SaveCatalogueAsync(catalogueFile, catalogue, cancellationToken).ConfigureAwait(false);
            written.Add(catalogueFile);
        }

        return new Outcome(plan.Notes, written);
    }

    /// <summary>
    /// Unmakes it: the launch configuration's parley entry and the catalogue line, and nothing else.
    /// </summary>
    /// <param name="workspace">The repository directory.</param>
    /// <param name="agent">The role being withdrawn.</param>
    /// <param name="catalogueFile">The project's catalogue, or null when the project has none.</param>
    /// <param name="cancellationToken">Stops the reads and writes.</param>
    /// <returns>What was removed, and what was already gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>The launch configuration is edited rather than deleted.</b> A repository may run other
    /// MCP servers, and a withdrawal that removed the file would take them with it. A file left
    /// holding an empty <c>mcpServers</c> is also the smaller diff and is what a person reverting
    /// this by hand would produce.
    /// </para>
    /// <para>
    /// Everything it cannot parse it leaves alone and reports. A withdrawal is somebody deciding
    /// an agent should stop being reachable; rewriting a file it does not understand in the
    /// course of that is a second, unasked-for change.
    /// </para>
    /// </remarks>
    public static async Task<Outcome> WithdrawAsync(
        string workspace,
        string agent,
        string? catalogueFile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        var notes = new List<string>();
        var written = new List<string>();
        var launchPath = LaunchPath(workspace);

        if (!File.Exists(launchPath))
        {
            notes.Add($"{launchPath}: already absent");
        }
        else
        {
            var text = await File.ReadAllTextAsync(launchPath, cancellationToken).ConfigureAwait(false);
            JsonNode? parsed;

            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = null;
                notes.Add($"{launchPath}: left alone, it is not readable JSON");
            }

            if (parsed is JsonObject root)
            {
                if (root["mcpServers"] is JsonObject servers)
                {
                    var named = NamedBy(servers[LaunchKey.Unnamed]);

                    if (named is not null && !string.Equals(named, agent, StringComparison.Ordinal))
                    {
                        // Somebody else's. The workspace comes from the registry, which is what the
                        // agent said about itself and may be some time old, so a repository
                        // re-enrolled under a second name has two rows pointing at one file. Taking
                        // this one would unhook an agent nobody named, and would report success.
                        notes.Add(
                            $"{launchPath}: left alone, its parley entry is '{named}' rather than "
                            + $"'{agent}'");
                    }
                    else if (servers.Remove(LaunchKey.Unnamed))
                    {
                        await File.WriteAllTextAsync(
                            launchPath,
                            root.ToJsonString(SerialisationOptions.Indented) + "\n",
                            cancellationToken).ConfigureAwait(false);

                        written.Add(launchPath);
                        notes.Add($"{launchPath}: parley entry removed, {servers.Count} other server(s) left");
                    }
                    else
                    {
                        notes.Add($"{launchPath}: no parley entry to remove");
                    }
                }
                else
                {
                    notes.Add($"{launchPath}: left alone, it names no servers this understands");
                }
            }
            else if (parsed is not null)
            {
                notes.Add($"{launchPath}: left alone, it is not a JSON object");
            }
        }

        if (catalogueFile is null)
        {
            notes.Add("no catalogue: this project has none");
        }
        else if (!File.Exists(catalogueFile))
        {
            notes.Add($"{catalogueFile}: already absent");
        }
        else
        {
            var catalogue = await ReadCatalogueAsync(catalogueFile, cancellationToken).ConfigureAwait(false);

            if (catalogue["agents"] is JsonObject agents)
            {
                if (Unlist(agents, agent))
                {
                    await SaveCatalogueAsync(catalogueFile, catalogue, cancellationToken).ConfigureAwait(false);
                    written.Add(catalogueFile);
                    notes.Add($"{catalogueFile}: entry for '{agent}' removed");
                }
                else
                {
                    notes.Add($"{catalogueFile}: no entry for '{agent}'");
                }
            }
            else
            {
                notes.Add($"{catalogueFile}: left alone, its agents are not an object");
            }
        }

        // Named rather than silently skipped. Somebody withdrawing an agent is entitled to know
        // that the file describing it to its peers keeps whatever it says about them, because
        // that is the one thing here that outlives the withdrawal.
        notes.Add("standing instructions: left untouched");

        return new Outcome(notes, written);
    }

    /// <summary>Reads a catalogue, refusing shapes that writing over would destroy.</summary>
    /// <param name="path">The catalogue file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The catalogue, or an empty one when the file is not there.</returns>
    /// <exception cref="InvalidDataException">It exists and is not readable JSON.</exception>
    public static async Task<JsonObject> ReadCatalogueAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            if (JsonNode.Parse(text) is JsonObject catalogue)
            {
                return catalogue;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"{path} is not readable JSON: {ex.Message}", ex);
        }

        throw new InvalidDataException($"{path} is not a JSON object");
    }

    /// <summary>The catalogue's agents, refusing a shape that writing over would destroy.</summary>
    /// <param name="catalogue">The catalogue.</param>
    /// <param name="path">Where it came from, for the refusal.</param>
    /// <returns>The agents object, created and attached when absent.</returns>
    /// <exception cref="InvalidDataException"><c>agents</c> is present and is not an object.</exception>
    /// <remarks>
    /// Absent is the only shape that means "empty". An <c>agents</c> that is an array or a string
    /// is somebody's file in a state this does not understand, and casting it away and writing an
    /// object over it loses what it held as completely as an unparseable file would. That exact
    /// bug was found three times in this feature's first form, which is why it is refused here
    /// rather than tidied up.
    /// </remarks>
    public static JsonObject AgentsOf(JsonObject catalogue, string path)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        if (catalogue["agents"] is not null and not JsonObject)
        {
            throw new InvalidDataException(
                $"{path} has an \"agents\" entry that is not an object. Fix or move it first: " +
                "writing here would replace it.");
        }

        if (catalogue["agents"] is not JsonObject agents)
        {
            agents = [];
            catalogue["agents"] = agents;
        }

        return agents;
    }

    /// <summary>Writes a catalogue back.</summary>
    /// <param name="path">Where it goes.</param>
    /// <param name="catalogue">What to write.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The write.</returns>
    public static async Task SaveCatalogueAsync(
        string path,
        JsonObject catalogue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            path,
            catalogue.ToJsonString(SerialisationOptions.Indented) + "\n",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a file starts with a byte-order mark.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether it does; false when it cannot be read.</returns>
    private static bool HadByteOrderMark(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            Span<byte> first = stackalloc byte[3];

            return stream.ReadAtLeast(first, 3, throwOnEndOfStream: false) == 3
                && first[0] == 0xEF
                && first[1] == 0xBB
                && first[2] == 0xBF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What an add or a withdrawal decided, and what it wrote.</summary>
    /// <param name="Notes">One human-readable line per decision, in the order decided.</param>
    /// <param name="Written">Every file whose contents changed.</param>
    /// <remarks>
    /// The two are separate because a note is not evidence. Every step says what it did, including
    /// the ones that did nothing, and only the paths in <paramref name="Written"/> actually
    /// changed on disk — which is what a surface reporting "exactly what was removed" needs.
    /// </remarks>
    public sealed record Outcome(IReadOnlyList<string> Notes, IReadOnlyList<string> Written);
}
