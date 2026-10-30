using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Wire = Parley.Transport;

namespace Parley.Cli;

/// <summary>
/// Runs this machine's node: the process of SPEC.md §4 that says the machine is here and starts
/// the agents on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never touches the store.</b> The broker owns the store and knows what is waiting, so it
/// says so — and what this contributes is the two things only a process on this machine can, which
/// are being evidence the machine is up and being able to start something on it.
/// </para>
/// <para>
/// <b>It is not a relay.</b> §4 is explicit, and it shapes what is missing here: no message
/// passes through this process and no agent's words touch it. An agent it starts connects to the
/// broker itself, under its own identity, through its own adapter. This holds the pipes and
/// nothing else.
/// </para>
/// <para>
/// <b>The command line never leaves the machine.</b> What can be started here comes from
/// <see cref="NodeCatalogue"/> — a local file — and the broker is told only which roles, never
/// how. That keeps an executable command line out of a shared store, which is a smaller trusted
/// surface than any arrangement where it is in one, and it stays smaller as a deployment spreads.
/// </para>
/// </remarks>
public static class NodeCommand
{
    private const int Config = 78;         // EX_CONFIG
    private const int Unavailable = 69;    // EX_UNAVAILABLE

    /// <summary>How many times a rejoin the broker refused as a second node is asked again.</summary>
    private const int RejoinAttempts = 8;

    /// <summary>The first wait before asking again, lengthened by each refusal: about seven seconds in all.</summary>
    private static readonly TimeSpan RejoinPause = TimeSpan.FromMilliseconds(200);

    /// <summary>How long to let an agent finish its turn before giving up on it.</summary>
    private static readonly TimeSpan TurnTimeout = InvocationCommand.DefaultTimeout;

    /// <summary>How long to let an agent shut down cleanly.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Joins the broker and supervises this machine until stopped.
    /// </summary>
    /// <param name="channel">The connection to the broker.</param>
    /// <param name="endpoint">Where the broker is, for reporting.</param>
    /// <param name="catalogueFile">The file naming what this machine can start.</param>
    /// <param name="output">Where to report what it is doing.</param>
    /// <param name="cancellationToken">Stops the node.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="catalogueFile"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// The catalogue is named by the caller rather than found here. Program.cs resolves it once,
    /// through the same precedence every other setting uses, and a node that resolved it a second
    /// time would ignore <c>--node-config</c>.
    /// </para>
    /// <para>
    /// <b>The file is watched, and a change to it rejoins.</b> The roles a node can start are sent
    /// in the join request, and a role added to the file afterwards would otherwise never be
    /// announced — an agent enrolled under a running node would read as <c>startable</c> and could
    /// not be started (SPEC.md §17 question 13). So the roles are compared every
    /// <see cref="ServeCommand.WatchInterval"/> and a difference ends the stream and joins again
    /// with the new set.
    /// </para>
    /// <para>
    /// It lives here rather than in the serve watcher because the node is the thing that knows its
    /// own file, and <c>parley node</c> run on its own must behave the same as one hosted by
    /// <c>serve --with-node</c>. The gap between the two joins reads as <c>stranded</c> for under
    /// a second, which is honest: for that moment the machine is not connected. Resident agents
    /// are kept across it — they belong to the machine, not to the connection.
    /// </para>
    /// </remarks>
    /// <param name="project">
    /// The project this node serves, which decides which names it announces. Null accepts every claim, for a
    /// node with no project of its own.
    /// </param>
    /// <param name="residents">
    /// The agents this machine is running, shared with every other node in this process and keyed by
    /// identity, so one checkout is one process however many projects ask for it. Null makes its own, which
    /// is what a node running alone wants.
    /// </param>
    public static async Task<int> RunAsync(
        GrpcChannel channel,
        string endpoint,
        string catalogueFile,
        TextWriter output,
        string? project = null,
        ConcurrentDictionary<AgentIdentity, ResidentAgent>? residents = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueFile);
        ArgumentNullException.ThrowIfNull(output);

        NodeCatalogue catalogue;

        try
        {
            catalogue = NodeCatalogue.ReadFrom(catalogueFile);
        }
        catch (InvalidOperationException exception)
        {
            await output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return Config;
        }

        var nodes = new Wire.Nodes.NodesClient(channel);

        // **Handed in rather than made here, and keyed by identity.** One process hosts a node per
        // broker it joins, so a table shared and keyed by what a checkout *is* means two projects asking
        // for one checkout find one process.
        // **Whoever makes this table is whoever stops it.** The table is shared across every project one
        // process hosts, so sweeping it whenever any node loop returns would send away every agent on the
        // machine, including the ones still serving other projects: `serve` stops and restarts a project
        // whose store or endpoint changed, and `project remove` ends one outright. SPEC.md §12 says what
        // that costs — the process is what held everything each agent had learned.
        var ours = residents is null;

        residents ??= new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        // Turns run concurrently and each reports its own outcome, so more than one of them can
        // be writing at once. A TextWriter is not thread-safe, and the callers here supply whatever
        // they have — one passes Console.Out, which is synchronised, and one passes a writer that
        // may not be. Wrapping once at the entry point makes that not a question the callers have
        // to have got right.
        output = TextWriter.Synchronized(output);

        // A rejoin follows the end of this node's own stream at once, and the broker lets go of the old join only when
        // it notices that end. Until it does, the rejoin reads as a second node and is refused: wait and ask again,
        // a bounded number of times, rather than end the node over its own shadow. A first join is never retried,
        // because a refusal then is a real second node.
        var rejoin = false;
        var refusals = 0;

        try
        {
            while (true)
            {
                var request = new Wire.JoinRequest
                {
                    Machine = NodeCatalogue.Machine,
                    Facts = Observe(),

                    // This node reports the turns it gives (SPEC.md §11.6), so its agents can be told working from stopped.
                    ReportsTurns = true,
                };

                // **What this node can start for this project, which the repositories decide.**
                // A catalogue entry says how to start a checkout and nothing about projects; each catalogued
                // workspace declares what it answers to, and a name two of them claim is announced by
                // neither.
                var announceable = catalogue.Announceable(project);

                request.StartableRoles.AddRange(announceable.Names);
                request.StartableIdentities.AddRange(catalogue.StartableIn(project).Select(identity => identity.ToString()));

                // Two sources, and they are not the same question. `watching` stops the poll when
                // this attempt is over for any reason; `rejoining` is cancelled only by the poll,
                // to end a stream whose announced roles have gone stale. Collapsing them would
                // make a broker that hung up look like a catalogue that changed.
                using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var rejoining = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var watcher = WatchAsync(
                    catalogueFile, project, announceable.Names, output, rejoining, watching.Token);
                var refused = false;

                try
                {
                    using var stream = nodes.Join(request, cancellationToken: rejoining.Token);

                    await foreach (var command in stream.ResponseStream
                        .ReadAllAsync(rejoining.Token).ConfigureAwait(false))
                    {
                        switch (command.CommandCase)
                        {
                            case Wire.NodeCommand.CommandOneofCase.Joined:
                                refusals = 0;
                                await ReportJoinedAsync(
                                    command.Joined, catalogue, project, catalogueFile, endpoint, output)
                                    .ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Start:
                                await StartAsync(command.Start, catalogue, project, residents, output,
                                    cancellationToken, new TurnReporter(nodes, request.Machine, output))
                                    .ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Turn:
                                await TurnAsync(
                                        command.Turn, catalogue, project, residents, output, cancellationToken,
                                        new TurnReporter(nodes, request.Machine, output))
                                    .ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Refresh:
                                await RefreshAsync(
                                    command.Refresh, catalogue, project, residents, output, cancellationToken,
                                    new TurnReporter(nodes, request.Machine, output))
                                    .ConfigureAwait(false);
                                break;

                            default:
                                // A command this build does not know can only be a bug: the wire
                                // changes in step with the node. Named loudly and failed, but the
                                // loop goes on, so one bad command does not take a machine off the air.
                                await output.WriteLineAsync(
                                    $"ERROR: the broker sent a node command this build does not know "
                                    + $"({command.CommandCase}). That command failed; this is a bug.")
                                    .ConfigureAwait(false);
                                break;
                        }
                    }
                }
                catch (Exception ending) when (
                    Rejoinable(ending)
                    && rejoining.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    // The catalogue changed under us. Not an error and not the end of the node.
                    //
                    // Both shapes are caught because cancelling a gRPC call does not reliably
                    // raise the cancellation: the stream reports it as an RpcException carrying
                    // Cancelled.
                }
                catch (RpcException failure) when (
                    failure.StatusCode == StatusCode.AlreadyExists && rejoin && refusals < RejoinAttempts)
                {
                    refused = true;
                }
                finally
                {
                    await watching.CancelAsync().ConfigureAwait(false);
                }

                if (refused)
                {
                    _ = await watcher.ConfigureAwait(false);
                    refusals++;
                    await Task.Delay(RejoinPause * refusals, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var changed = await watcher.ConfigureAwait(false);

                if (changed is null)
                {
                    // Either the person stopped this, or the broker ended the stream. Both mean
                    // the node is done; neither is a reason to reconnect in a loop.
                    break;
                }

                catalogue = changed;
                rejoin = true;

                await output.WriteLineAsync(
                    $"{catalogueFile} changed. Rejoining with: {Listed(catalogue.Announceable(project).Names)}")
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C, which is how this command is meant to end.
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            await output.WriteLineAsync(
                $"No broker is listening at {endpoint}. Start one with `parley serve`.")
                .ConfigureAwait(false);

            return Unavailable;
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.AlreadyExists)
        {
            await output.WriteLineAsync(failure.Status.Detail).ConfigureAwait(false);
            return Config;
        }
        finally
        {
            // Only the table this call created. A caller that supplied one owns its lifetime and sweeps it
            // when the process ends, which is the one moment every agent on the machine should be sent away.
            if (ours)
            {
                await StopEveryoneAsync(residents, output).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>
    /// Watches the catalogue and asks for a rejoin when the roles in it change.
    /// </summary>
    /// <param name="catalogueFile">The file to watch.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="announced">The roles the current join announced.</param>
    /// <param name="output">Where to report a file it could not read.</param>
    /// <param name="rejoining">Cancelled to end the current stream when the roles have changed.</param>
    /// <param name="cancellationToken">Stops the watch.</param>
    /// <returns>The catalogue to rejoin with, or null when nothing asked for one.</returns>
    /// <remarks>
    /// <para>
    /// <b>The roles are compared, not the file's timestamp.</b> A catalogue rewritten with the same
    /// agents in it — reformatted, or its commands edited — changes nothing the broker was told,
    /// and rejoining for it would drop every attached agent to announce the same list again.
    /// </para>
    /// <para>
    /// <b>A file it cannot read keeps the previous roles.</b> Somebody half-way through saving
    /// leaves a moment of invalid JSON, and a node that dropped all its roles because it read
    /// during that moment is worse than one that lags a poll. It is said once per spell of
    /// unreadability rather than every two seconds, because a message repeating thirty times a
    /// minute is a message nobody reads.
    /// </para>
    /// </remarks>
    private static async Task<NodeCatalogue?> WatchAsync(
        string catalogueFile,
        string? project,
        IReadOnlyCollection<string> announced,
        TextWriter output,
        CancellationTokenSource rejoining,
        CancellationToken cancellationToken)
    {
        var complained = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ServeCommand.WatchInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (!NodeCatalogue.TryReadFrom(catalogueFile, out var next, out var problem))
            {
                if (!complained)
                {
                    await output.WriteLineAsync(
                        $"{catalogueFile} could not be read: {problem} " +
                        "Keeping the roles already announced.").ConfigureAwait(false);

                    complained = true;
                }

                continue;
            }

            complained = false;

            // **Compared on what would be announced, not on what the file holds.** The catalogue is keyed
            // by identity and the names come from the repositories, so an entry repointed at a different
            // checkout changes nothing here and a repository renaming itself changes everything — which is
            // the comparison that actually decides whether the broker's picture is stale.
            if (next.Announceable(project).Names.Order(StringComparer.Ordinal)
                .SequenceEqual(announced.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                continue;
            }

            await rejoining.CancelAsync().ConfigureAwait(false);

            return next;
        }

        return null;
    }

    /// <summary>Whether an ended stream ended because this asked it to.</summary>
    /// <param name="ending">What came out of the stream.</param>
    /// <returns>Whether it is a cancellation rather than a failure.</returns>
    /// <remarks>
    /// The caller pairs this with a check that the rejoin was the thing cancelled, so a broker
    /// that itself cancelled a call is not mistaken for a catalogue that changed.
    /// </remarks>
    private static bool Rejoinable(Exception ending) =>
        ending is OperationCanceledException
        || ending is RpcException { StatusCode: StatusCode.Cancelled };

    /// <summary>The names a node offers, in a stable order, for a report.</summary>
    /// <param name="names">What it announced.</param>
    /// <returns>The names, or a phrase saying there are none.</returns>
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 0 ? "nothing" : string.Join(", ", names);

    /// <summary>Reports what the broker knows about this machine.</summary>
    /// <param name="joined">The acknowledgement.</param>
    /// <param name="catalogue">What this machine can start.</param>
    /// <param name="catalogueFile">The file it was read from, named when it turns out to be empty.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="endpoint">Where the broker is.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>A task that completes when it has been reported.</returns>
    /// <remarks>
    /// <para>
    /// The reconciliation is the useful part: an agent registered to this machine that the
    /// catalogue cannot launch is a repository that moved, was renamed, or was never listed —
    /// and saying so at startup beats discovering it when a request to it sits uncollected and is
    /// flagged.
    /// </para>
    /// <para>
    /// <b>"nothing" names the file it read.</b> An absent catalogue and an empty one are both
    /// legitimate and produce the same word, so without the path the one failure this cannot
    /// otherwise distinguish — a supervisor started somewhere the file is not — reads exactly
    /// like a machine nobody has listed any agents on yet.
    /// </para>
    /// </remarks>
    private static async Task ReportJoinedAsync(
        Wire.Joined joined,
        NodeCatalogue catalogue,
        string? project,
        string catalogueFile,
        string endpoint,
        TextWriter output)
    {
        var announceable = catalogue.Announceable(project);

        var startable = announceable.Names.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"nothing (no agents in {catalogueFile})")
            : string.Join(", ", announceable.Names);

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Node {joined.Machine} joined {endpoint}. Can start: {startable}."))
            .ConfigureAwait(false);

        // **A name two catalogued workspaces claim is started by neither, and saying so names both.** It is
        // either a copied repository or an enrolment mistake, and an agent's name is the one thing Parley
        // makes unforgeable — so this is the report a person acts on rather than a silence they cannot.
        foreach (var contested in announceable.Contested)
        {
            await output.WriteLineAsync(
                $"  {contested}: claimed by more than one catalogued workspace, so nothing answers to it "
                + "here. Enrol one of them under another name, or re-mint its identity.")
                .ConfigureAwait(false);
        }

        foreach (var unread in announceable.Unread)
        {
            await output.WriteLineAsync(
                $"  {unread.Workspace}: its launch configuration could not be read, so nothing it answers "
                + $"to is offered. {unread.Problem}")
                .ConfigureAwait(false);
        }

        foreach (var strange in catalogue.Unreadable)
        {
            await output.WriteLineAsync(
                $"  {strange}: not an agent identity, so this catalogue entry is ignored. Re-enrol that "
                + "repository to give it one.")
                .ConfigureAwait(false);
        }

        // By name, or by identity for an agent whose repository was renamed since it registered: the broker holds the old
        // name until it attaches under the new one, and the checkout is startable all the while.
        var unstartable = joined.Agents
            .Where(a => !announceable.Names.Contains(a.Split('@')[0], StringComparer.Ordinal)
                && !(joined.Identities.TryGetValue(a, out var written)
                     && AgentIdentity.TryRead(written, out var held)
                     && catalogue.Identities.Contains(held)))
            .ToArray();

        if (unstartable.Length > 0)
        {
            await output.WriteLineAsync(
                $"  Registered here but not in the catalogue: {string.Join(", ", unstartable)}. " +
                "A request to one of those waits, flagged, until somebody opens that repository.").ConfigureAwait(false);
        }
    }

    /// <summary>Finds the checkout a command means: by the identity the broker sent, else by the name.</summary>
    /// <param name="catalogue">This machine's catalogue.</param>
    /// <param name="project">The project the node loop serves, or null for any.</param>
    /// <param name="agent">The name the broker holds the agent under.</param>
    /// <param name="presented">The identity the broker sent, or empty when it knew none.</param>
    /// <param name="identity">Which checkout, when this returns true.</param>
    /// <param name="contested">The workspaces that both claim the name, when the name was what decided it.</param>
    /// <returns><see langword="true"/> when exactly one catalogued checkout is meant.</returns>
    /// <remarks>
    /// <b>The identity wins and the name is the fallback</b>, because the identity is what the agent is and the name is
    /// only what this project shows for it (SPEC.md §4.1.1): a repository renamed in its launch configuration is still
    /// the checkout the broker means by its old name. An identity this catalogue does not hold is not an error, since
    /// the broker may have it from another machine's checkout of the repository; the name is tried instead.
    /// </remarks>
    private static bool Resolve(
        NodeCatalogue catalogue,
        string? project,
        AgentName agent,
        string? presented,
        out AgentIdentity identity,
        out IReadOnlyList<string> contested)
    {
        if (AgentIdentity.TryRead(presented, out var named) && catalogue.Identities.Contains(named))
        {
            identity = named;
            contested = [];

            return true;
        }

        return catalogue.TryResolve(agent.Role, project, out identity, out contested);
    }

    /// <summary>Starts an agent and gives it a turn.</summary>
    /// <param name="start">What to start, and why.</param>
    /// <param name="catalogue">What this machine can start.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="residents">The agents already running here.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <param name="reporter">Tells the broker when the turn begins and ends (SPEC.md §11.6), or null to report nothing.</param>
    /// <returns>A task that completes when the turn has been taken.</returns>
    /// <remarks>
    /// <para>
    /// The turn is what makes starting worth anything. A process that launched and sat there
    /// would have attached, drained nothing, and done nothing — SPEC.md §4.2 is clear that
    /// waking an adapter is not the same as the model taking a turn, and the model is what
    /// does the work.
    /// </para>
    /// <para>
    /// Failures are reported and swallowed. A repository that has moved, a command that is not
    /// installed, an agent that dies on startup: none of them is a reason to take the whole
    /// machine off the air, and the Request that prompted this stays open and is flagged as not
    /// collected, which is how the person finds out.
    /// </para>
    /// </remarks>
    internal static async Task StartAsync(
        Wire.StartAgent start,
        NodeCatalogue catalogue,
        string? project,
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken,
        TurnReporter? reporter = null)
    {
        if (!AgentName.TryCreate(start.Agent, out var agent))
        {
            return;
        }

        // **A name is what a project calls an agent; the thing that starts is the identity behind it.**
        // Two workspaces claiming one name resolve to neither, because starting either would be
        // guessing at identity — and the name is the one thing Parley makes unforgeable.
        if (!Resolve(catalogue, project, agent, start.Identity, out var identity, out var contested))
        {
            await output.WriteLineAsync(
                contested.Count > 1
                    ? $"  {agent.Value}: two catalogued workspaces claim that name, so neither was "
                      + $"started: {string.Join(", ", contested)}."
                    : $"  {agent.Value}: asked for, but this machine has no way to start it.")
                .ConfigureAwait(false);

            return;
        }

        // **Looking and starting are one act.** `TryGetValue` followed later by an assignment is a
        // check-then-act, and this table is shared across every project one process hosts — so two
        // projects' node loops reaching here for one identity at the same moment would both miss and
        // both launch, and `residents[identity] = resident` would overwrite the loser, leaving a process
        // outside the table `StopEveryoneAsync` walks.
        //
        // One semaphore for the whole process rather than one per identity: starting is rare, it is already
        // slower than any lock, and a table shared by every project wants a guard with the same reach.
        await Residency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // **A node that has swept starts nothing more.** Reached by a start that was queued on the
            // gate while the sweep held it: launching now would put a process into a table nothing will
            // walk again, and the sweep is the last thing the process does.
            if (Shut.TryGetValue(residents, out _))
            {
                await output.WriteLineAsync(
                    $"  {agent.Value}: not started — this node is stopping.").ConfigureAwait(false);

                return;
            }

            var replacing = false;

            if (residents.TryGetValue(identity, out var existing))
            {
                if (existing.IsAlive)
                {
                    // Already here, and "here" is machine-wide rather than this node's own. This is also how
                    // one checkout stays one process when a second project asks for it under another name.
                    DispatchTurn(existing, output, cancellationToken, reporter);
                    return;
                }

                // **Ours, and dead — so the corpse is disposed rather than overwritten.** `TurnAsync` takes
                // exactly this care and says why: the object holding a departed process still owns a Process
                // handle and the two pumps draining its output, so letting the reference fall out of the
                // dictionary leaks both, once per agent that ever dies. The assignment below is what would
                // have dropped it.
                if (residents.TryRemove(identity, out var corpse))
                {
                    await corpse.DisposeAsync().ConfigureAwait(false);
                }

                replacing = true;
            }

            if (catalogue.CommandFor(identity) is not { } command)
            {
                await output.WriteLineAsync(
                    $"  {agent.Value}: asked for, but this machine has no way to start it.")
                    .ConfigureAwait(false);

                return;
            }

            // **A workspace that is gone retires its agent, and only when the folder above it is still there** (SPEC.md
            // §14): a deleted repository is worth a judgement, a drive briefly unmounted is not, and one unavailable volume
            // must not retire every agent on it at once. The node looks because the broker never reads this disk.
            if (catalogue.WorkspaceFor(identity) is { } catalogued
                && !string.IsNullOrWhiteSpace(catalogued)
                && !Directory.Exists(catalogued))
            {
                await WorkspaceGoneAsync(agent, catalogued, output, reporter).ConfigureAwait(false);

                return;
            }

            try
            {
                var invocation = InvocationCommand.Parse(command);
                var workspace = catalogue.WorkspaceFor(identity) ?? Directory.GetCurrentDirectory();

                // Named to the broker first, so the process it starts is known as Parley's (SPEC.md §11.7).
                var launched = reporter is null ? null : await reporter.LaunchAsync(agent).ConfigureAwait(false);
                var resident = ResidentAgent.Start(agent, invocation, workspace, TimeProvider.System, launched);
                residents[identity] = resident;

                // **A replacement says so, and that is not decoration.** Starting an agent because nothing
                // was running is an ordinary event; starting one because the process behind an attached
                // session had gone is an anomaly worth a line, and `TurnAsync` already names the same state
                // where it meets it. Reading a node's log, the two are completely different pieces of news.
                await output.WriteLineAsync(
                    replacing
                        ? $"  {agent.Value}: restarted ({start.Because}) — the previous process had gone."
                        : $"  {agent.Value}: started ({start.Because}).").ConfigureAwait(false);

                DispatchTurn(resident, output, cancellationToken, reporter);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync($"  {agent.Value}: could not start — {exception.Message}")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _ = Residency.Release();
        }
    }

    /// <summary>Says an agent's workspace is missing, and asks the broker to retire it when it is on the disk holding the person's home folder, with the folder above it still there, in use, no mount folder and reached through no link.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="workspace">Its catalogued workspace, which does not exist.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="reporter">The broker's ear, or null when there is none.</param>
    /// <returns>A task that completes when it is said.</returns>
    private static async Task WorkspaceGoneAsync(AgentName agent, string workspace, TextWriter output, TurnReporter? reporter)
    {
        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        }
        catch (Exception unreadable) when (unreadable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not started — its workspace '{workspace}' is not a path this machine can read, so "
                + "nothing is concluded.").ConfigureAwait(false);

            return;
        }

        var parent = Path.GetDirectoryName(full);

        if (parent is null || !Directory.Exists(parent))
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not started — its workspace {workspace} and the folder above it are both missing, "
                + "so nothing is concluded (a drive may be unmounted); it is tried again when next asked for.")
                .ConfigureAwait(false);

            return;
        }

        if (ThroughALink(full))
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not started — its workspace {workspace} is missing, and its path runs through a link, "
                + "whose far side may be a drive not mounted, so nothing is concluded; it is tried again when next asked for.")
                .ConfigureAwait(false);

            return;
        }

        // **Only on the disk that holds the person's home folder, and never at the root of a volume** (the owner's calls,
        // 2026-10-30). Elsewhere another disk can answer at the same path — a swapped removable drive, a disk with the same
        // label, a backup laid out the same way — and a folder that is there and in use says nothing about whether it is
        // the same disk (lens 1 run 89). The home disk cannot be swapped while the node runs, but its mount folders can
        // hold whichever disk is plugged in (lens 1 run 88). A repository missing from anywhere else is left for a person.
        if (!MayRetireUnder(parent))
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not started — its workspace {workspace} is missing from a disk other than the one holding "
                + "your home folder, or from where removable disks are mounted, so a different disk may be answering and "
                + "nothing is concluded; it is tried again when next asked for.")
                .ConfigureAwait(false);

            return;
        }

        // **The folder above must be one somebody is using, not one a volume left behind.** An unmounted Linux volume leaves
        // its mount folder in place and empty, and a mount point is no link, so an empty parent concludes nothing (lens 1
        // run 87); a repository that was the only thing in its folder is then left for a person to retire.
        if (HoldsNothing(parent))
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not started — its workspace {workspace} is missing and the folder above it holds "
                + "nothing else, as a volume not mounted leaves it, so nothing is concluded; it is tried again when next "
                + "asked for.")
                .ConfigureAwait(false);

            return;
        }

        // Looked at once more: a volume that came back while the checks ran shows its workspace again (lens 1 run 88).
        if (Directory.Exists(full))
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not started — its workspace {workspace} came back while it was being looked at; it is "
                + "started when next asked for.").ConfigureAwait(false);

            return;
        }

        var retired = reporter is null ? null : await reporter.WorkspaceGoneAsync(agent).ConfigureAwait(false);

        await output.WriteLineAsync(
            retired == true
                ? $"  {agent.Value}: not started — its workspace {workspace} is gone, so the broker retired it. Reinstate "
                  + "it in the terminal once the repository is back."
                : $"  {agent.Value}: not started — its workspace {workspace} is gone.")
            .ConfigureAwait(false);
    }

    /// <summary>Whether a missing workspace directly in this folder may retire its agent, as far as disks go.</summary>
    /// <param name="folder">The folder above the workspace, which exists.</param>
    /// <returns>True only on the home volume, and not at a volume's root or in a folder removable disks are mounted in.</returns>
    internal static bool MayRetireUnder(string folder) => OnTheHomeVolume(folder) && !AtAVolumeRoot(folder);

    /// <summary>Whether a folder that is there is on the volume holding the person's home folder.</summary>
    /// <param name="folder">A full path to a folder that exists.</param>
    /// <returns>True when it is; false when it is not, or when that cannot be told.</returns>
    /// <remarks>
    /// Windows compares drive roots with the profile's. Linux finds each path's mount point in the process's mount table, so
    /// a disk mounted anywhere is told from the one under the home folder. macOS puts every other disk in <c>/Volumes</c>.
    /// </remarks>
    internal static bool OnTheHomeVolume(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(home))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(folder)),
                Path.GetPathRoot(Path.GetFullPath(home)),
                StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsMacOS())
        {
            return !IsUnder(folder, "/Volumes") && !IsUnder(home, "/Volumes");
        }

        string[] mounts;

        try
        {
            mounts = File.ReadAllLines("/proc/self/mountinfo");
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return MountPointOf(folder, mounts) is { } here
               && string.Equals(here, MountPointOf(home, mounts), StringComparison.Ordinal);
    }

    /// <summary>The mount point a path lies under, from the lines of a Linux mount table.</summary>
    /// <param name="path">A full path.</param>
    /// <param name="mountInfo">The lines of <c>/proc/self/mountinfo</c>.</param>
    /// <returns>The longest mount point the path lies under, or null when none is listed.</returns>
    /// <remarks>The fifth field is the mount point, with a space, tab, newline or backslash written as an octal escape.</remarks>
    internal static string? MountPointOf(string path, IEnumerable<string> mountInfo)
    {
        string? best = null;

        foreach (var line in mountInfo)
        {
            var fields = line.Split(' ');

            if (fields.Length < 5)
            {
                continue;
            }

            var point = Unescape(fields[4]);

            if (IsUnder(path, point) && (best is null || point.Length > best.Length))
            {
                best = point;
            }
        }

        return best;
    }

    private static bool IsUnder(string path, string point) =>
        point == "/"
        || string.Equals(path, point, StringComparison.Ordinal)
        || path.StartsWith(point.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static string Unescape(string field) =>
        System.Text.RegularExpressions.Regex.Replace(
            field,
            @"\\([0-7]{3})",
            match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString(),
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

    /// <summary>Whether a folder is a drive's root, or a folder removable volumes are mounted directly in.</summary>
    /// <param name="folder">The folder above a workspace, as a full path.</param>
    /// <returns>True when a workspace directly in it may be a volume's root, or sit on a volume another disk can replace.</returns>
    /// <remarks>
    /// A drive's root (<c>E:\</c>, <c>/</c>, a share's root); <c>/Volumes</c> on macOS; <c>/mnt</c>, <c>/media</c>, and a
    /// user's folder in <c>/media</c> or <c>/run/media</c>, where a desktop mounts each removable disk under its label.
    /// </remarks>
    internal static bool AtAVolumeRoot(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);

        if (string.Equals(trimmed, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(folder) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var unix = trimmed.Replace(Path.DirectorySeparatorChar, '/');
        var above = unix.LastIndexOf('/') is var cut and > 0 ? unix[..cut] : null;

        return unix is "/Volumes" or "/mnt" or "/media" or "/run/media"
               || above is "/media" or "/run/media";
    }

    /// <summary>Whether a folder holds nothing, or cannot be listed.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>True when it is empty or unreadable; either way it is no evidence a repository was deleted from it.</returns>
    private static bool HoldsNothing(string folder)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether any part of a path that is there, from its root down, is a link: a junction or a symbolic link.</summary>
    /// <param name="path">A full path.</param>
    /// <returns>True when a link is on the way, including the path itself as a link whose target is gone.</returns>
    /// <remarks>
    /// <b>A link outlives what it leads to.</b> A folder above the workspace that is a junction to a second disk still exists
    /// when that disk is gone, so the parent check alone would retire every agent on the disk at once (found by review,
    /// lens 2 run 89), and a workspace that is itself a link to an unmounted drive reads as missing beside a parent that is
    /// there. Either way the path says nothing about whether the repository was deleted, so nothing is concluded from it.
    /// </remarks>
    private static bool ThroughALink(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var at = root;

        foreach (var part in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            var entry = new DirectoryInfo(at);

            if (entry.LinkTarget is not null)
            {
                return true;
            }

            if (!entry.Exists)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Serialises every change to who lives in the resident table.</summary>
    /// <remarks>
    /// <para>
    /// Two callers take it. A start, so that looking for a resident and creating one are one act, since a check-then-act would let two projects each launch one checkout. And the
    /// sweep, so that a start cannot record a process into a table the sweep has already walked and leave it running
    /// with nothing remaining that would stop it.
    /// </para>
    /// <para>
    /// <b>It is named for the table, not for either act.</b> A lock named for one of the two things it
    /// serialises is how the next reader concludes the other one is unguarded.
    /// </para>
    /// <para>
    /// Process-wide, because the table it protects is: every project shares one resident table so that a
    /// checkout answering in two projects is one process. A guard narrower than the thing it guards would
    /// leave a gap.
    /// </para>
    /// </remarks>
    private static readonly SemaphoreSlim Residency = new(1, 1);

    /// <summary>Gives a turn to an agent this node already holds.</summary>
    /// <param name="turn">Which agent, and why.</param>
    /// <param name="residents">The agents already running here.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="catalogue">What this machine can start, which is what resolves a name.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <param name="reporter">Tells the broker when the turn begins and ends (SPEC.md §11.6), or null to report nothing.</param>
    /// <returns>A task that completes when the turn has been taken.</returns>
    /// <remarks>
    /// <para>
    /// Silent when the agent is not one of this node's residents, and that silence is the point
    /// rather than an omission. An agent can be attached through a session this machine did not
    /// start — somebody's editor window — and there is nothing a node can do to make one take a
    /// turn. Saying so on every question would print a line per message for an agent that is
    /// working perfectly well, driven by the person sitting in front of it.
    /// </para>
    /// <para>
    /// A resident of ours that has died is a different case and is not silent. It means the broker
    /// believes a session is attached while the process behind it has gone, and dropping the
    /// question there would reproduce, in a smaller window, the silence this command exists to
    /// fix.
    /// </para>
    /// <para>
    /// The turn itself is serialised per resident by <see cref="ResidentAgent"/>, so a burst of
    /// questions queues rather than arriving at one agent at once.
    /// </para>
    /// </remarks>
    internal static async Task TurnAsync(
        Wire.GiveTurn turn,
        NodeCatalogue catalogue,
        string? project,
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken,
        TurnReporter? reporter = null)
    {
        if (!AgentName.TryCreate(turn.Agent, out var agent))
        {
            return;
        }

        // The name resolves to the identity the resident is held under, which may be a process another
        // project's node started under a different name. That is the point of sharing the table.
        if (!Resolve(catalogue, project, agent, turn.Identity, out var identity, out _)
            || !residents.TryGetValue(identity, out var resident))
        {
            // Not one of ours. Silent, and common: an agent attached through somebody's editor
            // window is working perfectly well, driven by the person in front of it.
            return;
        }

        if (!resident.IsAlive)
        {
            // Ours, and dead. Rare, and not the same thing at all — the broker believes a session
            // is attached and the process behind it has gone, which is the window between a
            // resident dying and the broker noticing the stream close. Saying nothing would drop
            // this question exactly the way the silence this command exists to fix dropped the
            // last one, so the corpse is cleared and the anomaly is named. The broker's next
            // question arrives as a start, because by then nothing is attached.
            if (residents.TryRemove(identity, out var corpse))
            {
                // Disposed rather than dropped. The process has gone but the object holding it
                // has not: it owns a Process handle and the two pumps draining that process's
                // output, and ShutDownAsync is careful about exactly this. Letting the reference fall
                // out of the dictionary would leak both, once per agent that ever dies.
                await corpse.DisposeAsync().ConfigureAwait(false);
            }

            await output.WriteLineAsync(
                $"  {agent.Value}: asked for a turn, but its process has gone. Cleared; the next "
                + "question will start it.").ConfigureAwait(false);

            return;
        }

        // Reported like any other turn once it is actually given. Only the "not mine" case is
        // silent; a turn that fails is a turn the operator needs to hear about.
        DispatchTurn(resident, output, cancellationToken, reporter);
    }

    /// <summary>Starts a turn without holding up the command stream.</summary>
    /// <param name="resident">The agent to give a turn.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <param name="reporter">Tells the broker when the turn begins and ends (SPEC.md §11.6), or null to report nothing.</param>
    /// <remarks>
    /// <para>
    /// A turn is a model taking a turn and can run for minutes. Awaiting one inside the command
    /// loop makes the whole machine single-file: while one agent thinks, a question for a
    /// different agent waits behind it, and on a machine hosting nineteen agents that is the
    /// ordinary case rather than the unlucky one. Every question to a running agent asks for a turn,
    /// so this is the common path.
    /// </para>
    /// <para>
    /// Starting a process deliberately stays inside the loop. Two starts racing for one agent
    /// would produce the duplicate process the design refuses everywhere else, and serialising
    /// them is what makes the check-then-add on the resident table safe. Launching is quick; it is
    /// the turn afterwards that is slow, and only that is let go.
    /// </para>
    /// <para>
    /// Left unobserved on purpose: <see cref="GiveTurnAsync"/> reports its own failures and lets
    /// nothing escape but cancellation, which arrives when the node is stopping and has nowhere
    /// useful to be reported to. Turns against one agent are still serialised, by that agent's own
    /// lock rather than by this loop.
    /// </para>
    /// </remarks>
    private static void DispatchTurn(
        ResidentAgent resident, TextWriter output, CancellationToken cancellationToken, TurnReporter? reporter = null)
    {
        _ = Observe(GiveTurnAsync(resident, output, cancellationToken, reporter));

#pragma warning disable CA1031 // Do not catch general exception types
        static async Task Observe(Task turn)
        {
            try
            {
                await turn.ConfigureAwait(false);
            }
            catch
            {
                // Total on purpose, because this is the boundary where nothing is awaiting.
                //
                // GiveTurnAsync handles its own faults and lets cancellation through, which alone
                // would argue for catching only that. But the way it handles a fault is to write a
                // line, and a write can fail too — a closed stream on the way down, an I/O error on
                // a redirected one. An exception escaping here has nowhere to go: it surfaces later
                // as an unobserved task exception, attributed to no agent and no command, on a path
                // where the useful information was already reported by the line that failed.
                //
                // A node must not be taken off the air by the reporting of one agent's turn.
            }
        }
#pragma warning restore CA1031
    }

    /// <summary>Gives a resident agent a turn.</summary>
    /// <param name="resident">The agent.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <param name="reporter">Tells the broker when the turn begins and ends (SPEC.md §11.6), or null to report nothing.</param>
    /// <returns>A task that completes when the turn ends.</returns>
    private static async Task GiveTurnAsync(
        ResidentAgent resident, TextWriter output, CancellationToken cancellationToken, TurnReporter? reporter = null)
    {
        try
        {
            // Reported to the broker when there is a reporter (SPEC.md §11.6), so a surface can tell this turn running from
            // it having ended — and see what it was refused.
            var outcome = await TurnReporter.Run(
                    reporter,
                    resident,
                    began => resident.TakeTurnAsync(InvocationCommand.Prompt, TurnTimeout, began, cancellationToken))
                .ConfigureAwait(false);

            if (!outcome.Succeeded)
            {
                // **How it ended, never what was said.** The record's default text includes Reply — the
                // agent's own words, which TurnOutcome keeps out of every record on purpose — and the broker's log
                // is never pruned and a line break in them would forge a line of it.
                await output.WriteLineAsync(
                        $"  {resident.Name.Value}: turn ended without finishing — {outcome.Describe()} after " +
                        $"{outcome.Elapsed.TotalSeconds:0}s, {outcome.PermissionDenials} permission(s) refused" +
                        WhyItFailed(outcome))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await output.WriteLineAsync($"  {resident.Name.Value}: turn failed — {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>The runner's own label for how an error turn ended, when it is a plain identifier.</summary>
    /// <param name="outcome">How the turn ended.</param>
    /// <returns>A comma and the label, or nothing when there is none or it is anything but a short identifier.</returns>
    /// <remarks>
    /// <b>The log names the outcome and, when the runner gave one, its label.</b> The runner's <c>result</c> text stays out,
    /// because a peer can steer what a model says; what is added is the <c>subtype</c> (<c>error_max_turns</c>,
    /// <c>error_during_execution</c>), which is the runner's vocabulary and not words, and is admitted only when it looks
    /// like one.
    /// </remarks>
    internal static string WhyItFailed(TurnOutcome outcome) =>
        outcome.IsError && outcome.StopReason is { Length: > 0 and <= 64 } label && label.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? ", reason " + label
            : string.Empty;

    /// <summary>
    /// Restarts an agent this node started, once its turn is over, so it reads its launch configuration afresh (SPEC.md
    /// §11.1's fourth way a resident ends).
    /// </summary>
    /// <param name="refresh">Which agent, and why.</param>
    /// <param name="catalogue">What this machine can start, which is what resolves a name.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="residents">The agents running here.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the node, and with it the wait.</param>
    /// <param name="reporter">Reports the restarted agent's first turn to the broker, when there is a broker to tell.</param>
    /// <returns>A task that completes once the refresh has been handed off.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only an agent this node started.</b> One attached from somebody's editor window is not this machine's to stop,
    /// and it is told so in the log rather than stopped: its line waits for its next start on its own.
    /// </para>
    /// <para>
    /// <b>The wait for the turn happens off this loop</b>, as a turn does (<see cref="DispatchTurn"/>). A refresh waiting
    /// inline for a long turn would hold every other command to this node behind it — a start or another
    /// agent's turn. Replacing the process happens under <see cref="Residency"/>, as a start does, so a start racing it
    /// cannot leave two processes for one checkout.
    /// </para>
    /// </remarks>
    internal static async Task RefreshAsync(
        Wire.RefreshAgent refresh,
        NodeCatalogue catalogue,
        string? project,
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken,
        TurnReporter? reporter = null)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(residents);
        ArgumentNullException.ThrowIfNull(output);

        if (!AgentName.TryCreate(refresh.Agent, out var agent)
            || !Resolve(catalogue, project, agent, refresh.Identity, out var identity, out _))
        {
            return;
        }

        if (!residents.TryGetValue(identity, out var resident) || !resident.IsAlive)
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: not refreshed — this node is not running it, so it reads its new ownership line " +
                "the next time it starts.").ConfigureAwait(false);

            return;
        }

        _ = Observed(RefreshWhenIdleAsync(
            agent, identity, resident, catalogue, residents, output, cancellationToken, reporter));

#pragma warning disable CA1031 // Do not catch general exception types
        static async Task Observed(Task refreshing)
        {
            try
            {
                await refreshing.ConfigureAwait(false);
            }
            catch
            {
                // The same boundary DispatchTurn's Observe is, for the same reason: nothing awaits this, and a node must
                // not be taken off the air by the reporting of one agent's refresh.
            }
        }
#pragma warning restore CA1031
    }

    /// <summary>Waits for an agent's turn to end, stops it, and starts it again.</summary>
    /// <returns>A task that completes when the new process is running, or the refresh was abandoned.</returns>
    private static async Task RefreshWhenIdleAsync(
        AgentName agent,
        AgentIdentity identity,
        ResidentAgent resident,
        NodeCatalogue catalogue,
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken,
        TurnReporter? reporter)
    {
        _ = await resident.ShutdownWhenIdleAsync(ShutdownGrace, cancellationToken).ConfigureAwait(false);

        await Residency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A start that got here first found the process gone and replaced it, which already reads the launch
            // configuration afresh — so there is nothing left to do, and the corpse is the start's to dispose.
            if (!residents.TryGetValue(identity, out var current) || !ReferenceEquals(current, resident))
            {
                return;
            }

            _ = residents.TryRemove(identity, out _);
            await resident.DisposeAsync().ConfigureAwait(false);

            // A node that has swept starts nothing more (see StartAsync).
            if (Shut.TryGetValue(residents, out _))
            {
                await output.WriteLineAsync($"  {agent.Value}: stopped, and not restarted — this node is stopping.")
                    .ConfigureAwait(false);

                return;
            }

            if (catalogue.CommandFor(identity) is not { } command)
            {
                await output.WriteLineAsync(
                    $"  {agent.Value}: stopped for a refresh, and this machine no longer has a way to start it.")
                    .ConfigureAwait(false);

                return;
            }

            var launched = reporter is null ? null : await reporter.LaunchAsync(agent).ConfigureAwait(false);
            var restarted = ResidentAgent.Start(
                agent,
                InvocationCommand.Parse(command),
                catalogue.WorkspaceFor(identity) ?? Directory.GetCurrentDirectory(),
                TimeProvider.System,
                launched);

            residents[identity] = restarted;

            await output.WriteLineAsync(
                $"  {agent.Value}: restarted once its turn was over, so it reads its new ownership line.")
                .ConfigureAwait(false);

            // **And given a turn, as a fresh start is** (StartAsync). A turn asked for while the old process was stopping
            // queued on it and came back died, and the broker's wake stays claimed until a drain — so without this the
            // restarted agent would sit on mail nothing asks it to read.
            DispatchTurn(restarted, output, cancellationToken, reporter);
        }
        finally
        {
            _ = Residency.Release();
        }
    }

    /// <summary>Ends one resident and says so.</summary>
    /// <param name="resident">The agent.</param>
    /// <param name="named">What to call it in the report.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>When it has gone.</returns>
    private static async Task ShutDownAsync(ResidentAgent resident, string named, TextWriter output)
    {
        _ = await resident.ShutdownAsync(ShutdownGrace).ConfigureAwait(false);
        await resident.DisposeAsync().ConfigureAwait(false);

        await output.WriteLineAsync($"  {named}: stopped.").ConfigureAwait(false);
    }

    /// <summary>Stops every agent this node started.</summary>
    /// <param name="residents">The agents running here.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>A task that completes when they have all stopped.</returns>
    /// <remarks>
    /// SPEC.md §11.1 gives a resident agent three ways to end, and this is one of them: the
    /// supervisor that started it telling it to stop. Leaving them running would be worse than
    /// untidy — they would hold sessions the broker still counts, and nothing would be left that
    /// could stop them.
    /// </remarks>
    internal static async Task StopEveryoneAsync(
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(residents);

        // **The sweep takes the same gate a start does, and shuts the table afterwards.** Recording a
        // resident happens inside `Residency`, so without this a start already past that gate lands its
        // process in the table *after* the sweep has walked it — and this is the last thing the process
        // does, so nothing sweeps again. That is an agent still running with nothing left that would stop
        // it, which breaks the promise SPEC.md §12 makes: stopping a node tells every agent it started
        // to quit.
        await Residency.WaitAsync().ConfigureAwait(false);

        try
        {
            // Shut before sweeping rather than after, so a start still queued on the gate finds the table
            // closed when it gets in, instead of launching a process into one nobody will walk again.
            Shut.AddOrUpdate(residents, Sentinel);

            foreach (var identity in residents.Keys.ToArray())
            {
                if (!residents.TryRemove(identity, out var resident))
                {
                    continue;
                }

                await ShutDownAsync(resident, identity.ToString(), output).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = Residency.Release();
        }
    }

    /// <summary>The tables that have been swept, so that nothing starts into one afterwards.</summary>
    /// <remarks>
    /// Keyed by the table rather than held as one flag for the process, because a flag would be a fact
    /// about whichever node stopped last — one process has a table shared by every project it
    /// hosts, while a test host has many tables and no shutdown between them. Weak, so a table that goes
    /// out of scope takes its entry with it.
    /// </remarks>
    private static readonly ConditionalWeakTable<
        ConcurrentDictionary<AgentIdentity, ResidentAgent>, object> Shut = new();

    /// <summary>What <see cref="Shut"/> records; its presence is the whole of the meaning.</summary>
    private static readonly object Sentinel = new();

    /// <summary>
    /// What this machine can observe about itself.
    /// </summary>
    /// <returns>The facts, for an operator reading a fleet listing.</returns>
    /// <remarks>
    /// Reported and used for nothing. §4 has a machine reached through the connection its node
    /// already holds and never by an address the broker dials, so none of this is a route in —
    /// it is what somebody reading a listing needs in order to know which box they are looking
    /// at. Recording an address the broker then connected to would invert §13's trust direction,
    /// letting a registration tell the broker where to send traffic.
    /// </remarks>
    private static Wire.NodeFacts Observe()
    {
        var facts = new Wire.NodeFacts
        {
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.Version.ToString()
        };

        try
        {
            var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

            facts.Domain = properties.DomainName ?? string.Empty;

            facts.IpAddress = System.Net.Dns.GetHostAddresses(properties.HostName)
                .FirstOrDefault(a =>
                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString() ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort, deliberately. Every one of these is decoration on an operator's
            // listing, and a machine with no network configuration a .NET API can read is still
            // a machine that can host agents — refusing to join over it would take a working box
            // off the air to protect a column.
        }

        return facts;
    }
}
