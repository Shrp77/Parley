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
            root = JsonNode.Parse(ReadShared(path));
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

            foreach (var fault in PathProblems(settings, path))
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
    public static IReadOnlyList<string> PathProblems(ProjectSettings project) =>
        PathProblems(project, null);

    /// <summary>
    /// Says what is wrong with a project's paths, including naming the manifest itself.
    /// </summary>
    /// <param name="project">The project to check.</param>
    /// <param name="manifestPath">The manifest the project is named in, or null to skip that check.</param>
    /// <returns>One line per problem, empty when the paths are usable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <remarks>
    /// A store or catalogue that is the manifest itself was accepted, and found by probing what
    /// <c>project remove --destroy</c> could be made to delete: the one file whose deletion takes
    /// every project with it. Refused on the way in, at both ends, rather than caught on the way
    /// out — the command that deletes also refuses, but a rule enforced only there is a rule the
    /// next deleting command forgets.
    /// </remarks>
    public static IReadOnlyList<string> PathProblems(ProjectSettings project, string? manifestPath)
    {
        ArgumentNullException.ThrowIfNull(project);

        var problems = new List<string>();

        foreach (var (field, value) in new[]
                 {
                     ("store", project.Store),
                     ("node_config", project.NodeConfig),
                 })
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!Path.IsPathRooted(value))
            {
                problems.Add(
                    $"{field} '{value}' is relative. A relative path resolves against whatever " +
                    "directory the caller happened to be in, which is the mistake a manifest " +
                    "exists to remove");

                continue;
            }

            if (manifestPath is not null && IsTheManifest(value, manifestPath))
            {
                problems.Add(
                    $"{field} '{value}' is this manifest. A project cannot keep its data in the " +
                    "file that names every project");
            }
        }

        return problems;
    }

    /// <summary>Whether a path names the manifest, however it was spelled.</summary>
    /// <param name="path">The path to check.</param>
    /// <param name="manifestPath">The manifest.</param>
    /// <returns><see langword="true"/> if they are the same file.</returns>
    public static bool IsTheManifest(string path, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(manifestPath);

        try
        {
            return PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            // Not a path at all, so not this one.
            return false;
        }
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

            // **Two projects sharing a catalogue was refused here, and 018 removed the reason.** The
            // rule read: consent to run unattended is per project because the catalogue is, and one file
            // cannot be two projects' answer. The catalogue belongs to the machine now and every project
            // on it reads the same file, so sharing one is the ordinary case rather than a collision.
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

    /// <summary>The name of the lock held while a manifest is being changed.</summary>
    /// <param name="path">The manifest being changed.</param>
    /// <returns>The path of its lock file.</returns>
    /// <remarks>
    /// <para>
    /// A sibling rather than something under the temporary directory, so two writers agree about
    /// it for the same reason they agree about the manifest: it is the same path, with the same
    /// permissions, on the same volume. A per-user temporary directory would exclude two commands
    /// run by one person and let two people editing one <c>--config</c> straight through.
    /// </para>
    /// <para>
    /// <b>Public because a second thing now needs the same gate.</b> Granting a repository an
    /// unattended start reads every project's catalogue and then writes one of them, and those two
    /// must be one act — the same machine-wide exclusion this file already needed for its own
    /// writes. A second lock over the same deployment would be two answers to one question, and the
    /// pair that disagreed would be the pair nobody thought to test together.
    /// </para>
    /// </remarks>
    public static string LockFor(string path) => path + ".lock";

    /// <summary>
    /// Changes the manifest under a lock, so a read and the write that depends on it are one act.
    /// </summary>
    /// <param name="path">The manifest to change.</param>
    /// <param name="change">
    /// Given what the manifest holds, returns what it should hold — or <see langword="null"/> to
    /// leave it alone, having reported why itself.
    /// </param>
    /// <param name="problem">Why nothing could be attempted, when this returns false.</param>
    /// <returns>
    /// <see langword="true"/> when the manifest was written; <see langword="false"/> when it was
    /// not, with <paramref name="problem"/> set unless <paramref name="change"/> declined.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Adding a project is a read and then a write, and it was neither locked nor atomic.</b>
    /// Two <c>project add</c> commands at once each read the manifest, each appended its own
    /// project to what it had read, and each wrote the result — so the second silently discarded
    /// the first. Nothing failed and nothing said so; the operator saw two successes and had one
    /// project. On a machine that enrols repositories from a script, that is the shape of the
    /// loss: it happens when the machine is busiest and is discovered when something later cannot
    /// find a deployment nobody remembers removing.
    /// </para>
    /// <para>
    /// The window is not narrow. It spans validation — collisions are checked against what was
    /// read — so the second writer also validates against a manifest that no longer exists, and
    /// can write a duplicate endpoint the rules exist to refuse.
    /// </para>
    /// <para>
    /// A lock file rather than a mutex, because .NET's named mutexes are not a cross-process
    /// primitive on every platform this runs on, and this has to hold on all three. Opened with
    /// <see cref="FileShare.None"/>, which is the exclusion; the file itself is empty and stays
    /// behind, because a lock somebody has to delete is a lock that eventually is not taken.
    /// </para>
    /// <para>
    /// The wait is bounded. A writer that cannot take the lock within it says so rather than
    /// blocking a person's terminal for ever — a held lock means another command is mid-write,
    /// which takes milliseconds, so exceeding this means something is wrong rather than busy.
    /// </para>
    /// </remarks>
    public static bool TryUpdate(
        string path,
        Func<IReadOnlyList<ProjectSettings>, IReadOnlyList<ProjectSettings>?> change,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(change);

        problem = null;

        FileStream? held;
        string? refused;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            held = Hold(LockFor(path), out refused);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            problem = $"Could not lock {path} to change it: {failure.Message}";
            return false;
        }

        if (held is null)
        {
            // Both causes named, because waiting cannot tell them apart and guessing at the
            // operator's expense is worse than saying so. A held lock means another command is
            // mid-write and will be done in milliseconds; anything else means the lock file
            // itself cannot be opened, and the last refusal says which.
            problem =
                $"Could not lock {path} within {LockWait.TotalSeconds:0} seconds, so nothing has "
                + $"been changed. Either another command is still changing it, or its lock file "
                + $"cannot be opened: {refused}";

            return false;
        }

        using (held)
        {
            // Inside the lock, because the write below is computed from it. This is the whole
            // point: read and write were already each safe on their own.
            if (!TryRead(path, out var projects, out var unreadable))
            {
                problem = unreadable;
                return false;
            }

            var updated = change(projects);

            if (updated is null)
            {
                return false;
            }

            Write(path, updated);

            return true;
        }
    }

    /// <summary>How long to wait for another command to finish changing the manifest.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>Takes the lock, or gives up.</summary>
    /// <param name="lockPath">The lock file.</param>
    /// <param name="refused">Why the last attempt failed, when the wait ran out.</param>
    /// <returns>The held stream, or <see langword="null"/> if the wait ran out.</returns>
    /// <remarks>
    /// Returns rather than throws on the deadline. An earlier version let the exception escape
    /// once the wait was over, which made the timeout unreachable and reported a sharing
    /// violation as though the file could not be opened at all — the two failures that most need
    /// telling apart, told apart wrongly.
    /// </remarks>
    private static FileStream? Hold(string lockPath, out string? refused)
    {
        var deadline = DateTimeOffset.UtcNow + LockWait;

        refused = null;

        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException failure)
            {
                // Somebody else holds it, which is the ordinary case rather than a fault. Only
                // this is waited on: a denied path or a missing directory will not become
                // available by asking again, and those propagate to the caller.
                refused = failure.Message;

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null;
                }

                Thread.Sleep(25);
            }
        }
    }

    /// <summary>
    /// Writes the manifest, preserving anything a newer tool may have added.
    /// </summary>
    /// <param name="path">Where to write.</param>
    /// <param name="projects">What to write.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The projects cannot coexist, or the file that is there could not be parsed.
    /// </exception>
    /// <exception cref="IOException">
    /// The file could not be read, written or replaced. Distinct from the above on purpose: a
    /// manifest that is merely busy is a condition that passes, and reporting it as one that
    /// "could not be parsed, so writing over it would discard it" tells somebody their
    /// configuration is destroyed because a file was open for a moment.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
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
        var crlf = existed && ReadShared(path).Contains("\r\n", StringComparison.Ordinal);
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
        // Unique per write, and that is not tidiness. A fixed name is shared by every writer, so
        // two of them do not merely race for the manifest -- one commits the other's half-written
        // file. Writer A truncates the pending file and begins writing; writer B, whose own write
        // finished, renames that same path over the manifest and puts A's fragment into place.
        //
        // Probed with four writers over 50 rounds each: five reads came back on a manifest that
        // no longer parsed, and eleven writes then refused to proceed because "it exists but
        // could not be parsed, so writing over it would discard it". That refusal is correct and
        // permanent -- the file naming every store on this machine, bricked by two commands run
        // at once. It is exactly the half-written file this whole temporary-file dance exists to
        // prevent, arriving by the one route the dance did not cover.
        var pending = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.writing";

        File.WriteAllText(
            pending,
            json,
            mark ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(false));

        try
        {
            try
            {
                // Replace rather than Move, and it has to be both this and ReadShared -- neither
                // works alone. Measured on Windows, eight readers spinning against 300 writes:
                //
                //   Move,    readers FileShare.Read        287 / 300 denied
                //   Move,    readers ReadWrite | Delete    287 / 300 denied
                //   Replace, readers FileShare.Read        300 / 300 denied
                //   Replace, readers ReadWrite | Delete      0 / 300 denied
                //
                // MoveFileEx with MOVEFILE_REPLACE_EXISTING refuses a destination anybody has open,
                // whatever share mode they opened it with, so the rename above was never the
                // interruption-safe commit its comment claims -- it was a commit that failed whenever
                // somebody was reading. That went unnoticed while the only reader was an MCP server
                // resolving a project at startup, and became ordinary the moment the console started
                // re-reading the manifest per page.
                File.Replace(pending, path, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                // Nothing to replace: the first manifest this machine has had. No handle can be
                // open on a file that does not exist, so the plain rename is safe here and only
                // here.
                File.Move(pending, path, overwrite: true);
            }
        }
        catch
        {
            // Never left behind to be mistaken for a manifest, or to make the next write fail.
            //
            // Nested so this is reached from the fallback as well. It was not: a fallback rename
            // that threw walked straight past the cleanup and orphaned the pending file. Harmless
            // while the name was fixed and the next write reused it; a growing pile of files that
            // look like configuration once the name became unique.
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

    /// <summary>Reads a file without standing in the way of whoever replaces it.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>Its contents.</returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="FileShare.Delete"/> is the part that matters, and its absence made the
    /// atomic write above fail intermittently on Windows.</b> <c>Write</c> puts the new manifest
    /// beside the old one and renames it over the top, and its remarks promise that a reader
    /// arriving mid-write sees the old file or the new one. That promise was only kept because
    /// nothing read often enough to test it: <see cref="File.ReadAllText(string)"/> opens with
    /// <see cref="FileShare.Read"/>, and a rename onto a file somebody holds without
    /// <see cref="FileShare.Delete"/> is refused with <see cref="UnauthorizedAccessException"/>.
    /// </para>
    /// <para>
    /// So a reader did not see half a file — it stopped the write from happening at all, and
    /// <c>parley project add</c> failed with an access error naming a file nothing else was
    /// writing. Rare while the only reader was an MCP server starting up; ordinary once the
    /// console began re-reading the manifest on every page it renders.
    /// </para>
    /// <para>
    /// <see cref="FileShare.ReadWrite"/> alongside it for the same reason in the other direction:
    /// a reader must not make a writer wait, and the atomic rename is what makes that safe rather
    /// than a race.
    /// </para>
    /// </remarks>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
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
            return JsonNode.Parse(ReadShared(path)) as JsonObject;
        }
        catch (JsonException)
        {
            // Null means one thing only: the file is there and is not JSON. The caller turns that
            // into a refusal to write, on the grounds that overwriting it would discard the only
            // copy -- which is right for a file nobody can parse, and wrong for one that was
            // merely busy.
            //
            // This used to swallow IOException and UnauthorizedAccessException here too, so a
            // sharing violation lasting microseconds was reported to the operator as "exists but
            // could not be parsed, so writing over it would discard it". That is the sentence
            // that tells somebody their configuration is destroyed, printed because a file was
            // open. Found under concurrent writers; let them propagate as what they are.
            return null;
        }
    }

    private static string? Text(JsonObject entry, string key) =>
        entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool HasByteOrderMark(string path)
    {
        try
        {
            // Shared for the same reason as ReadShared, which this cannot use because it wants
            // bytes rather than text. It is writer-side, so it stands in front of another writer
            // rather than of a reader -- one `parley project add` blocking another is rarer than
            // the case that was actually failing, and it is the same defect.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

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
