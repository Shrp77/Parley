using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// Adds, removes and lists the projects configured on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The manifest exists to stop operators maintaining paths by hand, so a file that must itself be
/// hand-created — in a location they have to look up — would keep most of the problem. Writing it
/// through the tool also means the whole file is validated before any of it is written, which is
/// where a duplicate endpoint or a shared store is caught: otherwise it surfaces later as a broker
/// that will not start, with nothing connecting the two events.
/// </para>
/// <para>
/// <b>Removal is withdrawal, not destruction.</b> Taking a project out of the manifest forgets how
/// to reach it and leaves every file intact — the same distinction <c>retire</c> and <c>purge</c>
/// already draw between withdrawing a registration and destroying one. A configuration command
/// able to delete a store as a side effect would be the most destructive operation in this tool
/// wearing the least alarming name.
/// </para>
/// </remarks>
public static class ProjectCommand
{
    private const int Usage = 64;          // EX_USAGE
    private const int Config = 78;         // EX_CONFIG
    private const int IoError = 74;        // EX_IOERR
    private const int Unavailable = 69;    // EX_UNAVAILABLE

    /// <summary>
    /// Runs one project subcommand.
    /// </summary>
    /// <param name="arguments">The subcommand and its operands.</param>
    /// <param name="manifestPath">The manifest this invocation resolved.</param>
    /// <param name="fields">The values `add` writes, taken before dispatch.</param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="destroy">
    /// For <c>remove</c>: also stop the project's broker and delete its store and catalogue. The
    /// one destructive thing this command can do, and it says so in its name.
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        string manifestPath,
        ProjectFields fields,
        TextWriter output,
        TextWriter error,
        bool destroy = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        return arguments switch
        {
            ["list"] => await ListAsync(manifestPath, output, error, cancellationToken).ConfigureAwait(false),
            ["add", var name] => await AddAsync(name, manifestPath, fields, output, error).ConfigureAwait(false),
            ["set", var name] => await SetAsync(name, manifestPath, fields, output, error).ConfigureAwait(false),
            ["remove", var name] => await RemoveAsync(name, manifestPath, destroy, output, error, cancellationToken)
                .ConfigureAwait(false),
            _ => await UsageAsync(error).ConfigureAwait(false),
        };
    }

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync(
            "parley project list                        what this machine is configured for\n" +
            "parley project add <name> --db <path>      add a project\n" +
            "                          --listen <url>\n" +
            "                          [--node-config <path>] [--description <text>]\n" +
            "parley project set <name> [--db <path>]    change what a project says; only the\n" +
            "                          [--listen <url>]  fields given are changed\n" +
            "                          [--node-config <path>] [--description <text>]\n" +
            "parley project remove <name>               forget how to reach a project\n" +
            "                      [--destroy]          ...and stop its broker and delete\n" +
            "                                           its store and catalogue")
            .ConfigureAwait(false);

        return Usage;
    }

    /// <summary>Changes the fields given on one project and leaves the rest as they were.</summary>
    /// <remarks>
    /// Because the alternative was remove-and-add, and removing a project prints a paragraph about
    /// what was not deleted for a change that never meant to delete anything. The case that made
    /// this necessary is a catalogue: a project named before its machine started agents for it
    /// has no <c>node_config</c>, and an autostart entry serving the manifest hosts a node only
    /// where one is named — so without a way to add it, switching entries silently dropped the
    /// node. Validated as one whole, under the same lock, by the same rules as <c>add</c>.
    /// </remarks>
    private static async Task<int> SetAsync(
        string requested,
        string manifestPath,
        ProjectFields fields,
        TextWriter output,
        TextWriter error)
    {
        if (fields.Store is null && fields.Endpoint is null && fields.Description is null)
        {
            await error.WriteLineAsync(
                "Nothing to change. Give at least one of --db, --listen or --description.")
                .ConfigureAwait(false);

            return Usage;
        }

        string? missing = null;
        ProjectSettings? changed = null;
        var faults = new List<string>();

        try
        {
            var written = ProjectManifest.TryUpdate(manifestPath, projects =>
            {
                var current = projects.FirstOrDefault(p =>
                    string.Equals(p.Name.Value, requested, StringComparison.Ordinal));

                if (current is null)
                {
                    missing = projects.Count == 0
                        ? $"No project named '{requested}': this machine has none configured."
                        : $"No project named '{requested}'. Configured: " +
                          $"{string.Join(", ", projects.Select(p => p.Name.Value).Order(StringComparer.Ordinal))}.";

                    return null;
                }

                // An empty value clears an optional field, so a catalogue can be taken away as
                // well as given; the required two cannot be emptied, and the manifest's own
                // rules say so below.
                changed = current with
                {
                    Store = fields.Store ?? current.Store,
                    Endpoint = fields.Endpoint ?? current.Endpoint,
                    Description = fields.Description is null
                        ? current.Description
                        : fields.Description.Length == 0 ? null : fields.Description,
                };

                faults.AddRange(changed.Problems());
                faults.AddRange(ProjectManifest.PathProblems(changed, manifestPath));

                var updated = projects.Select(p => p == current ? changed : p).ToArray();

                faults.AddRange(ProjectManifest.Collisions(updated));

                return faults.Count > 0 ? null : updated;
            },
            out var problem);

            if (problem is not null)
            {
                await error.WriteLineAsync(problem).ConfigureAwait(false);
                return Config;
            }

            if (!written)
            {
                await error.WriteLineAsync(
                    missing ??
                    $"Cannot change '{requested}':{Environment.NewLine}  - " +
                    string.Join($"{Environment.NewLine}  - ", faults) +
                    $"{Environment.NewLine}{manifestPath} is unchanged.")
                    .ConfigureAwait(false);

                return Config;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            await error.WriteLineAsync($"Could not write {manifestPath}: {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        await output.WriteLineAsync($"Changed '{requested}' in {manifestPath}.").ConfigureAwait(false);

        // What it says now, so the person can see the change landed without a second command --
        // and because a store, endpoint or catalogue changed here restarts the project in the
        // process serving the manifest, which is worth saying where the change is made.
        await output.WriteLineAsync(
            $"  endpoint  {changed!.Endpoint}{Environment.NewLine}" +
            $"  store     {changed.Store}" +
            (changed.Description is { Length: > 0 } about ? $"{Environment.NewLine}  {about}" : string.Empty))
            .ConfigureAwait(false);

        await output.WriteLineAsync(
            "The process serving the manifest restarts this project on the new store, endpoint " +
            "or catalogue within a few seconds; a broker started by hand keeps what it started " +
            "with until its next start.").ConfigureAwait(false);

        return 0;
    }

    private static async Task<int> ListAsync(
        string manifestPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!ProjectManifest.TryRead(manifestPath, out var projects, out var problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return Config;
        }

        if (projects.Count == 0)
        {
            // Not an error. A machine with nothing configured is an ordinary machine, and saying
            // where a manifest would live is more use than saying it is missing.
            await output.WriteLineAsync(
                $"No projects configured. One would live at {manifestPath}.\n" +
                "Add one with `parley project add <name> --db <path> --listen <url>`.")
                .ConfigureAwait(false);

            return 0;
        }

        foreach (var project in projects.OrderBy(p => p.Name.Value, StringComparer.Ordinal))
        {
            var reachable = await ReachableAsync(project.Endpoint, cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(
                $"{project.Name.Value,-16} {project.Endpoint,-28} {(reachable ? "reachable" : "not reachable")}")
                .ConfigureAwait(false);

            await output.WriteLineAsync($"                 store  {project.Store}").ConfigureAwait(false);

            if (project.Description is { Length: > 0 } description)
            {
                await output.WriteLineAsync($"                 {description}").ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync($"\nmanifest: {manifestPath}").ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Says whether a broker answers, without letting a dead one stall the report.
    /// </summary>
    /// <remarks>
    /// Observed rather than recorded, for the same reason agent liveness is read from a heartbeat:
    /// a written-down status is a claim that goes stale the moment the thing it describes changes,
    /// and nothing is watching to correct it. Bounded because an operator listing five projects
    /// should not wait on the one that is down.
    /// </remarks>
    private static async Task<bool> ReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            using var channel = GrpcChannel.ForAddress(endpoint);

            await channel.ConnectAsync(timeout.Token).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static async Task<int> AddAsync(
        string requested,
        string manifestPath,
        ProjectFields fields,
        TextWriter output,
        TextWriter error)
    {
        if (!ProjectName.TryCreate(requested, out var name))
        {
            await error.WriteLineAsync(
                $"'{requested}' is not a project name. " +
                "Names are lowercase letters, digits and hyphens, starting with a letter, at most " +
                $"{ProjectName.MaxLength} characters.").ConfigureAwait(false);

            return Usage;
        }

        var store = fields.Store;
        var endpoint = fields.Endpoint;

        if (string.IsNullOrWhiteSpace(store) || string.IsNullOrWhiteSpace(endpoint))
        {
            await error.WriteLineAsync(
                "A project needs a store and an endpoint: --db <path> --listen <url>.")
                .ConfigureAwait(false);

            return Usage;
        }

        var description = fields.Description;

        var settings = new ProjectSettings(name, store, endpoint, description);
        var faults = new List<string>();

        try
        {
            // Read, validated and written as one act, under a lock. Every part of this was here
            // before; what was missing was that they belonged together. Two `project add`
            // commands each read the manifest, each appended to what it had read, and each wrote
            // -- so the second discarded the first, with two successes reported and one project
            // written. The collision checks made it worse rather than better: they were computed
            // against a manifest that no longer existed, so the losing writer could also write a
            // duplicate endpoint the rules exist to refuse.
            var written = ProjectManifest.TryUpdate(manifestPath, projects =>
            {
                // The same three checks the manifest applies on read, in the same order and from
                // the same methods. Written out separately here once, with its own wording, which
                // is how a rule comes to be enforced in one direction only.
                //
                // The cost is that a fault names the manifest field rather than the flag that
                // wrote it -- "store" where the operator typed --db. Taken deliberately: the value
                // is about to live in the file under that name, the mapping is one line below, and
                // one rule with one message is worth more than two messages that agree today.
                faults.AddRange(settings.Problems());
                faults.AddRange(ProjectManifest.PathProblems(settings, manifestPath));

                var updated = projects.Append(settings).ToArray();

                faults.AddRange(ProjectManifest.Collisions(updated));

                // Declining rather than throwing, because these are the operator's faults to be
                // told about and the message below says them all at once.
                return faults.Count > 0 ? null : updated;
            },
            out var problem);

            if (problem is not null)
            {
                // Never start a fresh manifest over one that could not be read, and never write
                // one this could not lock. Overwriting an unreadable configuration is how a
                // deployment silently loses everything it described.
                await error.WriteLineAsync(problem).ConfigureAwait(false);
                return Config;
            }

            if (!written)
            {
                await error.WriteLineAsync(
                    $"Cannot add '{name}':{Environment.NewLine}  - " +
                    string.Join($"{Environment.NewLine}  - ", faults) +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    "store comes from --db, endpoint from --listen, node_config from --node-config." +
                    $"{Environment.NewLine}{manifestPath} is unchanged.")
                    .ConfigureAwait(false);

                return Config;
            }
        }
        // InvalidDataException included, and it is the backstop rather than the expected path:
        // every fault the write refuses has already been reported above with all the others.
        // If the two ever disagree the operator gets a sentence rather than a stack trace,
        // which is the difference between a bug report and a mystery.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            await error.WriteLineAsync($"Could not write {manifestPath}: {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        await output.WriteLineAsync($"Added '{name}' to {manifestPath}.").ConfigureAwait(false);

        return 0;
    }

    private static async Task<int> RemoveAsync(
        string requested,
        string manifestPath,
        bool destroy,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? missing = null;

        // Captured out of the change below, because what is reported afterwards is the project
        // that was actually removed rather than the one that was asked for -- and it is only
        // known inside the lock, where the manifest was read.
        ProjectSettings? going = null;
        IReadOnlyList<ProjectSettings> staying = [];

        // Asked before the entry goes, because the entry going is what stops the broker, and
        // the registry is the one place that says which repositories were agents here. Read
        // only when destroying: plain removal leaves the broker running and has no use for it.
        IReadOnlyList<(string Agent, string Workspace)>? agents = null;

        if (destroy && ProjectManifest.TryRead(manifestPath, out var before, out _))
        {
            var target = before.FirstOrDefault(p => string.Equals(p.Name.Value, requested, StringComparison.Ordinal));

            if (target is not null)
            {
                agents = await AgentsAsync(target.Endpoint, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            // Under the same lock as adding, and for the same reason: the project this removes is
            // chosen from what was read, so a concurrent add would otherwise be written over by a
            // remove that never saw it.
            var written = ProjectManifest.TryUpdate(manifestPath, projects =>
            {
                going = projects.FirstOrDefault(p =>
                    string.Equals(p.Name.Value, requested, StringComparison.Ordinal));

                if (going is null)
                {
                    missing = projects.Count == 0
                        ? $"No project named '{requested}': this machine has none configured."
                        : $"No project named '{requested}'. Configured: " +
                          $"{string.Join(", ", projects.Select(p => p.Name.Value).Order(StringComparer.Ordinal))}.";

                    return null;
                }

                staying = [.. projects.Where(p => p != going)];

                return staying;
            },
            out var problem);

            if (problem is not null)
            {
                await error.WriteLineAsync(problem).ConfigureAwait(false);
                return Config;
            }

            if (!written)
            {
                await error.WriteLineAsync(missing).ConfigureAwait(false);
                return Config;
            }
        }
        // InvalidDataException included, and it is the backstop rather than the expected path:
        // every fault Write refuses has already been reported above with all the others.
        // If the two ever disagree the operator gets a sentence rather than a stack trace,
        // which is the difference between a bug report and a mystery.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            await error.WriteLineAsync($"Could not write {manifestPath}: {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        await output.WriteLineAsync($"Removed '{requested}' from {manifestPath}.").ConfigureAwait(false);

        if (destroy)
        {
            return await DestroyAsync(going!, staying, agents, manifestPath, output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        // Said explicitly, every time. Somebody removing a project may well have meant to delete
        // its data too, and the only safe way to offer that is to say what still exists and let
        // them do it deliberately.
        await output.WriteLineAsync(
            $"Nothing was deleted. The store remains at {going!.Store}" +
            ", and every repository's launch configuration is untouched. `remove --destroy` " +
            "deletes them.").ConfigureAwait(false);

        if (await ReachableAsync(going.Endpoint, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(
                $"A broker still appears to be running at {going.Endpoint}. The process serving " +
                "the manifest stops it within a few seconds; one started by hand keeps running.")
                .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Stops a removed project's broker and deletes what withdrawal leaves behind.</summary>
    /// <param name="going">The project, as it was.</param>
    /// <param name="staying">Every project still named, for what must not be deleted.</param>
    /// <param name="agents">The registry as read before the entry went, or null if it could not be.</param>
    /// <param name="manifestPath">The manifest, which is never deleted whatever an entry says.</param>
    /// <param name="output">Where to say what was deleted.</param>
    /// <param name="error">Where to say what could not be.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// <para>
    /// <b>This command does not stop anything itself, and cannot.</b> §4 gives the store to one
    /// process, and that process is the one serving the manifest: removing the entry is what
    /// tells it to stop this project's broker, and this waits for that to have happened before
    /// touching a file the broker still had open. A broker started by hand, or by an entry
    /// with explicit paths, was not told anything, and is left running rather than killed: it
    /// is reported, with the store's path, and the deleting is the person's.
    /// </para>
    /// <para>
    /// The catalogue goes with the store. The manifest refuses two projects that share one —
    /// consent to run unattended is per project because the catalogue is — so a project's
    /// catalogue is nobody else's, and leaving it behind would be leaving a list of repositories
    /// that a deleted deployment was allowed to start.
    /// </para>
    /// </remarks>
    private static async Task<int> DestroyAsync(
        ProjectSettings going,
        IReadOnlyList<ProjectSettings> staying,
        IReadOnlyList<(string Agent, string Workspace)>? agents,
        string manifestPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        // Answering before is the sign the serving process is about to stop it; answering still,
        // after a generous multiple of the watch interval, is the sign nothing will. Generous
        // because stopping a project waits for its node to send every agent it started away,
        // which has its own grace period, and a wait that expired during that would report a
        // broker nobody owns while the owner was in the middle of stopping it.
        if (agents is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var stopped = false;

            while (DateTime.UtcNow < deadline)
            {
                if (!await ReachableAsync(going.Endpoint, cancellationToken).ConfigureAwait(false))
                {
                    stopped = true;
                    break;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            if (!stopped)
            {
                await error.WriteLineAsync(
                    $"The entry is gone, but a broker still answers at {going.Endpoint}, so it was " +
                    "not started by the process serving the manifest and nothing here can stop " +
                    $"it. Stop it yourself, then delete {going.Store} and its -wal and -shm " +
                    "files. Nothing was deleted.").ConfigureAwait(false);

                return Unavailable;
            }
        }

        var deleted = new List<string>();

        // The store and SQLite's companions. Retried briefly, because the broker releases the
        // file a moment after it stops answering, and on Windows a file still open cannot go.
        // Never the manifest, whatever the entry says: the manifest refuses such an entry on the
        // way in, and this is the second lock on the same door.
        //
        // **The lock first, and it is the check rather than a file to tidy.** A broker serving
        // this store holds it for as long as it serves, so failing to delete it means something
        // still is — a broker this command did not stop, started by hand or by a terminal restoring
        // its session. Deleting the store out from under that broker, and then reporting that the
        // lock could not go, would be the partial destruction this ordering exists to prevent.
        foreach (var suffix in new[]
                 {
                     Parley.Core.StoreFiles.Lock, Parley.Core.StoreFiles.Owner,
                     string.Empty, "-wal", "-shm", "-journal",
                 })
        {
            var path = going.Store + suffix;

            if (!File.Exists(path) || ProjectManifest.IsTheManifest(path, manifestPath))
            {
                continue;
            }

            if (!await TryDeleteAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(suffix, Parley.Core.StoreFiles.Lock, StringComparison.Ordinal))
                {
                    await error.WriteLineAsync(
                        $"The entry is gone, but {path} is still held, so a broker is still serving " +
                        $"{going.Store} — one this command did not stop. Stop it yourself, then " +
                        $"delete {going.Store} and the files beside it. Nothing was deleted.")
                        .ConfigureAwait(false);

                    return Unavailable;
                }

                await error.WriteLineAsync(
                    $"Could not delete {path}: something still has it open. The entry is gone " +
                    $"and {string.Join(", ", deleted)} {(deleted.Count == 1 ? "was" : "were")} " +
                    "deleted; delete the rest yourself once the broker has released it.")
                    .ConfigureAwait(false);

                return IoError;
            }

            deleted.Add(path);
        }

        // The file location and its staging directory (SPEC.md §9.5), and only after the lock went: a
        // broker still serving would have kept the lock, and deleting files it may be streaming to an
        // agent would be the partial destruction the ordering above exists to prevent.
        foreach (var suffix in new[] { Parley.Core.StoreFiles.Files, Parley.Core.StoreFiles.FilesIncoming })
        {
            var path = going.Store + suffix;

            if (!Directory.Exists(path))
            {
                continue;
            }

            if (!await TryDeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                await error.WriteLineAsync(
                    $"Could not delete {path}: something still has a file in it open. The entry is gone " +
                    $"and {string.Join(", ", deleted)} {(deleted.Count == 1 ? "was" : "were")} " +
                    "deleted; delete the rest yourself once nothing holds it.")
                    .ConfigureAwait(false);

                return IoError;
            }

            deleted.Add(path);
        }

        _ = staying;

        await output.WriteLineAsync(
            deleted.Count == 0
                ? $"Destroyed '{going.Name}'. Nothing was on disk to delete."
                : $"Destroyed '{going.Name}'. Deleted {string.Join(", ", deleted)}.")
            .ConfigureAwait(false);

        // The half nothing here can do. A repository's launch configuration names the project or
        // the broker's address, and a session opened there will be told no broker is listening.
        // Naming them is the most this can do, and it can only do that if the registry answered
        // before the broker went.
        if (agents is null)
        {
            await output.WriteLineAsync(
                "Its registry could not be read before it stopped, so the repositories that were " +
                "agents in it are not listed here. Each still names it in its launch configuration " +
                "and will report no broker listening when opened.").ConfigureAwait(false);
        }
        else if (agents.Count > 0)
        {
            await output.WriteLineAsync(
                "These repositories were agents in it. Each still names it in its launch " +
                "configuration (.mcp.json), which nothing here touches:").ConfigureAwait(false);

            foreach (var (agent, workspace) in agents.OrderBy(a => a.Agent, StringComparer.Ordinal))
            {
                await output.WriteLineAsync(
                    $"  {agent,-24} {(workspace.Length > 0 ? workspace : "(workspace not stated)")}")
                    .ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>The agents a project's broker knows, with where each lives.</summary>
    /// <returns>The list, or null when the broker did not answer.</returns>
    private static async Task<IReadOnlyList<(string Agent, string Workspace)>?> AgentsAsync(
        string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var channel = GrpcChannel.ForAddress(endpoint);
            var operators = new Transport.Operators.OperatorsClient(channel);

            var reply = await operators.DescribeRegistryAsync(
                new Transport.Empty(), cancellationToken: timeout.Token);

            return [.. reply.Rows.Select(row => (row.Name, row.Workspace))];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Deletes a file, waiting a little for whatever had it open to let go.</summary>
    private static async Task<bool> TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Delete(path);

                return true;
            }
            catch (IOException)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        return !File.Exists(path);
    }

    /// <summary>Deletes a directory and everything in it, retrying while something still holds a file.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="cancellationToken">Cancels the waits between attempts.</param>
    /// <returns><see langword="true"/> when the directory is gone.</returns>
    /// <remarks>
    /// Retried for the reason <see cref="TryDeleteAsync"/> is: a broker releases what it held a moment after
    /// it stops answering, and on Windows a file still open cannot be deleted.
    /// </remarks>
    private static async Task<bool> TryDeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);

                return true;
            }
            catch (IOException)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        return !Directory.Exists(path);
    }
}

/// <summary>
/// The values `project add` writes, taken from the command line before dispatch.
/// </summary>
/// <param name="Store">The database file, from <c>--db</c>.</param>
/// <param name="Endpoint">The transport address, from <c>--listen</c>.</param>
/// <param name="Description">One line for the operator, from <c>--description</c>, or null.</param>
/// <remarks>
/// Passed in rather than parsed here, because the entry point consumes every option before any
/// command sees its arguments — anything left starting with a hyphen is refused as an option the
/// tool does not have. A command that parsed its own flags would never receive them.
/// </remarks>
public sealed record ProjectFields(string? Store, string? Endpoint, string? Description);
