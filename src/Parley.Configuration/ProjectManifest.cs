using System.Security;
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
                    $"to keep its projects. Name one with --config, or set {PathVariable}.")
                : Path.Combine(home, ".config");
        }

        return Path.Combine(configuration, DirectoryName);
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
            return AsRoot(explicitPath);
        }

        var configured = variable(PathVariable);

        return string.IsNullOrWhiteSpace(configured) ? DefaultPath : AsRoot(configured);
    }

    /// <summary>
    /// Reads a configuration location given in either spelling, and answers with the root.
    /// </summary>
    /// <param name="given">What was named on the command line or in the environment.</param>
    /// <returns>The root directory it means.</returns>
    /// <remarks>
    /// <para>
    /// <b>The previous spelling named the file, and a machine that has not been reinstalled still uses
    /// it.</b> The owner's logon entry holds the old path verbatim; refusing it would stop that machine
    /// serving at its next start, which is the one moment nobody is watching. So a value whose final
    /// segment is the manifest's own file name is read as naming its parent.
    /// </para>
    /// <para>
    /// <b>Recognised by name, never by asking the filesystem.</b> A machine being set up has neither the
    /// file nor the directory yet, so a rule that tested for existence would behave differently before and
    /// after first run — which is a rule nobody can reason about. The name is decidable either way.
    /// </para>
    /// <para>
    /// Stated here and nowhere else, because this is the one place a configuration location is resolved.
    /// 018 paid for the alternative: the launch-key rule grew a second implementation that happened to
    /// agree for one build.
    /// </para>
    /// </remarks>
    public static string AsRoot(string given)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(given);

        var trimmed = given.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmed.Length == 0)
        {
            return given;
        }

        return string.Equals(Path.GetFileName(trimmed), FileName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? Path.GetDirectoryName(trimmed) is { Length: > 0 } parent ? parent : "."
            : trimmed;
    }

    /// <summary>
    /// Reads every project a machine has, from the contents of its projects directory.
    /// </summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="projects">The projects, empty when the machine has none.</param>
    /// <param name="problem">Why they could not be read, when this returns false.</param>
    /// <returns><see langword="true"/> when the machine had no projects or usable ones.</returns>
    /// <remarks>
    /// <para>
    /// <b>The directory listing is the set of projects, and there is no index.</b> So there is nothing for
    /// an index to disagree with, and the lost-add race a single file had is gone: creating a directory is
    /// one act.
    /// </para>
    /// <para>
    /// No projects returns true: a machine that has never had one is an ordinary machine, not a broken
    /// one. What cannot be read returns false and is reported <b>in terms of the project and its file</b>,
    /// never as the machine having no projects — a distinction this reader has always been held to, and
    /// one that matters more now that a bad folder sits beside good ones rather than inside one file.
    /// </para>
    /// </remarks>
    public static bool TryRead(
        string root,
        out IReadOnlyList<ProjectSettings> projects,
        out string? problem)
    {
        projects = [];
        problem = null;

        // **A caller that built this path itself rather than asking Locate.** The old spelling named the
        // manifest file, and a directory reader handed one would find no projects and say nothing — the
        // shape this repository keeps meeting, where a green-looking answer is given to a question nobody
        // asked. Named rather than absorbed, because Locate is where the compatibility rule lives and a
        // second implementation of it is what 018 paid for.
        if (string.Equals(
                Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                FileName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            problem =
                $"{root} names the manifest file this version replaced. The root is the directory " +
                "holding it; resolve a configuration location with Locate rather than composing one.";

            return false;
        }

        var directory = ProjectFolder.ProjectsIn(root);

        // A machine that has never had a project is an ordinary machine, not a broken one.
        if (!Directory.Exists(directory))
        {
            return true;
        }

        string[] folders;

        try
        {
            folders = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = $"{directory} could not be listed: {exception.Message}";
            return false;
        }

        var found = new List<ProjectSettings>();
        var problems = new List<string>();

        foreach (var folder in folders.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var entry = Path.GetFileName(folder);

            // **The folder's name is the project's**, so a folder that is not named like one is not a
            // project. Reported rather than passed over: it is somebody's mistake in the one directory
            // whose contents are the answer, and silence there is how a project goes missing.
            if (!ProjectName.TryCreate(entry, out var name))
            {
                problems.Add(
                    $"'{entry}' is not a project name, so it is not a project. Names are lowercase " +
                    $"letters, digits and hyphens, starting with a letter, at most " +
                    $"{ProjectName.MaxLength} characters");

                continue;
            }

            // A folder with no configuration in it is half-created, or somebody's scratch directory.
            // Silent on purpose — see ProjectFolder.TryRead.
            if (!ProjectFolder.TryRead(root, name, out var settings, out var fault))
            {
                if (fault is { Length: > 0 })
                {
                    problems.Add(fault);
                }

                continue;
            }

            found.Add(settings!);
        }

        // **What is left of Collisions is the one check the filesystem cannot make.** Two projects cannot
        // share a name or a store any more — a name is a directory entry and a store is derived from it —
        // so what survives is two projects claiming one endpoint, which only a hand edit can produce and
        // which is reported naming both rather than resolved in favour of either.
        problems.AddRange(Collisions(found));

        if (problems.Count > 0)
        {
            problem = $"{directory} is not usable yet:{Environment.NewLine}  - " +
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
    /// <para>
    /// <b>One check is left, and that is the point rather than an erosion.</b> This refused a shared name,
    /// a shared store and a shared endpoint. Two of those are no longer expressible: a project's name is
    /// its directory entry, so a name cannot repeat, and its store is derived from that name, so two
    /// projects cannot reach one file. Neither is checked because neither can happen — which is stronger
    /// than checking, and is the form of Principle V this layout offers.
    /// </para>
    /// <para>
    /// A shared endpoint survives because nothing about the layout prevents it: two projects on one port
    /// is a hand edit away, and only one broker can hold a port. Reported naming both projects rather
    /// than resolved in favour of either — whichever this picked would be somebody's deployment silently
    /// not starting.
    /// </para>
    /// <para>
    /// Applied when reading and when writing, so a hand-edited file meets the same rule <c>project add</c>
    /// applies. It cannot live in storage the way the constitution prefers: the thing being compared is
    /// which port each of N separate files names, and no one store sees them all.
    /// </para>
    /// <para>
    /// <b>The rows are named by project rather than by index.</b> An index meant a position in one array
    /// in one file; there is no array now, and a person told "projects[1]" would have nothing to count.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Collisions(IReadOnlyList<ProjectSettings> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var problems = new List<string>();
        var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            if (string.IsNullOrEmpty(project.Endpoint))
            {
                continue;
            }

            if (endpoints.TryGetValue(project.Endpoint, out var first))
            {
                problems.Add(
                    $"'{project.Name}' and '{first}' share an endpoint ('{project.Endpoint}'): "
                    + "only one broker can hold a port");
            }
            else
            {
                endpoints[project.Endpoint] = project.Name.Value;
            }
        }

        return problems;
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
            // **Inside the projects directory rather than at the root**, because FR-008 gives the root
            // exactly two files that are not a project. A file there is invisible to the reader, which
            // lists directories — so the lock cannot be mistaken for a project whose name is not one.
            Directory.CreateDirectory(ProjectFolder.ProjectsIn(path));
            held = Hold(Path.Combine(ProjectFolder.ProjectsIn(path), ".lock"), out refused);
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
    /// Writes a machine's projects, one folder each.
    /// </summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="projects">What the machine now has.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">The projects cannot coexist.</exception>
    /// <remarks>
    /// <para>
    /// <b>Most of what this used to be went with the single file it wrote.</b> That file named every
    /// store on the machine and had no second copy, so it was written to a sibling and renamed over
    /// itself — the failure worth designing against being a half-written file rather than a bad value,
    /// and a reader arriving mid-write seeing the old file or the new one and never half of either.
    /// </para>
    /// <para>
    /// A project is its own small file now, so a half-written one costs that project and not the
    /// machine, and the folder that holds it is created by one atomic act. The careful machinery is not
    /// deleted out of taste: it defended a shared file that no longer exists, and keeping it would be
    /// keeping the shape of a hazard that has gone.
    /// </para>
    /// </remarks>
    public static void Write(string root, IReadOnlyList<ProjectSettings> projects)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(projects);

        // Every rule reading applies, not just the ones about pairs. A writer that checked only
        // collisions would happily write a description containing a newline — valid JSON, refused by the
        // very next read — and a tool that writes what it cannot read back leaves a machine needing a
        // text editor to start.
        var faults = projects
            .SelectMany(p => p.Problems().Select(f => $"{p.Name}: {f}"))
            .Concat(Collisions(projects))
            .ToList();

        if (faults.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", faults));
        }

        var directory = ProjectFolder.ProjectsIn(root);

        _ = Directory.CreateDirectory(directory);

        foreach (var project in projects)
        {
            ProjectFolder.Write(root, project);
        }

        // **A project this write does not name loses its configuration and keeps its store.** That is
        // what forgetting a project is — SPEC.md's own wording is that it deletes nothing — and a folder
        // with no configuration in it is already defined as not a project, so the two agree by
        // construction rather than by a second rule. Destroying a store is `remove --destroy`, which is a
        // different act behind a different door, and a writer that deleted folders would make every
        // caller of this one capable of it.
        var keeping = projects
            .Select(project => project.Name.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var folder in Directory.GetDirectories(directory))
        {
            var entry = Path.GetFileName(folder);

            if (keeping.Contains(entry) || !ProjectName.TryCreate(entry, out var name))
            {
                continue;
            }

            var configuration = ProjectFolder.ConfigurationFor(root, name);

            if (File.Exists(configuration))
            {
                File.Delete(configuration);
            }
        }
    }
}
