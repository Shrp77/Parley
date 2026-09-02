using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Web;

/// <summary>
/// The projects this machine is configured for, and one operator client per project.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the console's multi-project handling lives here, and it is deliberately entirely
/// client-side. SPEC.md §4.1 says the protocol has no concept of a project and must not acquire
/// one: membership is which endpoint an agent connected to, so the mapping from a name to an
/// endpoint is a fact about this machine's manifest and about nothing else. No request this class
/// sends carries a project identifier, and none may ever be added.
/// </para>
/// <para>
/// A channel is held per project because constructing one is not free and the console builds a
/// page per request. Reachability is not held: it is observed by whether a call succeeds, per
/// call, because a broker that was up when the page was built tells the reader nothing about
/// whether it is up now.
/// </para>
/// <para>
/// <b>The project list is not held either, and that reverses what this class did before.</b> It
/// read the manifest once, in the factory, and a singleton behind a web server then served that
/// answer for the life of the process. That was defensible while <c>parley console</c> was
/// something an operator started, looked at, and stopped — a startup read and a live one are the
/// same read when the process lasts a minute. It stopped being defensible when the console moved
/// inside the broker and <c>autostart install</c> turned it on by default: the process now lasts
/// weeks, and the manifest is precisely the file that changes underneath it. The naming page
/// sends a person away to run <c>parley project add</c> and promises the new name will be on the
/// list when they come back; with a cached read that promise was false, and the only way to
/// collect on it was to restart the broker — which drops every agent attached to the deployment,
/// to pick up a rename the protocol never sees.
/// </para>
/// <para>
/// So the manifest is re-read, and the read is skipped when the file's stamp is unchanged. The
/// cheap thing here is the file — a few hundred bytes of local JSON, against the gRPC round trips
/// every page already makes — and the expensive thing is the channel, which is why the channels
/// stay cached and the projects do not.
/// </para>
/// </remarks>
public sealed class BrokerDirectory : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<GrpcChannel>> _channels =
        new(StringComparer.Ordinal);

    private readonly Func<string, GrpcChannel> _connect;
    private readonly string? _manifestPath;
    private readonly FallbackBroker? _fallback;
    private volatile Snapshot _snapshot;
    private volatile bool _disposed;

    /// <summary>Builds a directory over a fixed set of projects, which no file can change.</summary>
    /// <param name="projects">The projects, in the order the manifest listed them.</param>
    /// <param name="connect">Opens a channel to an endpoint. Injected so tests need no sockets.</param>
    /// <param name="manifestProblem">Why the manifest could not be read, when it could not.</param>
    /// <exception cref="ArgumentNullException">Either required argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The list given here is the answer for the life of the object: there is no path to re-read,
    /// so nothing is watched. That is what a test wants, and it is also the honest shape — a
    /// caller who supplied the projects owns them, and a directory that quietly replaced them
    /// with whatever was in this machine's manifest would be answering a question nobody asked.
    /// <see cref="FromManifest"/> is the constructor that follows a file.
    /// </remarks>
    public BrokerDirectory(
        IReadOnlyList<ProjectSettings> projects,
        Func<string, GrpcChannel> connect,
        string? manifestProblem = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(connect);

        _connect = connect;
        _snapshot = new Snapshot(projects, manifestProblem, default);
    }

    /// <summary>Builds a directory that follows this machine's manifest.</summary>
    /// <param name="manifestPath">The manifest to read, now and whenever it changes.</param>
    /// <param name="fallback">A broker to offer when the manifest names no projects.</param>
    /// <param name="connect">Opens a channel to an endpoint.</param>
    private BrokerDirectory(
        string manifestPath,
        FallbackBroker? fallback,
        Func<string, GrpcChannel> connect)
    {
        _connect = connect;
        _manifestPath = manifestPath;
        _fallback = fallback;
        _snapshot = ReadManifest(manifestPath, fallback, StampOf(manifestPath));
    }

    /// <summary>The projects named in this machine's manifest, as it stands now.</summary>
    public IReadOnlyList<ProjectSettings> Projects => Current.Projects;

    /// <summary>
    /// Why the manifest could not be read, or <see langword="null"/> when it was read.
    /// </summary>
    /// <remarks>
    /// A machine with no manifest is an ordinary machine, not a broken one, and reports no
    /// problem with an empty project list. This is only set when a manifest exists and is
    /// unusable, which the reader must be told rather than shown as "no projects".
    /// </remarks>
    public string? ManifestProblem => Current.Problem;

    /// <summary>The manifest as it stands, re-read only when the file has moved on.</summary>
    /// <remarks>
    /// <para>
    /// <b>The stamp is taken before the read, not after, and the order is the whole correctness
    /// of this.</b> Taken afterwards, a write landing mid-read would be stored under the stamp of
    /// the file that superseded it — content from before the edit, labelled as the state after it,
    /// and never re-read again. Taken first, the same race stores newer content under an older
    /// stamp, so the next caller finds a mismatch and reads again. One direction is a stale answer
    /// that never heals; the other is one redundant read.
    /// </para>
    /// <para>
    /// Two callers arriving together may both read and both assign, and the loser's work is
    /// discarded. That is deliberate: a lock here would put every page render behind one file
    /// handle to save an operation that happens when a manifest has just been edited and not
    /// otherwise. If the later write wins with the older snapshot, its stamp is older too, so the
    /// next access corrects it by the paragraph above.
    /// </para>
    /// </remarks>
    private Snapshot Current
    {
        get
        {
            if (_manifestPath is null)
            {
                return _snapshot;
            }

            var stamp = StampOf(_manifestPath);
            var held = _snapshot;

            if (stamp == held.Stamp)
            {
                return held;
            }

            var fresh = ReadManifest(_manifestPath, _fallback, stamp);

            _snapshot = fresh;

            return fresh;
        }
    }

    /// <summary>Reads this machine's manifest and builds a directory from it.</summary>
    /// <param name="explicitPath">A path given on the command line, if one was.</param>
    /// <param name="fallbackEndpoint">
    /// A broker to offer when the manifest names no projects, or null to offer none.
    /// </param>
    /// <returns>The directory, whether or not the manifest was readable.</returns>
    /// <remarks>
    /// <para>
    /// A manifest is how a machine names its deployments, and SPEC.md §4.1 is explicit that
    /// running without one — configured entirely by explicit paths — is supported. Every other
    /// operator command works on such a machine, because each takes <c>--broker</c> or reads
    /// <c>PARLEY_BROKER</c> and falls back to the local one. A console that showed an empty page
    /// there would be the only client unable to reach a broker the rest of the tool talks to
    /// happily, and the page would say "no projects" — which is true and reads as "nothing to
    /// see".
    /// </para>
    /// <para>
    /// The fallback is a project like any other, so "pick a project first" still holds: the list
    /// is shown, it has one entry, and it is chosen before anything below it renders. What
    /// changes is where the entry came from, not whether there is one.
    /// </para>
    /// <para>
    /// Only when the manifest names nothing. A machine that has named its deployments has said
    /// what it wants listed, and quietly adding an unnamed row to that list would be the console
    /// inventing a project the operator did not configure.
    /// </para>
    /// </remarks>
    public static BrokerDirectory FromManifest(
        string? explicitPath = null, FallbackBroker? fallbackEndpoint = null) =>
        new(ProjectManifest.Locate(explicitPath, Environment.GetEnvironmentVariable),
            fallbackEndpoint,
            Connect);

    /// <summary>Reads one manifest into a snapshot.</summary>
    /// <param name="path">The manifest to read.</param>
    /// <param name="fallback">A broker to offer when it names no projects.</param>
    /// <param name="stamp">The file's stamp, taken before this read began.</param>
    /// <returns>What the file says, including that it could not be read.</returns>
    /// <remarks>
    /// A read that fails reports the problem and no projects, rather than keeping the projects
    /// the last good read found. An operator who has just broken their manifest needs to be told
    /// so; a console that went on rendering the old list would hide the edit that caused it, and
    /// the file is the authority whether or not this process liked the last version of it.
    /// </remarks>
    private static Snapshot ReadManifest(string path, FallbackBroker? fallback, Stamp stamp)
    {
        if (!ProjectManifest.TryRead(path, out var projects, out var problem))
        {
            return new Snapshot([], problem, stamp);
        }

        if (projects.Count == 0 && fallback is { Endpoint.Length: > 0 } offer)
        {
            return new Snapshot([Unnamed(offer.Endpoint)], null, stamp);
        }

        return new Snapshot(projects, null, stamp);
    }

    /// <summary>Reads the manifest's stamp without opening it.</summary>
    /// <param name="path">The file to look at.</param>
    /// <returns>Its stamp, or the default when it is absent or cannot be looked at.</returns>
    /// <remarks>
    /// <para>
    /// Length as well as write time, because the two together are what a filesystem cheaply
    /// offers and either alone is weaker. A stat that throws reports the default — the same as
    /// absent — which is the safe direction: a directory built from a file that was present then
    /// holds a stamp that this cannot equal, so the mismatch sends it to
    /// <see cref="ReadManifest"/>, where a real failure becomes a problem the operator is shown
    /// rather than a silence.
    /// </para>
    /// <para>
    /// <b>The accepted limit.</b> Windows updates a file's write time on the system timer tick,
    /// not per write, so two edits inside about fifteen milliseconds share a timestamp — and if
    /// they also share a length, this reports no change and the second is missed until something
    /// else touches the file. Reaching that needs two runs of <c>parley project</c> inside one
    /// tick, each its own process launch, producing manifests of identical size. It is written
    /// down rather than closed because closing it means hashing the contents, which is the read
    /// this exists to avoid.
    /// </para>
    /// </remarks>
    private static Stamp StampOf(string path)
    {
        try
        {
            var file = new FileInfo(path);

            return file.Exists ? new Stamp(file.Length, file.LastWriteTimeUtc) : default;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>What one read of the manifest found, and the file it found it in.</summary>
    /// <param name="Projects">The projects, in the order the manifest listed them.</param>
    /// <param name="Problem">Why the manifest could not be read, when it could not.</param>
    /// <param name="Stamp">The stamp of the file this was read from.</param>
    private sealed record Snapshot(
        IReadOnlyList<ProjectSettings> Projects, string? Problem, Stamp Stamp);

    /// <summary>Enough of a file to tell whether it is the one already read.</summary>
    /// <param name="Length">Its size in bytes.</param>
    /// <param name="WrittenUtc">When it was last written.</param>
    /// <remarks>
    /// The default value means <i>absent</i>, and is what a machine with no manifest holds — so a
    /// machine that has never named a deployment does no reading at all, and the file appearing
    /// is itself the change that provokes one.
    /// </remarks>
    private readonly record struct Stamp(long Length, DateTime WrittenUtc);

    /// <summary>The single project a machine with no manifest is nonetheless running.</summary>
    /// <param name="endpoint">Where its broker listens.</param>
    /// <returns>A project standing for that broker.</returns>
    /// <remarks>
    /// Named <c>this machine</c> rather than for the endpoint, because the name is what appears
    /// in every heading below it and an address there reads as a setting rather than as a place.
    /// The endpoint is shown beside it on the list, as it is for every other project.
    /// </remarks>
    private static ProjectSettings Unnamed(string endpoint) =>
        new(ProjectName.Create("this-machine"),
            string.Empty,
            endpoint,
            null,
            "Not named in a manifest. This is the broker this machine's commands talk to; "
            + "`parley project add` gives it a name.");

    /// <summary>Opens a channel with the console's own connect policy.</summary>
    /// <param name="endpoint">Where that project's broker listens.</param>
    /// <returns>The channel.</returns>
    /// <remarks>
    /// The two-second connect timeout matches <c>Parley.Mcp</c>'s and exists for the same reason:
    /// a broker that is down is down, waiting longer does not find it, and the default ten seconds
    /// sits in front of a page the operator is waiting on. It also keeps SC-004 — an unreachable
    /// project reported within five seconds — reachable by arithmetic rather than by hope.
    /// </remarks>
    public static GrpcChannel Connect(string endpoint) =>
        GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            }
        });

    /// <summary>Finds a project by the name the manifest gave it.</summary>
    /// <param name="name">The project name from the route.</param>
    /// <returns>The project, or <see langword="null"/> when this machine has no such project.</returns>
    public ProjectSettings? Find(string? name) =>
        name is null
            ? null
            : Projects.FirstOrDefault(p => string.Equals(p.Name.Value, name, StringComparison.Ordinal));

    /// <summary>The operator client for one project.</summary>
    /// <param name="project">The project to reach.</param>
    /// <returns>A client bound to that project's endpoint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The <c>Operators</c> client, and the one every page reads through. <see cref="AgentsFor"/>
    /// is the other half and is deliberately a separate method rather than an option on this one:
    /// a caller reaching for an agent's operations has to say so, which is the whole of what
    /// SPEC.md §10.2 asks for and is what its naming convention was standing in for.
    /// </para>
    /// <para>
    /// Concurrent by construction, because this object is a singleton behind a web server and
    /// every page reaches it. A plain dictionary here is not merely a duplicated channel under
    /// race — two threads resizing one at the same time can leave its buckets in a state where a
    /// later read never terminates, which presents as a request that hangs forever and a core at
    /// a hundred percent, on a console that had been working for weeks.
    /// </para>
    /// <para>
    /// <see cref="Lazy{T}"/> rather than a bare value because <c>GetOrAdd</c> may run its factory
    /// more than once and keep only one result. The losers of that race would be connected
    /// channels nobody holds a reference to and nothing ever disposes.
    /// </para>
    /// </remarks>
    public Wire.Operators.OperatorsClient ClientFor(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = _channels.GetOrAdd(
            project.Endpoint,
            endpoint => new Lazy<GrpcChannel>(
                () => _connect(endpoint), LazyThreadSafetyMode.ExecutionAndPublication));

        return new Wire.Operators.OperatorsClient(channel.Value);
    }

    /// <summary>Opens an agent client for one project, for the seat a person holds there.</summary>
    /// <param name="project">The project whose broker to reach.</param>
    /// <returns>A client bound to that project's endpoint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The directory has been disposed.</exception>
    /// <remarks>
    /// <para>
    /// This existing at all reverses what was written here before it, so the argument that was
    /// replaced is worth stating: an <c>Agents</c> client in the console put an agent's
    /// operations one mistake from a web route, and the console had no business holding one
    /// because an operator is not an agent.
    /// </para>
    /// <para>
    /// The second half of that is still true and is why the reversal is narrow. An operator still
    /// has no agent identity. What has one is a <em>person</em>, who SPEC.md §11.2 always admitted
    /// as a responder and §11.3 always gave a name — and who had no way to hold that identity
    /// between turns while sitting at a browser. So this is bound for a seat and by
    /// <see cref="Seating.SeatBook"/> alone, which is the only thing in the console that knows a
    /// person's name and the only thing that checks it against the registry before using it.
    /// </para>
    /// <para>
    /// The channel is the same one <see cref="ClientFor"/> uses, because it is the same broker.
    /// A second channel per project would double the connections a console holds for no reason
    /// beyond which service is being called over them.
    /// </para>
    /// </remarks>
    public Wire.Agents.AgentsClient AgentsFor(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = _channels.GetOrAdd(
            project.Endpoint,
            endpoint => new Lazy<GrpcChannel>(
                () => _connect(endpoint), LazyThreadSafetyMode.ExecutionAndPublication));

        return new Wire.Agents.AgentsClient(channel.Value);
    }

    /// <summary>Runs one read against one project, containing a failure to that project.</summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="read">The call to make.</param>
    /// <returns>What came back, or why nothing did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The containment of FR-004 in one place. A broker that is not answering makes its own
    /// project unreadable and must leave every other project alone — so every page reads each
    /// project through this, and a page that awaited a bare call would reintroduce the coupling
    /// by omission.
    /// </remarks>
    public static async Task<ProjectRead<T>> ReadAsync<T>(Func<Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            return ProjectRead<T>.Read(await read().ConfigureAwait(false));
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            return ProjectRead<T>.Unreachable(
                "This project's broker is not answering. Nothing else on this page is affected.");
        }
        catch (RpcException failure)
        {
            return ProjectRead<T>.Unreachable(
                $"This project's broker refused the read: {failure.Status.Detail}");
        }
        catch (Exception failure) when (failure is UriFormatException or InvalidOperationException)
        {
            // Opening the channel, not making the call. An endpoint the manifest accepted and a
            // URL Grpc can dial are not the same set -- "localhost:5301" with no scheme is the
            // usual way to find that out -- and this throws where the connection is first
            // attempted, which is inside here rather than at startup.
            //
            // Contained for the same reason as the rest: without it, one unusable endpoint takes
            // down the page for every project beside it, which is precisely the coupling this
            // method exists to prevent.
            return ProjectRead<T>.Unreachable(
                $"This project's endpoint cannot be reached as written: {failure.Message}");
        }
    }

    /// <summary>Closes every channel this directory opened.</summary>
    /// <remarks>
    /// Marks itself disposed before it closes anything, so a request arriving mid-shutdown is
    /// refused rather than handed a channel that is about to be torn out from under it. Anything
    /// already inside <see cref="ClientFor"/> at that instant still completes; that is a
    /// shutdown, not a leak.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var channel in _channels.Values)
        {
            if (channel.IsValueCreated)
            {
                channel.Value.Dispose();
            }
        }

        _channels.Clear();
    }
}

/// <summary>The broker to offer when a machine has named no projects.</summary>
/// <param name="Endpoint">Where that broker listens.</param>
/// <remarks>
/// A type of its own rather than a second string beside the manifest path, because two adjacent
/// nullable strings is a call site somebody gets the wrong way round — and the mistake is silent
/// in the worst way: an address read as a manifest path is simply a path that does not exist,
/// which is an ordinary machine with nothing configured, so the page shows an empty list and no
/// error. A test caught exactly that, and a compiler error is a better answer than a test.
/// </remarks>
public sealed record FallbackBroker(string Endpoint);

/// <summary>The outcome of one read against one project.</summary>
/// <typeparam name="T">What the read returns.</typeparam>
/// <remarks>
/// Two states rather than an exception, because "this project could not be read" is something the
/// page renders rather than something it fails on. §4.1 gives each project its own broker, so one
/// being down is a fact about that project and not about the deployment.
/// </remarks>
public sealed record ProjectRead<T>
{
    private ProjectRead(T? value, string? problem)
    {
        Value = value;
        Problem = problem;
    }

    /// <summary>What came back, when the read succeeded.</summary>
    public T? Value { get; }

    /// <summary>Why nothing came back, when it did not.</summary>
    public string? Problem { get; }

    /// <summary>Whether this project answered.</summary>
    public bool Reachable => Problem is null;

    /// <summary>A successful read.</summary>
    /// <param name="value">What came back.</param>
    /// <returns>The outcome.</returns>
    public static ProjectRead<T> Read(T value) => new(value, null);

    /// <summary>A project that could not be read.</summary>
    /// <param name="problem">What to tell the operator.</param>
    /// <returns>The outcome.</returns>
    public static ProjectRead<T> Unreachable(string problem) => new(default, problem);
}
