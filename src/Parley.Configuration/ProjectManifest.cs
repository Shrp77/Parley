using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// The projects configured on this machine: where the root is, what it holds, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4.1 makes each project a store and an endpoint of its own, the protocol unaware of any
/// of it. A machine has one root, named once and never inferred from the working directory, so
/// every command and the node agree on where the projects are.
/// </para>
/// <para>
/// <b>An unreadable project is refused, never treated as empty.</b> Absent is the only shape that
/// means "none". Anything else is a file somebody wrote, in a state this does not understand, and
/// writing over it destroys the only copy.
/// </para>
/// </remarks>
public static class ProjectManifest
{
    /// <summary>The environment variable naming the machine's root.</summary>
    public const string PathVariable = "PARLEY_CONFIG";

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
    /// <b><see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns the empty
    /// string when it cannot locate the folder, and on Unix it does.</b> Combined straight into a
    /// path that would be <i>relative</i>, resolved against whatever directory the command was typed
    /// in, so two invocations from two directories would each use a different root.
    /// </para>
    /// <para>
    /// So it falls back to the home directory and the XDG default beneath it, and refuses when
    /// there is no home either: with no per-user location there is nowhere a machine-level root
    /// could go, and the honest answer is to say so and name <c>--config</c>.
    /// </para>
    /// <para>
    /// Takes the lookup as an argument so the empty case can be tested; it cannot be reached by
    /// setting a variable.
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
    /// Finds the root this invocation should use.
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
    /// Reads every project a machine has, from the contents of its projects directory.
    /// </summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="projects">The projects, empty when the machine has none.</param>
    /// <param name="problem">
    /// Why the projects could not be read, when this returns false; and what was left out, when it returns true having
    /// skipped a folder that is not a usable project. Null when there is nothing to say.
    /// </param>
    /// <returns><see langword="true"/> unless the projects directory itself could not be listed.</returns>
    /// <remarks>
    /// <para>
    /// <b>The directory listing is the set of projects, and there is no index.</b> So there is nothing for
    /// an index to disagree with: creating a directory is one act.
    /// </para>
    /// <para>
    /// No projects returns true: a machine that has never had one is an ordinary machine, not a broken
    /// one. What cannot be read returns false and is reported <b>in terms of the project and its file</b>,
    /// never as the machine having no projects.
    /// </para>
    /// <para>
    /// <b>One bad folder costs that project and no other.</b> A folder that is not a project, or whose
    /// configuration does not read, is left out and said; two projects claiming one endpoint are both left out, since
    /// serving either would be a guess about which the person meant.
    /// </para>
    /// </remarks>
    public static bool TryRead(
        string root,
        out IReadOnlyList<ProjectSettings> projects,
        out string? problem)
    {
        projects = [];
        problem = null;

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
                // **Only when it holds a configuration.** A folder with none is nobody's project whatever its name,
                // and the stamp the watchers compare counts configurations only, so the two agree by construction
                // (see ProjectFolder.StampOf).
                if (!File.Exists(Path.Combine(folder, ProjectFolder.ConfigurationFileName)))
                {
                    continue;
                }

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

        // **Collisions is the one check the filesystem cannot make.** A name is a directory entry and a store is
        // derived from it, so only two projects claiming one endpoint can clash; a hand edit produces it and it is
        // reported naming both rather than resolved in favour of either.
        problems.AddRange(Collisions(found));

        var clashing = found
            .Where(p => !string.IsNullOrEmpty(p.Endpoint))
            .GroupBy(p => p.Endpoint, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        if (problems.Count > 0)
        {
            problem = $"{directory} holds something that is not a usable project, and it was left out:" +
                $"{Environment.NewLine}  - " + string.Join($"{Environment.NewLine}  - ", problems);
        }

        projects = [.. found.Where(p => !clashing.Contains(p))];
        return true;
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
    /// <b>Only an endpoint can collide.</b> A project's name is its directory entry, so a name cannot
    /// repeat, and its store is derived from that name, so two projects cannot reach one file.
    /// </para>
    /// <para>
    /// A shared endpoint is possible because nothing about the layout prevents it: two projects on one
    /// port is a hand edit away, and only one broker can hold a port. Reported naming both projects
    /// rather than resolved in favour of either — whichever this picked would be somebody's deployment
    /// silently not starting.
    /// </para>
    /// <para>
    /// Applied when reading and when writing, so a hand-edited file meets the same rule as a write. It
    /// cannot live in storage: the comparison spans N separate files, and no one store sees them all.
    /// </para>
    /// <para>
    /// The rows are named by project, since there is no array position to count.
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

    /// <summary>The name of the lock held while this machine's projects are being changed.</summary>
    /// <remarks>
    /// <para>
    /// A sibling rather than something under the temporary directory, so two writers agree about
    /// it for the same reason they agree about the manifest: it is the same path, with the same
    /// permissions, on the same volume. A per-user temporary directory would exclude two commands
    /// run by one person and let two people editing one <c>--config</c> straight through.
    /// </para>
    /// <para>
    /// <b>Public because the grant gate takes the same lock.</b> Granting a repository an unattended
    /// start reads every project's catalogue and then writes one of them, and those two must be one
    /// act under the same machine-wide exclusion as this file's own writes.
    /// </para>
    /// </remarks>
    /// <param name="root">The machine's root.</param>
    /// <returns>The lock file's path, which need not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    public static string LockFor(string root) => Path.Combine(ProjectFolder.ProjectsIn(root), ".lock");

    /// <summary>
    /// Changes the machine's projects under a lock, so a read and the write that depends on it are one act.
    /// </summary>
    /// <param name="path">The machine's root.</param>
    /// <param name="change">
    /// Given what the root holds, returns what it should hold — or <see langword="null"/> to
    /// leave it alone, having reported why itself.
    /// </param>
    /// <param name="problem">Why nothing could be attempted, when this returns false.</param>
    /// <returns>
    /// <see langword="true"/> when the projects were written; <see langword="false"/> when they were
    /// not, with <paramref name="problem"/> set unless <paramref name="change"/> declined.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Adding a project is a read and then a write, so both run under one lock.</b> Unlocked, two
    /// concurrent adds would each write from what they had read and the second would silently discard
    /// the first, or write a duplicate endpoint that collision checks against a stale read let through.
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
            // **Inside the projects directory rather than at the root**, because the root holds exactly two
            // files that are not a project. A file here is invisible to the reader, which lists directories.
            //
            // **Through LockFor rather than composed here**: `Catalogues.GateAsync` takes this same lock so that
            // a bulk enrolment and a rewrite exclude each other, and two implementations of one rule drift.
            Directory.CreateDirectory(ProjectFolder.ProjectsIn(path));
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
            // Inside the lock, because the write below is computed from it.
            // **Refused when anything was left out.** Reading leaves an unreadable project out, and Write removes every
            // configuration not in what it is handed, so a write would delete the unreadable project's file.
            // A write needs every project, or none.
            if (!TryRead(path, out var projects, out var unreadable) || unreadable is { Length: > 0 })
            {
                problem = $"Nothing has been changed, because a project here cannot be read: {unreadable}";
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
    /// Returns rather than throws on the deadline, so a held lock and a lock file that cannot be
    /// opened at all are told apart.
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
    /// A project is its own small file, so a half-written one costs that project and not the machine,
    /// and the folder that holds it is created by one atomic act.
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
        // what forgetting a project is (it deletes nothing), and a folder with no configuration is
        // already not a project. Destroying a store is `remove --destroy`, a different act behind a
        // different door; a writer that deleted folders would make every caller capable of it.
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
