using System.Collections.Concurrent;
using System.Globalization;
using Grpc.Core;
using Grpc.Net.Client;
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
    /// The catalogue is named by the caller rather than found here. Program.cs resolves it once,
    /// through the same precedence every other setting uses, and a node that resolved it a second
    /// time would ignore <c>--node-config</c> — which is what it used to do.
    /// </remarks>
    public static async Task<int> RunAsync(
        GrpcChannel channel,
        string endpoint,
        string catalogueFile,
        TextWriter output,
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

        var request = new Wire.JoinRequest
        {
            Machine = NodeCatalogue.Machine,
            Facts = Observe()
        };

        request.StartableRoles.AddRange(catalogue.Roles);

        var residents = new ConcurrentDictionary<string, ResidentAgent>(StringComparer.Ordinal);

        // Turns now run concurrently and each reports its own outcome, so more than one of them can
        // be writing at once. A TextWriter is not thread-safe, and the callers here supply whatever
        // they have — one passes Console.Out, which is synchronised, and one passes a writer that
        // may not be. Wrapping once at the entry point makes that not a question the callers have
        // to have got right.
        output = TextWriter.Synchronized(output);

        try
        {
            using var stream = nodes.Join(request, cancellationToken: cancellationToken);

            await foreach (var command in stream.ResponseStream
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (command.CommandCase)
                {
                    case Wire.NodeCommand.CommandOneofCase.Joined:
                        await ReportJoinedAsync(
                            command.Joined, catalogue, catalogueFile, endpoint, output)
                            .ConfigureAwait(false);
                        break;

                    case Wire.NodeCommand.CommandOneofCase.Start:
                        await StartAsync(command.Start, catalogue, residents, output,
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case Wire.NodeCommand.CommandOneofCase.Turn:
                        await TurnAsync(command.Turn, residents, output, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case Wire.NodeCommand.CommandOneofCase.Stop:
                        await StopAsync(command.Stop.Agent, residents, output).ConfigureAwait(false);
                        break;

                    default:
                        // A command this build does not know about. Ignored rather than fatal:
                        // the broker may be newer, and a node that fell over on an unrecognised
                        // instruction would take a machine off the air over a feature it simply
                        // does not participate in.
                        break;
                }
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
            await StopEveryoneAsync(residents, output).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Reports what the broker knows about this machine.</summary>
    /// <param name="joined">The acknowledgement.</param>
    /// <param name="catalogue">What this machine can start.</param>
    /// <param name="catalogueFile">The file it was read from, named when it turns out to be empty.</param>
    /// <param name="endpoint">Where the broker is.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>A task that completes when it has been reported.</returns>
    /// <remarks>
    /// <para>
    /// The reconciliation is the useful part: an agent registered to this machine that the
    /// catalogue cannot launch is a repository that moved, was renamed, or was never listed —
    /// and saying so at startup beats discovering it when a question to it goes unanswered for a
    /// window and is then ghosted.
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
        string catalogueFile,
        string endpoint,
        TextWriter output)
    {
        var startable = catalogue.Roles.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"nothing (no agents in {catalogueFile})")
            : string.Join(", ", catalogue.Roles.Order(StringComparer.Ordinal));

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Node {joined.Machine} joined {endpoint}. Can start: {startable}."))
            .ConfigureAwait(false);

        var unstartable = joined.Agents
            .Where(a => AgentName.TryCreate(a, out var name) && catalogue.CommandFor(name) is null)
            .ToArray();

        if (unstartable.Length > 0)
        {
            await output.WriteLineAsync(
                $"  Registered here but not in the catalogue: {string.Join(", ", unstartable)}. " +
                "A question to one of those will wait and then be ghosted.").ConfigureAwait(false);
        }
    }

    /// <summary>Starts an agent and gives it a turn.</summary>
    /// <param name="start">What to start, and why.</param>
    /// <param name="catalogue">What this machine can start.</param>
    /// <param name="residents">The agents already running here.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <returns>A task that completes when the turn has been taken.</returns>
    /// <remarks>
    /// <para>
    /// The turn is what makes starting worth anything. A process that launched and sat there
    /// would have attached, drained nothing, and answered nothing — SPEC.md §4.2 is clear that
    /// waking an adapter is not the same as the model taking a turn, and the model is what
    /// answers.
    /// </para>
    /// <para>
    /// Failures are reported and swallowed. A repository that has moved, a command that is not
    /// installed, an agent that dies on startup: none of them is a reason to take the whole
    /// machine off the air, and the question that prompted this will orphan and be ghosted on
    /// its ordinary schedule, which is how the asker finds out.
    /// </para>
    /// </remarks>
    private static async Task StartAsync(
        Wire.StartAgent start,
        NodeCatalogue catalogue,
        ConcurrentDictionary<string, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!AgentName.TryCreate(start.Agent, out var agent))
        {
            return;
        }

        if (residents.TryGetValue(agent.Value, out var existing) && existing.IsAlive)
        {
            // Already here. The broker checks before asking, but the check and the command are
            // not one operation — so this is the race, and losing it must not produce a second
            // process for an agent that needed one.
            DispatchTurn(existing, output, cancellationToken);
            return;
        }

        if (catalogue.CommandFor(agent) is not { } command)
        {
            await output.WriteLineAsync(
                $"  {agent.Value}: asked for, but this machine has no way to start it.")
                .ConfigureAwait(false);

            return;
        }

        try
        {
            var invocation = InvocationCommand.Parse(command);
            var workspace = catalogue.WorkspaceFor(agent) ?? Directory.GetCurrentDirectory();

            var resident = ResidentAgent.Start(agent, invocation, workspace, TimeProvider.System);
            residents[agent.Value] = resident;

            await output.WriteLineAsync(
                $"  {agent.Value}: started ({start.Because}).").ConfigureAwait(false);

            DispatchTurn(resident, output, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await output.WriteLineAsync($"  {agent.Value}: could not start — {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>Gives a turn to an agent this node already holds.</summary>
    /// <param name="turn">Which agent, and why.</param>
    /// <param name="residents">The agents already running here.</param>
    /// <param name="output">Where to report.</param>
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
        ConcurrentDictionary<string, ResidentAgent> residents,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!AgentName.TryCreate(turn.Agent, out var agent))
        {
            return;
        }

        if (!residents.TryGetValue(agent.Value, out var resident))
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
            if (residents.TryRemove(agent.Value, out var corpse))
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
    /// <param name="residents">The agents running here.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>A task that completes when it has stopped.</returns>
    private static async Task StopAsync(
        string name, ConcurrentDictionary<string, ResidentAgent> residents, TextWriter output)
    {
        if (!residents.TryRemove(name, out var resident))
        {
            return;
        }

        _ = await resident.ShutdownAsync(ShutdownGrace).ConfigureAwait(false);
        await resident.DisposeAsync().ConfigureAwait(false);

        await output.WriteLineAsync($"  {name}: stopped.").ConfigureAwait(false);
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
    private static async Task StopEveryoneAsync(
        ConcurrentDictionary<string, ResidentAgent> residents, TextWriter output)
    {
        foreach (var name in residents.Keys.ToArray())
        {
            await StopAsync(name, residents, output).ConfigureAwait(false);
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
