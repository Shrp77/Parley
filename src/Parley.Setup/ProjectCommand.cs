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
    /// <param name="root">Where this machine keeps its Parley files, as this invocation resolved it.</param>
    /// <param name="fields">The values `add` writes, taken before dispatch.</param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="destroy">
    /// For <c>remove</c>: also stop the project's broker and delete its store. The
    /// one destructive thing this command can do, and it says so in its name.
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        string root,
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
            ["list"] => await ListAsync(root, output, error, cancellationToken).ConfigureAwait(false),
            ["add", var name] => await AddAsync(name, root, fields, output, error).ConfigureAwait(false),
            ["set", var name] => await SetAsync(name, root, fields, output, error).ConfigureAwait(false),
            ["remove", var name] => await RemoveAsync(name, root, destroy, output, error, cancellationToken)
                .ConfigureAwait(false),
            _ => await UsageAsync(error).ConfigureAwait(false),
        };
    }

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync(
            "parley project list                        what this machine is configured for\n" +
            "\n" +
            "Projects are created in the terminal or the web console, and removed or renamed only in the terminal:\n" +
            "\n" +
            "    parley\n" +
            "\n" +
            "It is the same code this command runs, on a screen that asks for what it needs -- and\n" +
            "removing a project there describes what goes with it and takes the project's own name\n" +
            "typed back, which a command line cannot do.")
            .ConfigureAwait(false);

        return Usage;
    }

    /// <summary>Changes the fields given on one project and leaves the rest as they were.</summary>
    /// <remarks>
    /// Remove-and-add would print a paragraph about what was not deleted for a change that never
    /// meant to delete anything. Validated as one whole, under the same lock, by the same rules as
    /// <c>add</c>.
    /// </remarks>
    private static async Task<int> SetAsync(
        string requested,
        string root,
        ProjectFields fields,
        TextWriter output,
        TextWriter error)
    {
        if (fields.Description is null)
        {
            await error.WriteLineAsync("Nothing to change. Give --description.").ConfigureAwait(false);

            return Usage;
        }

        string? missing = null;
        ProjectSettings? changed = null;
        var faults = new List<string>();

        try
        {
            var written = ProjectManifest.TryUpdate(root, projects =>
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

                // **Neither a store nor an endpoint is among the things that can be changed.** The store
                // is derived from the project's name and the endpoint was claimed when it was created, so
                // neither has a value to set — moving a project's data is moving its folder, and moving
                // its port is a claim, both of which are acts rather than edits.
                //
                // An empty value clears the one optional field there is.
                changed = current with
                {
                    Description = fields.Description is { Length: > 0 } about ? about : null,
                };

                faults.AddRange(changed.Problems());

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
                    $"{Environment.NewLine}'{requested}' is unchanged.")
                    .ConfigureAwait(false);

                return Config;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            await error.WriteLineAsync(
                $"Could not change '{requested}' under {ProjectFolder.ProjectsIn(root)}: {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        await output.WriteLineAsync($"Changed '{requested}'.").ConfigureAwait(false);

        // What it says now, so the person can see the change landed without a second command -- and
        // because an endpoint changed here restarts the project in the process serving this machine,
        // which is worth saying where the change is made.
        await output.WriteLineAsync(
            $"  endpoint  {changed!.Endpoint}{Environment.NewLine}" +
            $"  store     {changed.Store}" +
            (changed.Description is { Length: > 0 } about ? $"{Environment.NewLine}  {about}" : string.Empty))
            .ConfigureAwait(false);

        await output.WriteLineAsync(
            "The process serving this machine restarts this project on the new endpoint within a " +
            "few seconds; a broker started by hand keeps what it started with until its next " +
            "start.").ConfigureAwait(false);

        return 0;
    }

    private static async Task<int> ListAsync(
        string root,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!ProjectManifest.TryRead(root, out var projects, out var problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return Config;
        }

        // What was left out, said before what is listed: a listing that silently lacked a project is how one goes missing.
        // And the listing then exits as a refusal does, so a script reading it is not told all was well (never
        // read as the machine having fewer projects than it has).
        var incomplete = problem is { Length: > 0 };

        if (incomplete)
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
        }

        if (projects.Count == 0 && incomplete)
        {
            // Never "no projects configured" when one is there and could not be read.
            return Config;
        }

        if (projects.Count == 0)
        {
            // Not an error. A machine with nothing configured is an ordinary machine, and saying
            // where a manifest would live is more use than saying it is missing.
            await output.WriteLineAsync(
                $"No projects configured. One would live under {ProjectFolder.ProjectsIn(root)}.\n" +
                "Name the first one in `parley`.")
                .ConfigureAwait(false);

            return 0;
        }

        foreach (var project in projects.OrderBy(p => p.Name.Value, StringComparer.Ordinal))
        {
            var answered = await ReachableAsync(project.Endpoint, cancellationToken).ConfigureAwait(false);

            // Two facts, never one word: whether the broker *answered*, which is about right now and
            // changes minute to minute; and whether anything off this machine could *address* it at all,
            // which is a property of the endpoint and changes only when somebody edits `project.json`.
            // They are on separate lines because a reader scanning a column must not read "answering"
            // as "reachable from the network".
            await output.WriteLineAsync(
                $"{project.Name.Value,-16} {project.Endpoint,-28} {(answered ? "answering" : "not answering")}")
                .ConfigureAwait(false);

            // The same rule the broker warns on at every start, asked of the same method, so a
            // listing and a start-up warning cannot come to disagree about one endpoint.
            await output.WriteLineAsync(
                BrokerEndpoint.OnlyThisMachine(project.Endpoint)
                    ? "                 only this machine can address it"
                    : "                 ANY MACHINE that can reach that address can address it, with no credentials")
                .ConfigureAwait(false);

            await output.WriteLineAsync($"                 store  {project.Store}").ConfigureAwait(false);

            if (project.Description is { Length: > 0 } description)
            {
                await output.WriteLineAsync($"                 {description}").ConfigureAwait(false);
            }
        }

        // The root, labelled, because both installers read this line. `root:` rather than `manifest:`,
        // because the deployment is a directory, not a file.
        await output.WriteLineAsync($"\nroot:     {root}").ConfigureAwait(false);
        await output.WriteLineAsync($"projects: {ProjectFolder.ProjectsIn(root)}").ConfigureAwait(false);

        return incomplete ? Config : 0;
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
        string root,
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

        if (ProjectName.IsDeviceName(name.Value))
        {
            await error.WriteLineAsync(
                $"'{name}' is a name Windows keeps for a device, so a project's folder cannot be called it. " +
                "Choose another name.").ConfigureAwait(false);

            return Usage;
        }

        // A name is the whole of it: the store is derived from the name and the port is claimed, so
        // there is nothing left to get wrong.
        if (ProjectFolder.TryRead(root, name, out _, out _))
        {
            // Names no command, because this text is read on two surfaces: the terminal runs this same
            // method in process and prints what it said.
            await error.WriteLineAsync(
                $"'{name}' is already a project on this machine. It can be described differently, "
                + "or taken out, without being created again.").ConfigureAwait(false);

            return Config;
        }

        // The port is claimed before anything is created, so a claim that fails leaves no folder and no
        // half-written configuration behind. The claim is also the only thing here that can collide with
        // another command running at the same moment.
        //
        // There is no way to state a port. A project that must be reachable from another machine (SPEC.md
        // §16) has its endpoint edited in its own `project.json`, the one place every reader looks.
        //
        // Nothing is created beside a project that cannot be read. Reading leaves such a project out and
        // carries on, since serving and listing the rest costs nothing; creating does not, because the
        // port the unreadable one holds is unknown and a claim could hand it out again.
        if (!ProjectManifest.TryRead(root, out _, out var unreadable) || unreadable is { Length: > 0 })
        {
            await error.WriteLineAsync(
                $"'{name}' has not been created, because another project here cannot be read: {unreadable}")
                .ConfigureAwait(false);

            return Config;
        }

        if (!PortLedger.TryClaim(root, name, out var endpoint, out var refused))
        {
            await error.WriteLineAsync(refused).ConfigureAwait(false);

            return Config;
        }

        var settings = new ProjectSettings(name, ProjectFolder.StoreFor(root, name), endpoint!, fields.Description);
        var faults = new List<string>();

        try
        {
            // Read, validated and written as one act, under a lock: otherwise two `project add` commands
            // each append to what they read, and the second discards the first.
            var written = ProjectManifest.TryUpdate(root, projects =>
            {
                // The same checks reading applies, in the same order and from the same methods. Stated
                // out separately here once, which is how a rule comes to be enforced in one direction
                // only.
                faults.AddRange(settings.Problems());

                var updated = projects.Append(settings).ToArray();

                faults.AddRange(ProjectManifest.Collisions(updated));

                // Declining rather than throwing, because these are the operator's faults to be told
                // about and the message below says them all at once.
                return faults.Count > 0 ? null : updated;
            },
            out var problem);

            if (problem is not null || !written)
            {
                // **The port goes back.** It was claimed a moment ago and nothing is going to use it, so
                // leaving it recorded would strand it against a project that does not exist -- and the
                // record is what the next claim steps over. Best effort: a release that cannot be taken
                // leaves bookkeeping to be reconciled, which is what reconciliation is for.
                _ = PortLedger.TryRelease(root, name, out _);

                if (problem is not null)
                {
                    // Never start fresh over configuration that could not be read, and never write one
                    // this could not lock. Overwriting an unreadable configuration is how a deployment
                    // silently loses everything it described.
                    await error.WriteLineAsync(problem).ConfigureAwait(false);

                    return Config;
                }

                await error.WriteLineAsync(
                    $"Cannot add '{name}':{Environment.NewLine}  - " +
                    string.Join($"{Environment.NewLine}  - ", faults) +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    $"Nothing has been created under {ProjectFolder.ProjectsIn(root)}.")
                    .ConfigureAwait(false);

                return Config;
            }
        }
        // InvalidDataException included, and it is the backstop rather than the expected path: every
        // fault the write refuses has already been reported above with all the others. If the two ever
        // disagree the operator gets a sentence rather than a stack trace, which is the difference
        // between a bug report and a mystery.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            _ = PortLedger.TryRelease(root, name, out _);

            await error.WriteLineAsync(
                $"Could not create '{name}' under {ProjectFolder.ProjectsIn(root)}: {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        // Both facts, because the endpoint is what every other command needs to reach it and the store
        // is what a backup has to include.
        await output.WriteLineAsync($"Added '{name}'.").ConfigureAwait(false);
        await output.WriteLineAsync($"  serving on {endpoint}").ConfigureAwait(false);
        await output.WriteLineAsync($"  files in  {ProjectFolder.For(root, name)}").ConfigureAwait(false);

        // Said here because this is the one moment somebody is thinking about where a project answers.
        // There is no flag that binds a network address, so the answer for another machine is a tunnel,
        // done outside this tool; naming it makes the capability look confined rather than absent.
        await output.WriteLineAsync(
            "  reachable from this machine only. An agent elsewhere reaches it through a tunnel "
            + "to that port -- see the README; there is no flag that binds a network address, "
            + "because the wire carries no credentials.").ConfigureAwait(false);

        return 0;
    }

    private static async Task<int> RemoveAsync(
        string requested,
        string root,
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

        if (destroy && ProjectManifest.TryRead(root, out var before, out _))
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
            var written = ProjectManifest.TryUpdate(root, projects =>
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
            await error.WriteLineAsync($"Could not change the projects under {ProjectFolder.ProjectsIn(root)}: {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        // The port goes back only once the project is genuinely gone: released before the write, a write
        // that then failed would hand somebody else a port this project still serves on. The record is
        // bookkeeping, so a release that cannot take the hold leaves an entry for reconciliation to step
        // over and must not change the exit code.
        _ = PortLedger.TryRelease(root, going!.Name, out _);

        await output.WriteLineAsync($"Removed '{requested}'.").ConfigureAwait(false);

        if (destroy)
        {
            return await DestroyAsync(going!, agents, root, output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        // Said explicitly, every time. Somebody removing a project may well have meant to delete
        // its data too, and the only safe way to offer that is to say what still exists and let
        // them do it deliberately.
        await output.WriteLineAsync(
            $"Nothing was deleted. The store remains at {going.Store}" +
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
    /// touching a file the broker still had open. A broker started by hand was not told
    /// anything, and is left running rather than killed: it is reported, with the store's
    /// path, and the deleting is the person's.
    /// </para>
    /// <para>
    /// <b>No catalogue goes with it, because there is none per project.</b> The catalogue is one per machine, keyed by
    /// each repository's identity, and a repository's membership is read out of the repository — so there is nothing of
    /// the project's left in it to delete.
    /// </para>
    /// </remarks>
    private static async Task<int> DestroyAsync(
        ProjectSettings going,
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

        // Every copy taken before an upgrade (SPEC.md §9.2). Each holds the text of every conversation up to its upgrade,
        // and the destroy says the conversations go with the store. After the lock, like the store itself.
        if (Path.GetDirectoryName(Path.GetFullPath(going.Store)) is { } beside && Directory.Exists(beside))
        {
            foreach (var path in Directory.EnumerateFiles(beside, Path.GetFileName(going.Store) + Parley.Core.StoreFiles.BeforeSchema + "*"))
            {
                if (ProjectManifest.IsTheManifest(path, manifestPath))
                {
                    continue;
                }

                if (!await TryDeleteAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    await error.WriteLineAsync(
                        $"Could not delete {path}: something still has it open. The entry is gone " +
                        $"and {string.Join(", ", deleted)} {(deleted.Count == 1 ? "was" : "were")} " +
                        "deleted; delete the rest yourself once nothing holds it.")
                        .ConfigureAwait(false);

                    return IoError;
                }

                deleted.Add(path);
            }
        }

        // The file location and its staging directory (SPEC.md §9.5), and only after the lock went: a
        // broker still serving would have kept the lock, and deleting files it may be streaming to an
        // agent would be the partial destruction the ordering above exists to prevent.
        foreach (var suffix in new[] { Parley.Core.StoreFiles.Files, Parley.Core.StoreFiles.FilesIncoming, Parley.Core.StoreFiles.FilesQuarantine })
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
/// <param name="Description">One line for the operator, from <c>--description</c>, or null.</param>
/// <remarks>
/// <para>
/// <b>There is neither a store nor an endpoint here.</b> A project's store is derived from its name and
/// its endpoint is claimed when it is created, so neither has a value a command line could supply. A
/// description is the one thing that is somebody's to choose.
/// </para>
/// <para>
/// Passed in rather than parsed here, because the entry point consumes every option before any
/// command sees its arguments — anything left starting with a hyphen is refused as an option the
/// tool does not have. A command that parsed its own flags would never receive them.
/// </para>
/// </remarks>
public sealed record ProjectFields(string? Description);
