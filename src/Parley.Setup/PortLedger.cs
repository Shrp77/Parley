using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// What port each project on this machine was handed, and the hold that makes handing one out safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>A claim, not a probe.</b> Choosing "a free port" by binding one and letting go is a race this
/// repository already has on record as an intermittent suite failure — two callers bind, both release,
/// both believe they have it. Making that the way a person creates a project would be shipping the flake.
/// What serialises the decision is this file: it is opened with no sharing, and the whole
/// read-choose-write happens inside that one hold.
/// </para>
/// <para>
/// <b>The record is its own lock, and that is deliberate.</b> A separate lock file would be two files
/// where one will do, and a second thing to leave behind. <see cref="FileShare.None"/> gives the property
/// needed on both platforms with no new primitive — on Windows a sharing refusal, on Linux and macOS an
/// exclusive <c>flock</c> — and a crash releases it with every other handle, so there is no stale hold to
/// clear and no timeout to choose. That reasoning is <c>StoreLock</c>'s and is borrowed rather than
/// reinvented.
/// </para>
/// <para>
/// <b>Two things are carried across from <c>StoreLock</c> and both are load-bearing.</b> The file is
/// never deleted, including when the last project goes: on Linux an exclusive lock belongs to an open
/// file rather than to a name, so unlinking it would let the next two claimants each create and lock a
/// different file of one name. And a failed open counts as contention only when the file exists
/// afterwards, so a missing directory is not reported as somebody else holding it.
/// </para>
/// <para>
/// <b>Where this differs from <c>StoreLock</c>, and it is the only difference:</b> the hold is transient,
/// so contention is ordinary rather than fatal. Two brokers on one store is a defect and is refused
/// outright; two people running <c>project add</c> at the same moment is a Tuesday. So a claim retries
/// under a budget, and a claim that runs out of budget says so and leaves nothing behind.
/// </para>
/// <para>
/// <b>The projects are the truth and this is bookkeeping</b>, which is what makes losing it survivable.
/// Every claim reconciles: after taking the hold it reads every project's configuration and treats each
/// endpoint's port as spoken for, whether or not this file lists it. A record that has lost entries costs
/// one directory listing to repair; a record deleted outright costs exactly the same.
/// </para>
/// <para>
/// <b>So this file is written in place, where <c>project.json</c> is committed by rename.</b> That is not
/// an inconsistency: the rename exists because a project's endpoint has no second copy, and a torn write
/// leaves a machine needing a text editor. This file's contents are reconstructible from the projects
/// themselves, so a torn write costs a rebuild rather than a deployment — and the rename could not be
/// used here anyway, because replacing the file would break the very hold that makes the claim safe.
/// </para>
/// </remarks>
// **Internal, because only `project add` and `project remove` claim a port**, and both live in this
// assembly. Neither tool names this type, so making it public would widen the surface for the tests'
// benefit — which SetupSurfaceJustifiedTests exists to catch, and did, on this very type.
internal static class PortLedger
{
    /// <summary>What the record is called, at the root.</summary>
    internal const string FileName = "ports.json";

    /// <summary>The lowest port handed out.</summary>
    /// <remarks>
    /// <b>The same number as <see cref="BrokerEndpoint.DefaultPort"/>, and read from it rather than
    /// repeated.</b> The first project a machine names is handed this port, so "where a broker is
    /// when nothing says otherwise" and "where this machine's broker is" are one address on the
    /// ordinary machine — which is only true while the two numbers agree, and two constants agree
    /// until one of them is edited. 019 shipped with them disagreeing: the band moved here and the
    /// default was left pointing into the ephemeral range the band exists to avoid.
    /// </remarks>
    internal const int BandFirst = BrokerEndpoint.DefaultPort;

    /// <summary>The highest port handed out.</summary>
    /// <remarks>
    /// <para>
    /// <b>Above the well-known range, below Linux's ephemeral floor, and clear of what developers
    /// habitually take.</b> The deployment that prompted 019 serves on 51707 and 53283, and both sit
    /// inside the operating system's ephemeral range — Windows' dynamic range on that machine is
    /// 49152–65535, Linux's default <c>ip_local_port_range</c> is 32768–60999. A port in that band can be
    /// held transiently by an *outbound* connection from any program on the machine, which is a
    /// logon-time bind failure that happens rarely and looks like nothing. Handing ports out from there
    /// would manufacture that flake rather than inherit it.
    /// </para>
    /// <para>
    /// <b>Windows also reserves ranges, and reallocates them at boot.</b> On that same machine
    /// <c>netsh int ipv4 show excludedportrange protocol=tcp</c> reports exclusions at 50000–50059 and
    /// seven blocks between 53116 and 54235 — and the second project serves on 53283, one port below the
    /// block beginning at 53284. A reallocation shifting down by one would take that broker out with
    /// "an attempt was made to access a socket in a way forbidden by its access permissions", which reads
    /// as a permissions fault rather than a taken port. This band is clear of every exclusion there, and
    /// the availability check below is what handles a range that moves later.
    /// </para>
    /// <para>
    /// Two hundred and fifty-six projects is far beyond what a machine will have, so this is a constant
    /// rather than a setting until somebody needs it to be one.
    /// </para>
    /// </remarks>
    internal const int BandLast = 19355;

    /// <summary>The member naming which project holds which port.</summary>
    private const string HandedOut = "handed_out";

    /// <summary>The member recording the range ports are taken from.</summary>
    private const string Band = "band";

    /// <summary>How long a claim waits for the hold before giving up.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly string[] Guidance =
    [
        "Which port each project on this machine was handed. Bookkeeping, not truth: the projects",
        "themselves are, and every claim reads them before choosing - so deleting this file costs a",
        "rebuild and nothing else.",
        "",
        "It is also its own lock. Creating and removing a project open it with no sharing for the",
        "whole of choosing a port; nothing that reads, starts or serves ever touches it.",
    ];

    /// <summary>Where the record lives.</summary>
    /// <param name="root">The machine's root.</param>
    /// <returns>The path, which need not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    internal static string PathIn(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return Path.Combine(root, FileName);
    }

    /// <summary>Reads what has been handed out, for a caller that only wants to look.</summary>
    /// <param name="root">The machine's root.</param>
    /// <returns>One entry per project the record names, which may be stale.</returns>
    /// <remarks>
    /// <b>Takes no hold</b>, because nothing that reads may block a claim — and because what it returns
    /// is bookkeeping, so a reader that needs the truth reads the projects.
    /// </remarks>
    internal static IReadOnlyDictionary<string, int> Read(string root)
    {
        try
        {
            using var stream = new FileStream(
                PathIn(root), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);

            return Recorded(JsonNode.Parse(reader.ReadToEnd()) as JsonObject);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>Hands a project a port nothing else on this machine has been handed.</summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project being created.</param>
    /// <param name="endpoint">The address it should serve on, when this returns true.</param>
    /// <param name="problem">Why no port could be handed out, when this returns false.</param>
    /// <returns><see langword="true"/> when a port was claimed and recorded.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    internal static bool TryClaim(string root, ProjectName name, out string? endpoint, out string? problem) =>
        TryClaim(root, name, CanBind, out endpoint, out problem);

    /// <summary>Hands a project a port, given a way to ask whether one is available.</summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project being created.</param>
    /// <param name="free">Answers whether a port can be bound on loopback.</param>
    /// <param name="endpoint">The address it should serve on, when this returns true.</param>
    /// <param name="problem">Why no port could be handed out, when this returns false.</param>
    /// <returns><see langword="true"/> when a port was claimed and recorded.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="free"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>The availability check is a parameter so exhaustion can be driven.</b> Proving that a full band
    /// is reported rather than papered over would otherwise mean binding two hundred and fifty-six
    /// sockets, which is a test that measures the machine it runs on.
    /// </para>
    /// <para>
    /// <b>Binding inside the hold is what answers the recorded flake.</b> What made the old free-port
    /// helper race was that its bind-and-release had nothing serialising it; the hold is what this adds.
    /// The bind is kept because it stops a port being handed out that something else already has — and it
    /// is honestly not a guarantee: a non-Parley program may take the port a moment later, and the record
    /// states what Parley handed out rather than what the operating system did. A broker refusing to
    /// start on a held port is what reports that, and already does.
    /// </para>
    /// </remarks>
    internal static bool TryClaim(
        string root,
        ProjectName name,
        Func<int, bool> free,
        out string? endpoint,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(free);

        endpoint = null;

        var path = PathIn(root);

        using var held = Hold(path, out problem);

        if (held is null)
        {
            return false;
        }

        var document = Parse(held);
        var taken = SpokenFor(root, document);
        var (first, last) = BandIn(document);

        // A project already holding a port keeps it. Claiming twice is what `project set` does when it
        // changes something else, and handing out a second port would strand the first.
        if (Recorded(document).TryGetValue(name.Value, out var already))
        {
            // **The project wins where the two disagree**. Its own file is what its
            // broker binds, so handing back the record's number gave the project a second, different port — found by
            // the combined review gate. The record is corrected rather than believed.
            if (ProjectFolder.TryRead(root, name, out var own, out _)
                && Uri.TryCreate(own!.Endpoint, UriKind.Absolute, out var address)
                && address.Port > 0)
            {
                already = address.Port;
            }


            endpoint = Loopback(already);
            problem = null;

            return Commit(held, document, name, already, out problem);
        }

        for (var port = first; port <= last; port++)
        {
            if (taken.Contains(port) || !free(port))
            {
                continue;
            }

            endpoint = Loopback(port);

            return Commit(held, document, name, port, out problem);
        }

        problem =
            $"Every port from {first} to {last} is already handed out or in use, so '{name}' "
            + "has not been created. A machine with this many projects needs a decision rather than "
            + "another one.";

        return false;
    }

    /// <summary>Gives a project's port back.</summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project being removed.</param>
    /// <param name="problem">Why the record could not be changed, when this returns false.</param>
    /// <returns><see langword="true"/> when the record no longer names the project.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    /// <remarks>
    /// A project the record does not name is not a failure: the record is bookkeeping, and being asked
    /// to forget something it never knew is the state the caller wanted.
    /// </remarks>
    internal static bool TryRelease(string root, ProjectName name, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var path = PathIn(root);

        using var held = Hold(path, out problem);

        if (held is null)
        {
            return false;
        }

        var document = Parse(held);

        if (document[HandedOut] is JsonObject ports)
        {
            _ = ports.Remove(name.Value);
        }

        return Write(held, document, out problem);
    }

    /// <summary>The address a claimed port means.</summary>
    /// <param name="port">The port.</param>
    /// <returns>A loopback endpoint.</returns>
    /// <remarks>
    /// <b>What is handed out is loopback, and a wider address is stated rather than chosen.</b> SPEC.md
    /// §16 confines the transport to a loopback connection, a private network somebody controls, or a
    /// tunnel authenticated outside this protocol — so a broker reachable from another machine is
    /// supported. It cannot be picked automatically: nothing here knows which interface a person means
    /// to expose, and guessing would put a broker on a network by accident.
    /// </remarks>
    internal static string Loopback(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");

    /// <summary>Takes the hold, retrying while somebody else has it.</summary>
    private static FileStream? Hold(string path, out string? problem)
    {
        problem = null;

        var directory = Path.GetDirectoryName(path);

        if (directory is { Length: > 0 })
        {
            _ = Directory.CreateDirectory(directory);
        }

        var deadline = DateTime.UtcNow + Budget;
        var backoff = TimeSpan.FromMilliseconds(10);
        IOException? last = null;

        while (true)
        {
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException contention)
                when (contention is not DirectoryNotFoundException && File.Exists(path))
            {
                // Held, rather than broken — the same distinction StoreLock draws, and for the same
                // reason: anything else that fails an open is its own fault and must propagate as
                // itself, or somebody is told a command is running that is not.
                last = contention;
            }

            if (DateTime.UtcNow >= deadline)
            {
                problem =
                    $"Could not take {path} within {Budget.TotalSeconds:0} seconds, so nothing has "
                    + "been created. Another `parley project` command is still choosing a port, or its "
                    + $"record cannot be opened: {last?.Message}";

                return null;
            }

            Thread.Sleep(backoff);

            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 250));
        }
    }

    /// <summary>Reads the document out of the held stream.</summary>
    private static JsonObject Parse(FileStream held)
    {
        held.Position = 0;

        using var reader = new StreamReader(held, leaveOpen: true);

        try
        {
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // **Rebuilt rather than refused, which is the opposite of what `project.json` does.** That
            // file is the only record of something and is protected accordingly; this one is derivable
            // from the projects, and the reconciliation below is about to read them anyway. Refusing
            // here would leave a machine unable to create a project because of a file whose whole
            // contents it can work out.
            return [];
        }
    }

    /// <summary>The range this machine takes ports from.</summary>
    /// <remarks>
    /// <b>Recorded only when somebody has widened it, and read back so the decision survives.</b> A
    /// machine that needed a different band needed it for a reason this code does not know, and a claim
    /// that silently reverted to the built-in default the next time somebody ran <c>project add</c> would
    /// hand out a port on a range whose owner had already ruled it out. An absent or unusable entry means
    /// the default, because a band nobody set is not a decision to preserve.
    /// </remarks>
    private static (int First, int Last) BandIn(JsonObject document)
    {
        if (document[Band] is JsonObject band
            && band["from"] is JsonValue fromValue && fromValue.TryGetValue<int>(out var from)
            && band["to"] is JsonValue toValue && toValue.TryGetValue<int>(out var to)
            && from > 0 && to >= from && to <= 65535)
        {
            return (from, to);
        }

        return (BandFirst, BandLast);
    }

    /// <summary>Every port that must not be handed out.</summary>
    /// <remarks>
    /// <b>The projects first, the record second.</b> FR-013: an endpoint found on a project is spoken for
    /// whether or not this record lists it, which is what makes a lost or deleted record cost a rebuild
    /// and nothing more.
    /// </remarks>
    private static HashSet<int> SpokenFor(string root, JsonObject document)
    {
        var taken = new HashSet<int>(Recorded(document).Values);

        if (!ProjectManifest.TryRead(root, out var projects, out _))
        {
            // A machine whose projects cannot all be read is one where the ports in the record are the
            // best answer available. Reported by the caller that reads them, not here — a claim that
            // refused because an unrelated project's file is mid-edit would be a new way to fail.
            return taken;
        }

        foreach (var project in projects)
        {
            if (Uri.TryCreate(project.Endpoint, UriKind.Absolute, out var address) && address.Port > 0)
            {
                _ = taken.Add(address.Port);
            }
        }

        return taken;
    }

    private static Dictionary<string, int> Recorded(JsonObject? document)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);

        if (document?[HandedOut] is not JsonObject ports)
        {
            return found;
        }

        foreach (var (project, value) in ports)
        {
            if (value is JsonValue holding && holding.TryGetValue<int>(out var port))
            {
                found[project] = port;
            }
        }

        return found;
    }

    private static bool Commit(
        FileStream held, JsonObject document, ProjectName name, int port, out string? problem)
    {
        if (document[HandedOut] is not JsonObject ports)
        {
            ports = [];
            document[HandedOut] = ports;
        }

        ports[name.Value] = port;

        return Write(held, document, out problem);
    }

    private static bool Write(FileStream held, JsonObject document, out string? problem)
    {
        problem = null;

        document["_readme"] = new JsonArray([.. Guidance.Select(line => JsonValue.Create(line))]);

        try
        {
            held.Position = 0;
            held.SetLength(0);

            using var writer = new StreamWriter(held, leaveOpen: true);

            writer.Write(document.ToJsonString(Indented));
            writer.Write('\n');
            writer.Flush();

            held.Flush(flushToDisk: true);

            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            problem = $"The port record could not be written: {failure.Message}";

            return false;
        }
    }

    /// <summary>Whether a port can be bound on loopback right now.</summary>
    private static bool CanBind(int port) => CanBind(IPAddress.Loopback, port);

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // Without this a bound-and-closed port stays unavailable for the operating system's own
            // linger period, so a machine that has just stopped a broker would be told its port is
            // taken — by itself, a moment ago.
            probe.ExclusiveAddressUse = true;

            probe.Bind(new IPEndPoint(address, port));

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
