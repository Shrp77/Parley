using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// The projects configured on this machine: where the file is, what it holds, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4.1 establishes projects as a deployment concept — each with its own store and its own
/// endpoint, the protocol unaware that any of it exists. What it did not settle is where the list
/// lives, and the absence had a cost: with no manifest, every path was supplied separately and the
/// node catalogue fell back to <i>the working directory</i>, so nothing was in a position to notice
/// when the three disagreed. Enrolling ten repositories from the wrong directory would have written
/// a new catalogue there while the one the node reads sat elsewhere — ten agents recorded as
/// consenting to run unattended, none of them startable, and a successful-looking run.
/// </para>
/// <para>
/// <b>An unreadable manifest is refused, never treated as empty.</b> This is the third time that
/// distinction has had to be made in this codebase: an unparseable node catalogue was once read as
/// "no agents" and overwritten, and a well-formed catalogue whose <c>agents</c> was the wrong JSON
/// type was cast away and replaced. Absent is the only shape that means "none". Anything else is a
/// file somebody wrote, in a state this does not understand, and writing over it destroys the only
/// copy.
/// </para>
/// </remarks>
public static class ProjectManifest
{
    /// <summary>The environment variable naming the manifest.</summary>
    public const string PathVariable = "PARLEY_CONFIG";

    /// <summary>The file name looked for in the per-user configuration directory.</summary>
    public const string FileName = "projects.json";

    /// <summary>The directory, under the platform's configuration location, that holds it.</summary>
    public const string DirectoryName = "Parley";

    private static readonly string[] Guidance =
    [
        "Projects on this machine. Each is an independent Parley deployment: its own store, its",
        "own endpoint, its own agents. Names may repeat across projects - two projects may each",
        "have an `orders` - because a broker's connection cannot see another's file.",
        "",
        "What may not repeat is a store, an endpoint or a node catalogue. Two brokers on one",
        "database corrupt it, two cannot hold one port, and one catalogue cannot be two projects'",
        "answer to which repositories may run with nobody watching.",
        "",
        "Managed with `parley project add|remove|list`. Editing by hand is fine; the same rules",
        "are applied on read, so a mistake is refused rather than half-applied - and whatever this",
        "version does not recognise is carried across untouched, so an older Parley editing a newer",
        "one's file does not eat it.",
        "",
        "This block is the exception: it is rewritten on every write, so notes of your own belong",
        "anywhere else in the file rather than here.",
    ];

    /// <summary>
    /// Where the manifest lives when nothing names one.
    /// </summary>
    /// <returns>An absolute path, which need not exist.</returns>
    /// <exception cref="InvalidOperationException">
    /// This machine has no per-user configuration directory to put one in.
    /// </exception>
    /// <remarks>
    /// One runtime call rather than a per-platform branch, because a hand-written path is correct
    /// only on the platform whoever wrote it was using. Resolves under <c>%APPDATA%</c> on Windows
    /// and the user's configuration directory elsewhere.
    /// </remarks>
    public static string DefaultPath => DefaultPathFrom(Environment.GetFolderPath);

    /// <summary>
    /// Where the manifest lives, given a way to locate the platform's special folders.
    /// </summary>
    /// <param name="folder">Resolves a special folder, as <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> does.</param>
    /// <returns>An absolute path, which need not exist.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="folder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Neither a configuration directory nor a home directory could be found.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> is documented to
    /// return the empty string when it cannot locate the folder, and on Unix it does.</b> Combined
    /// straight into a path that produced <c>Parley/projects.json</c> — <i>relative</i>, resolved
    /// against whatever directory the command was typed in. That is precisely the ambiguity this
    /// whole feature exists to remove, reintroduced one level above everything it fixed: two
    /// invocations from two directories would each find, create and write a different manifest,
    /// and every one of them would look like the manifest.
    /// </para>
    /// <para>
    /// So it falls back to the home directory and the XDG default beneath it, and refuses when
    /// there is no home either. Refusing is right rather than defensive: with no per-user
    /// location there is nowhere a machine-level manifest could go, and the honest answer is to
    /// say so and name <c>--config</c>, not to invent a path that happens to be writable.
    /// </para>
    /// <para>
    /// Takes the lookup as an argument so the empty case can be tested. It cannot be reached by
    /// setting a variable — the mapping is inside the runtime — and it was not reached by
    /// inspection either: the failure was found by running the suite on Linux, where the default
    /// was silently relative and a test asserting it was absolute is the only thing that said so.
    /// </para>
    /// </remarks>
    public static string DefaultPathFrom(Func<Environment.SpecialFolder, string> folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var configuration = folder(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrEmpty(configuration))
        {
            var home = folder(Environment.SpecialFolder.UserProfile);

            configuration = string.IsNullOrEmpty(home)
                ? throw new InvalidOperationException(
                    "This machine has no per-user configuration directory, so there is nowhere " +
                    $"to keep {FileName}. Name one with --config, or set {PathVariable}.")
                : Path.Combine(home, ".config");
        }

        return Path.Combine(configuration, DirectoryName, FileName);
    }

    /// <summary>
    /// Finds the manifest this invocation should use.
    /// </summary>
    /// <param name="explicitPath">A path given on the command line, or null.</param>
    /// <param name="variable">Reads an environment variable.</param>
    /// <returns>The path to use, which need not exist.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    public static string Locate(string? explicitPath, Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var configured = variable(PathVariable);

        return string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;
    }

    /// <summary>
    /// Reads the manifest at a path.
    /// </summary>
    /// <param name="path">Where to read from.</param>
    /// <param name="projects">The projects, empty when the file is absent.</param>
    /// <param name="problem">Why it could not be read, when this returns false.</param>
    /// <returns><see langword="true"/> when the manifest was absent or usable.</returns>
    /// <remarks>
    /// Absent returns true with no projects: a machine that has never had a manifest is an
    /// ordinary machine, not a broken one. Present-and-unreadable returns false, and the caller
    /// must refuse rather than continue — see the type's own remarks for why that is not
    /// pedantry.
    /// </remarks>
    public static bool TryRead(
        string path,
        out IReadOnlyList<ProjectSettings> projects,
        out string? problem)
    {
        projects = [];
        problem = null;

        if (!File.Exists(path))
        {
            return true;
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            problem = $"{path} exists but could not be read: {exception.Message}";
            return false;
        }

        if (root is not JsonObject document)
        {
            problem = $"{path} is not a JSON object.";
            return false;
        }

        var listed = document["projects"];

        if (listed is null)
        {
            return true;
        }

        if (listed is not JsonArray entries)
        {
            // Well-formed JSON of the wrong shape. Casting it away and writing an array over it
            // loses whatever it held just as completely as an unparseable file would.
            problem = $"{path} has a \"projects\" entry that is not a list.";
            return false;
        }

        var found = new List<ProjectSettings>();
        var problems = new List<string>();

        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not JsonObject entry)
            {
                problems.Add($"projects[{index}] is empty; delete the row or fill it in");
                continue;
            }

            var name = Text(entry, "name");

            if (!ProjectName.TryCreate(name, out var projectName))
            {
                problems.Add(
                    $"projects[{index}]: '{name}' is not a project name. Names are lowercase " +
                    $"letters, digits and hyphens, starting with a letter, at most " +
                    $"{ProjectName.MaxLength} characters");

                continue;
            }

            var settings = new ProjectSettings(
                projectName,
                Text(entry, "store") ?? string.Empty,
                Text(entry, "endpoint") ?? string.Empty,
                Text(entry, "node_config"),
                Text(entry, "description"));

            foreach (var fault in settings.Problems())
            {
                problems.Add($"projects[{index}] ({projectName}): {fault}");
            }

            foreach (var fault in PathProblems(settings))
            {
                problems.Add($"projects[{index}] ({projectName}): {fault}");
            }

            found.Add(settings);
        }

        problems.AddRange(Collisions(found));

        if (problems.Count > 0)
        {
            problem = $"{path} is not usable yet:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", problems);

            return false;
        }

        projects = found;
        return true;
    }

    /// <summary>
    /// Says what is wrong with a project's paths.
    /// </summary>
    /// <param name="project">The project to check.</param>
    /// <returns>One line per problem, empty when the paths are usable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The half of validation <see cref="ProjectSettings.Problems"/> deliberately leaves out.
    /// Whether a path is absolute is a question about a filesystem, and <c>Parley.Core</c> has no
    /// I/O — this assembly is on the machine those paths describe and may legitimately ask.
    /// </para>
    /// <para>
    /// One method rather than the same loop at both ends. It was written twice, once where the
    /// manifest is read and once where <c>project add</c> writes, with wording that had already
    /// begun to differ — which is how a rule comes to be enforced in one direction only, and the
    /// direction that lapses is always the one nobody tested.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> PathProblems(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var problems = new List<string>();

        foreach (var (field, value) in new[]
                 {
                     ("store", project.Store),
                     ("node_config", project.NodeConfig),
                 })
        {
            if (!string.IsNullOrEmpty(value) && !Path.IsPathRooted(value))
            {
                problems.Add(
                    $"{field} '{value}' is relative. A relative path resolves against whatever " +
                    "directory the caller happened to be in, which is the mistake a manifest " +
                    "exists to remove");
            }
        }

        return problems;
    }

    /// <summary>
    /// Finds projects that cannot coexist.
    /// </summary>
    /// <param name="projects">The projects to check.</param>
    /// <returns>One line per collision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <remarks>
    /// Applied when reading and when writing, so a hand-edited file meets the same rule
    /// <c>project add</c> applies. These invariants cannot live in storage the way the
    /// constitution prefers — the file names the store, so it cannot live inside it — and
    /// enforcing them at both ends is the strongest placement available.
    /// </remarks>
    public static IReadOnlyList<string> Collisions(IReadOnlyList<ProjectSettings> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var problems = new List<string>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var stores = new Dictionary<string, int>(PathComparer);
        var endpoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Catalogues, because agent names collide freely across projects and a node reads one
        // file. Two projects sharing a catalogue means `parley up` for either starts entries the
        // other consented to, under a name that legitimately means different repositories in each
        // — and the consent that says a repository may run with nobody watching stops being per
        // project, which is the one thing SPEC.md §4.1's isolation cannot supply from storage
        // because the catalogue is not in the store.
        var catalogues = new Dictionary<string, int>(PathComparer);

        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];

            Claim(names, project.Name.Value, index, "a name", "two projects cannot share one");

            if (!string.IsNullOrEmpty(project.Store))
            {
                Claim(
                    stores,
                    Canonical(project.Store),
                    index,
                    "a store",
                    "two brokers on one database file breaks the isolation SPEC.md 4.1 relies on");
            }

            if (!string.IsNullOrEmpty(project.Endpoint))
            {
                Claim(endpoints, project.Endpoint, index, "an endpoint", "only one broker can hold a port");
            }

            if (!string.IsNullOrEmpty(project.NodeConfig))
            {
                Claim(
                    catalogues,
                    Canonical(project.NodeConfig),
                    index,
                    "a node catalogue",
                    "consent to run unattended is per project because the catalogue is, and one " +
                    "file cannot be two projects' answer");
            }
        }

        return problems;

        // The article is part of the label rather than composed here: "share a endpoint" is the
        // kind of wrong that makes a person doubt the rest of the message.
        void Claim(Dictionary<string, int> seen, string value, int index, string field, string why)
        {
            if (seen.TryGetValue(value, out var first))
            {
                problems.Add(
                    $"projects[{index}] and projects[{first}] share {field} ('{value}'): {why}");
            }
            else
            {
                seen[value] = index;
            }
        }
    }

    /// <summary>
    /// The form of a path that decides whether two entries name the same file.
    /// </summary>
    /// <param name="path">The path as written.</param>
    /// <returns>Its resolved form, or the original if it cannot be resolved.</returns>
    /// <remarks>
    /// <para>
    /// String equality is not the question being asked. <c>C:\srv\orders\..\orders\parley.db</c>
    /// and <c>C:/srv/orders/parley.db</c> are one file, and comparing the text lets both into one
    /// manifest — where they become two brokers holding one SQLite database, which is not an
    /// error either of them reports. WAL mode needs coherent shared memory between every process
    /// touching a file, so the damage is to the store rather than to either broker, and it is the
    /// specific outcome this whole check exists to prevent.
    /// </para>
    /// <para>
    /// Syntactic only, and knowingly short of complete: symbolic links, junctions and two mounts
    /// of one filesystem still get past it. Resolving those means asking the filesystem about
    /// paths that may be on a machine this is not, and a check that is right about the spellings
    /// people actually write is worth more than none while the remainder stays out of reach.
    /// </para>
    /// </remarks>
    private static string Canonical(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or SecurityException)
        {
            // Unresolvable, which the path rules on read will have something to say about. Falling
            // back to the text keeps the duplicate check working rather than dropping the entry
            // out of it entirely.
            return path;
        }
    }

    /// <summary>
    /// How two paths are compared for being the same file.
    /// </summary>
    /// <remarks>
    /// Case-insensitive except on Linux, because that is what the filesystem does. A single
    /// choice is wrong on one platform or the other: ordinal everywhere would let one Windows
    /// store be claimed by two projects in two casings, and ignoring case everywhere would refuse
    /// a Linux manifest naming two files that genuinely differ only by it.
    /// </remarks>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Writes the manifest, preserving anything a newer tool may have added.
    /// </summary>
    /// <param name="path">Where to write.</param>
    /// <param name="projects">What to write.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The projects cannot coexist, or the file that is there could not be parsed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Validated whole before anything is written, and the existing file's byte-order mark and
    /// line endings are kept — editing one project should not produce a diff touching every line
    /// of a file somebody reads by hand.
    /// </para>
    /// <para>
    /// <b>Keys this version does not know about are carried across, at the top level and on each
    /// project.</b> This said so before it did it: it built a fresh document holding only
    /// <c>_readme</c> and <c>projects</c>, so a <c>schema_version</c> written by a newer Parley, or
    /// a field somebody added to one row by hand, was deleted by the next <c>project add</c> — a
    /// successful-looking run that quietly discarded what it did not recognise. An older tool must
    /// be able to edit a newer tool's file without eating it, because that is the only version
    /// pairing nobody can plan for.
    /// </para>
    /// <para>
    /// <c>_readme</c> is the exception and is rewritten every time. It is this tool's own
    /// explanation of the file, so it should say what this build believes rather than what some
    /// earlier one did.
    /// </para>
    /// </remarks>
    public static void Write(string path, IReadOnlyList<ProjectSettings> projects)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(projects);

        // Every rule reading applies, not just the ones about pairs. This checked collisions only,
        // so it would happily write a description containing a newline — valid JSON, refused by the
        // very next TryRead. A tool that writes a file it cannot read back leaves the machine with
        // an unreadable manifest, and the rule that protects a manifest the rest of the time is
        // exactly the rule that then makes it unusable until somebody edits it by hand.
        var faults = projects
            .SelectMany(p => p.Problems().Concat(PathProblems(p)).Select(f => $"{p.Name}: {f}"))
            .Concat(Collisions(projects))
            .ToList();

        if (faults.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", faults));
        }

        // Refused rather than replaced, for the same reason reading refuses it. Callers reach here
        // only after TryRead has succeeded, so this is the belt to that braces — but a Write that
        // started fresh over a file it could not understand would destroy the only copy, which is
        // the failure this whole type is arranged against.
        var document = Existing(path)
            ?? throw new InvalidDataException(
                $"{path} exists but could not be parsed, so writing over it would discard it.");

        // Indexed before `projects` is replaced, so each row can be rebuilt on top of the one it
        // replaces rather than in place of it.
        var previous = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        if (document["projects"] is JsonArray existingRows)
        {
            foreach (var row in existingRows)
            {
                if (row is JsonObject entry && Text(entry, "name") is { Length: > 0 } named)
                {
                    previous[named] = (JsonObject)entry.DeepClone();
                }
            }
        }

        document["_readme"] = new JsonArray([.. Guidance.Select(line => JsonValue.Create(line))]);
        document["projects"] = new JsonArray([.. projects.Select(p => Describe(p, previous))]);

        var existed = File.Exists(path);
        var mark = existed && HasByteOrderMark(path);
        var crlf = existed && File.ReadAllText(path).Contains("\r\n", StringComparison.Ordinal);
        var json = document.ToJsonString(SerialisationOptions.Indented) + "\n";

        if (crlf)
        {
            json = json.ReplaceLineEndings("\r\n");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        // Written beside the manifest and moved over it, rather than written in place. This file
        // names every store on the machine and there is no second copy of it, so the failure worth
        // designing against is not a bad value but a half-written file: File.WriteAllText truncates
        // first, and a process that stops between the truncate and the write leaves nothing. Every
        // reader then refuses it — correctly, and permanently, because refusing to overwrite an
        // unreadable manifest is the rule that protects it the rest of the time.
        //
        // The temporary file is a sibling so the move stays within one volume, which is what makes
        // it a rename rather than a copy. A reader arriving mid-write sees the old file or the new
        // one; it cannot see half of either. That matters beyond crashes: an MCP server resolving
        // its project starts whenever its client decides to, including while `project add` runs.
        var pending = path + ".writing";

        File.WriteAllText(
            pending,
            json,
            mark ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(false));

        try
        {
            File.Move(pending, path, overwrite: true);
        }
        catch
        {
            // Never left behind to be mistaken for a manifest, or to make the next write fail.
            try
            {
                File.Delete(pending);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The move already failed; the caller is about to hear about that.
            }

            throw;
        }
    }

    /// <summary>Renders one project, keeping whatever its previous row carried.</summary>
    /// <param name="project">The values this build knows about.</param>
    /// <param name="previous">The rows being replaced, by name.</param>
    /// <returns>The row to write.</returns>
    /// <remarks>
    /// Built on top of the row it replaces rather than beside it. Assigning to an existing key
    /// leaves it where it was, so a hand-edited file keeps its own field order and a rewrite stays
    /// a small diff; an unknown field survives because nothing here removes it. The two optional
    /// fields <i>are</i> removed when absent, because for those the caller is authoritative —
    /// otherwise clearing a project's node catalogue would leave the old one behind.
    /// </remarks>
    private static JsonObject Describe(
        ProjectSettings project, IReadOnlyDictionary<string, JsonObject> previous)
    {
        var entry = previous.TryGetValue(project.Name.Value, out var kept)
            ? (JsonObject)kept.DeepClone()
            : [];

        entry["name"] = project.Name.Value;
        entry["store"] = project.Store;
        entry["endpoint"] = project.Endpoint;

        Set(entry, "node_config", project.NodeConfig);
        Set(entry, "description", project.Description);

        return entry;

        static void Set(JsonObject entry, string key, string? value)
        {
            if (value is { Length: > 0 })
            {
                entry[key] = value;
            }
            else
            {
                entry.Remove(key);
            }
        }
    }

    /// <summary>Reads the document already at a path, for a write that must not lose it.</summary>
    /// <param name="path">Where to look.</param>
    /// <returns>The document, an empty one when the file is absent, or null when it cannot be parsed.</returns>
    private static JsonObject? Existing(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject entry, string key) =>
        entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool HasByteOrderMark(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];

            return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
                && head is [0xEF, 0xBB, 0xBF];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
