using System.Collections.Concurrent;
using System.Globalization;
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
/// <b>It replaced a supervisor that polled a database.</b> The old one opened the store, looked
/// for agents with work waiting, and started those it had a command for. Every part of that is
/// now somebody else's: the broker owns the store and knows what is waiting, so it says so — and
/// what this contributes is the two things only a process on this machine can, which are being
/// evidence the machine is up and being able to start something on it.
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
    /// time would ignore <c>--node-config</c> — which is what it used to do.
    /// </para>
    /// <para>
    /// <b>The file is watched, and a change to it rejoins.</b> The roles a node can start are sent
    /// once, in the join request, and a role added to the file afterwards used to be announced
    /// never — so an agent enrolled under a running node read as <c>startable</c> and could not be
    /// started, which is exactly the misleading state SPEC.md §17 question 13 describes. Now the
    /// roles are compared every <see cref="ServeCommand.WatchInterval"/> and a difference ends the
    /// stream and joins again with the new set.
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

        // **Handed in rather than made here, and keyed by identity** (018 R5). One process hosts a node per
        // broker it joins, and each used to keep its own table as a local — so two projects asking for one
        // checkout started two processes, which is the hazard the per-project catalogue was partitioned to
        // avoid. Shared and keyed by what a checkout *is*, two asks find one process.
        // **Whoever makes this table is whoever stops it**, and the review gate found the other rule was
        // machine-wide harm. The sweep below used to run whenever *any* node loop returned — and 018 made
        // this table shared across every project one process hosts, so one project stopping swept every
        // agent on the machine, including the ones still serving other projects. `serve` stops and restarts
        // a project whose store or endpoint changed, and `project remove` ends one outright: either of them
        // silently took down every agent Parley was running. SPEC.md §12 says what that costs — the process
        // is what held everything each agent had learned.
        //
        // 018 already fixed one instance of this shape, where recording a catalogue on a project restarted
        // the broker and dropped every agent attached to it. This is the same class, one level down.
        var ours = residents is null;

        residents ??= new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        // Turns now run concurrently and each reports its own outcome, so more than one of them can
        // be writing at once. A TextWriter is not thread-safe, and the callers here supply whatever
        // they have — one passes Console.Out, which is synchronised, and one passes a writer that
        // may not be. Wrapping once at the entry point makes that not a question the callers have
        // to have got right.
        output = TextWriter.Synchronized(output);

        try
        {
            while (true)
            {
                var request = new Wire.JoinRequest
                {
                    Machine = NodeCatalogue.Machine,
                    Facts = Observe()
                };

                // **What this node can start for this project, which the repositories decide** (018 R3).
                // A catalogue entry says how to start a checkout and nothing about projects; each catalogued
                // workspace declares what it answers to, and a name two of them claim is announced by
                // neither.
                var announceable = catalogue.Announceable(project);

                request.StartableRoles.AddRange(announceable.Names);

                // Two sources, and they are not the same question. `watching` stops the poll when
                // this attempt is over for any reason; `rejoining` is cancelled only by the poll,
                // to end a stream whose announced roles have gone stale. Collapsing them would
                // make a broker that hung up look like a catalogue that changed.
                using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var rejoining = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var watcher = WatchAsync(
                    catalogueFile, project, announceable.Names, output, rejoining, watching.Token);

                try
                {
                    using var stream = nodes.Join(request, cancellationToken: rejoining.Token);

                    await foreach (var command in stream.ResponseStream
                        .ReadAllAsync(rejoining.Token).ConfigureAwait(false))
                    {
                        switch (command.CommandCase)
                        {
                            case Wire.NodeCommand.CommandOneofCase.Joined:
                                await ReportJoinedAsync(
                                    command.Joined, catalogue, project, catalogueFile, endpoint, output)
                                    .ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Start:
                                await StartAsync(command.Start, catalogue, project, residents, output,
                                    cancellationToken).ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Turn:
                                await TurnAsync(command.Turn, catalogue, project, residents, output, cancellationToken)
                                    .ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Instruct:
                                await InstructAsync(
                                    command.Instruct, nodes, request.Machine, catalogue, project, residents,
                                    output, cancellationToken)
                                    .ConfigureAwait(false);
                                break;

                            case Wire.NodeCommand.CommandOneofCase.Stop:
                                await StopAsync(command.Stop.Agent, catalogue, project, residents, output)
                                    .ConfigureAwait(false);
                                break;

                            default:
                                // A command this build does not know about. Ignored rather than
                                // fatal: the broker may be newer, and a node that fell over on an
                                // unrecognised instruction would take a machine off the air over a
                                // feature it simply does not participate in.
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
                    // Cancelled, and a version that caught only OperationCanceledException took
                    // the whole node down every time somebody added an agent — reported as "this
                    // machine's node stopped", which reads as the broker's doing.
                }
                finally
                {
                    await watching.CancelAsync().ConfigureAwait(false);
                }

                var changed = await watcher.ConfigureAwait(false);

                if (changed is null)
                {
                    // Either the person stopped this, or the broker ended the stream. Both mean
                    // the node is done; neither is a reason to reconnect in a loop.
                    break;
                }

                catalogue = changed;

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
    /// like a machine nobody has listed any agents on yet. That is the shape of every silent
    /// defect this area has produced.
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

        var unstartable = joined.Agents
            .Where(a => !announceable.Names.Contains(a.Split('@')[0], StringComparer.Ordinal))
            .ToArray();

        if (unstartable.Length > 0)
        {
            await output.WriteLineAsync(
                $"  Registered here but not in the catalogue: {string.Join(", ", unstartable)}. " +
                "A request to one of those waits, flagged, until somebody opens that repository.").ConfigureAwait(false);
        }
    }

    /// <summary>Starts an agent and gives it a turn.</summary>
    /// <param name="start">What to start, and why.</param>
    /// <param name="catalogue">What this machine can start.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="residents">The agents already running here.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
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
        CancellationToken cancellationToken)
    {
        if (!AgentName.TryCreate(start.Agent, out var agent))
        {
            return;
        }

        // **A name is what a project calls an agent; the thing that starts is the identity behind it**
        // (018). Two workspaces claiming one name resolve to neither, because starting either would be
        // guessing at identity — and the name is the one thing Parley makes unforgeable.
        if (!catalogue.TryResolve(agent.Role, project, out var identity, out var contested))
        {
            await output.WriteLineAsync(
                contested.Count > 1
                    ? $"  {agent.Value}: two catalogued workspaces claim that name, so neither was "
                      + $"started: {string.Join(", ", contested)}."
                    : $"  {agent.Value}: asked for, but this machine has no way to start it.")
                .ConfigureAwait(false);

            return;
        }

        // **Looking and starting are one act, and until the review gate probed it they were not.**
        // `TryGetValue` followed later by an assignment is a check-then-act, and 018 made this table shared
        // across every project one process hosts — so two projects' node loops reach here for one identity
        // at the same moment, both miss, and both launch. Twenty-five concurrent pairs produced **forty-seven
        // processes**: the hazard 009's FR-013 partitioned the catalogues to avoid, arriving by the route the
        // partition's removal opened.
        //
        // The second half was worse than the first. `residents[identity] = resident` overwrote the loser, so
        // the process it had started was no longer in the table `StopEveryoneAsync` walks — an agent nothing
        // would ever stop, accumulating one per race.
        //
        // One semaphore for the whole process rather than one per identity: starting is rare, it is already
        // slower than any lock, and a table shared by every project wants a guard with the same reach.
        await Starting.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var replacing = false;

            if (residents.TryGetValue(identity, out var existing))
            {
                if (existing.IsAlive)
                {
                    // Already here, and "here" is machine-wide rather than this node's own. This is also how
                    // one checkout stays one process when a second project asks for it under another name.
                    DispatchTurn(existing, output, cancellationToken);
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

            try
            {
                var invocation = InvocationCommand.Parse(command);
                var workspace = catalogue.WorkspaceFor(identity) ?? Directory.GetCurrentDirectory();

                var resident = ResidentAgent.Start(agent, invocation, workspace, TimeProvider.System);
                residents[identity] = resident;

                // **A replacement says so, and that is not decoration.** Starting an agent because nothing
                // was running is an ordinary event; starting one because the process behind an attached
                // session had gone is an anomaly worth a line, and `TurnAsync` already names the same state
                // where it meets it. Reading a node's log, the two are completely different pieces of news.
                await output.WriteLineAsync(
                    replacing
                        ? $"  {agent.Value}: restarted ({start.Because}) — the previous process had gone."
                        : $"  {agent.Value}: started ({start.Because}).").ConfigureAwait(false);

                DispatchTurn(resident, output, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync($"  {agent.Value}: could not start — {exception.Message}")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _ = Starting.Release();
        }
    }

    /// <summary>Serialises starting, so that looking for a resident and creating one are one act.</summary>
    /// <remarks>
    /// Process-wide, because the table it protects is: 018 gave every project one resident table so that a
    /// checkout answering in two projects is one process. A guard narrower than the thing it guards would
    /// leave exactly the gap this closes.
    /// </remarks>
    private static readonly SemaphoreSlim Starting = new(1, 1);

    /// <summary>Gives a turn to an agent this node already holds.</summary>
    /// <param name="turn">Which agent, and why.</param>
    /// <param name="residents">The agents already running here.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="catalogue">What this machine can start, which is what resolves a name.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
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
        CancellationToken cancellationToken)
    {
        if (!AgentName.TryCreate(turn.Agent, out var agent))
        {
            return;
        }

        // The name resolves to the identity the resident is held under, which may be a process another
        // project's node started under a different name. That is the point of sharing the table.
        if (!catalogue.TryResolve(agent.Role, project, out var identity, out _)
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
                // output, and StopAsync is careful about exactly this. Letting the reference fall
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
        DispatchTurn(resident, output, cancellationToken);
    }

    /// <summary>Gives an agent the operator's words, and reports what became of them.</summary>
    /// <param name="instruct">Which agent, and what was said.</param>
    /// <param name="nodes">The broker, for the report.</param>
    /// <param name="machine">This machine's name.</param>
    /// <param name="residents">The agents running here.</param>
    /// <param name="output">Where to report locally.</param>
    /// <param name="catalogue">What this machine can start, which is what resolves a name.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <returns>A task that completes when the outcome has been reported.</returns>
    /// <remarks>
    /// <para>
    /// Not dispatched like an ordinary turn, and that is the difference between the two. A turn
    /// says only that there is mail and nobody is waiting on the answer; an instruction has a
    /// person holding a page open, so this runs to its end and reports.
    /// </para>
    /// <para>
    /// The operator's text is the prompt. §4 gives a node ownership of its agents' standard
    /// input, which is the channel a turn already travels down — so an instruction is not a new
    /// route into an agent, it is the existing one carrying one more kind of thing.
    /// </para>
    /// <para>
    /// An agent this node does not hold is reported undeliverable rather than started. Starting
    /// one to deliver a person's words would put the operator's instruction into a session
    /// nobody asked for, and §11.5 says an agent whose node cannot reach it must be told so.
    /// </para>
    /// </remarks>
    private static async Task InstructAsync(
        Wire.DeliverInstruction instruct,
        Wire.Nodes.NodesClient nodes,
        string machine,
        NodeCatalogue catalogue,
        string? project,
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var outcome = Wire.InstructionOutcome.Undeliverable;
        var reply = string.Empty;

        if (AgentName.TryCreate(instruct.Agent, out var agent)
            && catalogue.TryResolve(agent.Role, project, out var identity, out _)
            && residents.TryGetValue(identity, out var resident)
            && resident.IsAlive)
        {
            try
            {
                // Marked, never bare. §11.5 requires an instruction to reach the agent as the
                // operator's rather than as a peer's, because what the agent must be able to tell
                // is which of the two it is reading — and it is only able to decline knowingly if
                // it knows it was instructed at all.
                var turn = await resident
                    .TakeTurnAsync(
                        OperatorInstruction.Mark(instruct.Text), TurnTimeout, cancellationToken)
                    .ConfigureAwait(false);

                outcome = turn.Succeeded
                    ? Wire.InstructionOutcome.Taken
                    : Wire.InstructionOutcome.Failed;

                reply = turn.ToString();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = Wire.InstructionOutcome.Failed;
                reply = exception.Message;

                await output.WriteLineAsync(
                    $"  {agent.Value}: instruction failed — {exception.Message}")
                    .ConfigureAwait(false);
            }
        }

        try
        {
            _ = await nodes.ReportInstructionAsync(
                new Wire.InstructionReport
                {
                    Machine = machine,
                    InstructionId = instruct.InstructionId,
                    Outcome = outcome,
                    Reply = reply
                },
                cancellationToken: cancellationToken);
        }
        catch (RpcException failure)
        {
            // The agent may well have done the work. Losing the report loses the operator's
            // answer, not the effect, and saying so is better than a page that waits out its
            // patience with no explanation.
            await output.WriteLineAsync(
                $"  {instruct.Agent}: took the instruction, but the outcome could not be "
                + $"reported — {failure.Status.Detail}").ConfigureAwait(false);
        }
    }

    /// <summary>Starts a turn without holding up the command stream.</summary>
    /// <param name="resident">The agent to give a turn.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <remarks>
    /// <para>
    /// A turn is a model taking a turn and can run for minutes. Awaiting one inside the command
    /// loop makes the whole machine single-file: while one agent thinks, a question for a
    /// different agent waits behind it, and on a machine hosting nineteen agents that is the
    /// ordinary case rather than the unlucky one. It mattered less when a turn followed only a
    /// start; now that every question to a running agent asks for one, it would be the common path.
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
        ResidentAgent resident, TextWriter output, CancellationToken cancellationToken)
    {
        _ = Observe(GiveTurnAsync(resident, output, cancellationToken));

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
    /// <returns>A task that completes when the turn ends.</returns>
    private static async Task GiveTurnAsync(
        ResidentAgent resident, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await resident
                .TakeTurnAsync(InvocationCommand.Prompt, TurnTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Succeeded)
            {
                await output.WriteLineAsync($"  {resident.Name.Value}: {outcome}")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await output.WriteLineAsync($"  {resident.Name.Value}: turn failed — {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stops one agent.</summary>
    /// <param name="name">The agent.</param>
    /// <param name="catalogue">What this machine can start, which is what resolves a name.</param>
    /// <param name="project">The project this node serves, or null to accept every claim.</param>
    /// <param name="residents">The agents running here.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>A task that completes when it has stopped.</returns>
    private static async Task StopAsync(
        string name,
        NodeCatalogue catalogue,
        string? project,
        ConcurrentDictionary<AgentIdentity, ResidentAgent> residents,
        TextWriter output)
    {
        if (!AgentName.TryCreate(name, out var asked)
            || !catalogue.TryResolve(asked.Role, project, out var identity, out _)
            || !residents.TryRemove(identity, out var resident))
        {
            return;
        }

        await ShutDownAsync(resident, name, output).ConfigureAwait(false);
    }

    /// <summary>Ends one resident and says so.</summary>
    /// <param name="resident">The agent.</param>
    /// <param name="named">What to call it in the report.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>When it has gone.</returns>
    /// <remarks>
    /// Shared by the one that is asked for and the sweep at the end, so a shutdown cannot come to mean two
    /// things depending on which route reached it.
    /// </remarks>
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
        foreach (var identity in residents.Keys.ToArray())
        {
            if (!residents.TryRemove(identity, out var resident))
            {
                continue;
            }

            await ShutDownAsync(resident, identity.ToString(), output).ConfigureAwait(false);
        }
    }

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
            OsVersion = Environment.OSVersion.Version.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ParleyVersion = ProtocolVersion.Current
        };

        try
        {
            var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

            facts.Domain = properties.DomainName ?? string.Empty;

            facts.Fqdn = string.IsNullOrEmpty(properties.DomainName)
                ? properties.HostName
                : $"{properties.HostName}.{properties.DomainName}";

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
