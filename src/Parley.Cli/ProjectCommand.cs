using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Cli;

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

    /// <summary>
    /// Runs one project subcommand.
    /// </summary>
    /// <param name="arguments">The subcommand and its operands.</param>
    /// <param name="manifestPath">The manifest this invocation resolved.</param>
    /// <param name="fields">The values `add` writes, taken before dispatch.</param>
    /// <param name="output">Where to report what happened.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        string manifestPath,
        ProjectFields fields,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        return arguments switch
        {
            ["list"] => await ListAsync(manifestPath, output, error, cancellationToken).ConfigureAwait(false),
            ["add", var name] => await AddAsync(name, manifestPath, fields, output, error).ConfigureAwait(false),
            ["remove", var name] => await RemoveAsync(name, manifestPath, output, error, cancellationToken)
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
            "parley project remove <name>               forget how to reach a project")
            .ConfigureAwait(false);

        return Usage;
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

            if (project.NodeConfig is { Length: > 0 } node)
            {
                await output.WriteLineAsync($"                 node   {node}").ConfigureAwait(false);
            }

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

        var node = fields.NodeConfig;
        var description = fields.Description;

        var settings = new ProjectSettings(name, store, endpoint, node, description);
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
                faults.AddRange(ProjectManifest.PathProblems(settings));

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
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? missing = null;

        // Captured out of the change below, because what is reported afterwards is the project
        // that was actually removed rather than the one that was asked for -- and it is only
        // known inside the lock, where the manifest was read.
        ProjectSettings? going = null;

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

                return [.. projects.Where(p => p != going)];
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

        // Said explicitly, every time. Somebody removing a project may well have meant to delete
        // its data too, and the only safe way to offer that is to say what still exists and let
        // them do it deliberately.
        await output.WriteLineAsync(
            $"Nothing was deleted. The store remains at {going!.Store}" +
            (going.NodeConfig is { Length: > 0 } node ? $", the node catalogue at {node}" : string.Empty) +
            ", and every repository's launch configuration is untouched.").ConfigureAwait(false);

        if (await ReachableAsync(going.Endpoint, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(
                $"A broker still appears to be running at {going.Endpoint}. This entry was how you " +
                "reached it.").ConfigureAwait(false);
        }

        return 0;
    }
}

/// <summary>
/// The values `project add` writes, taken from the command line before dispatch.
/// </summary>
/// <param name="Store">The database file, from <c>--db</c>.</param>
/// <param name="Endpoint">The transport address, from <c>--listen</c>.</param>
/// <param name="NodeConfig">The node catalogue, from <c>--node-config</c>, or null.</param>
/// <param name="Description">One line for the operator, from <c>--description</c>, or null.</param>
/// <remarks>
/// Passed in rather than parsed here, because the entry point consumes every option before any
/// command sees its arguments — anything left starting with a hyphen is refused as an option the
/// tool does not have. A command that parsed its own flags would never receive them.
/// </remarks>
public sealed record ProjectFields(string? Store, string? Endpoint, string? NodeConfig, string? Description);
