namespace Parley.Core;

/// <summary>
/// Where the broker of SPEC.md §4 listens, and how everything else finds it.
/// </summary>
/// <remarks>
/// <para>
/// In <see cref="Parley.Core"/> because every one of the three processes needs it and none of
/// them should learn it from another: an adapter that took the address from a node, or a node
/// that took it from an adapter, would make the deployment's shape depend on start-up order.
/// </para>
/// <para>
/// <b>Loopback by default, and that is a security decision rather than a convenience.</b>
/// SPEC.md §16 records a hostile network as a non-goal and the transport carries no credentials,
/// so a broker that bound every interface out of the box would put an unauthenticated control
/// surface on whatever network the machine happened to be on. Binding <c>127.0.0.1</c> means the
/// deployment that spans machines is the one somebody configured on purpose.
/// </para>
/// </remarks>
public static class BrokerEndpoint
{
    /// <summary>The environment variable naming the broker.</summary>
    public const string Variable = "PARLEY_BROKER";

    /// <summary>
    /// The port the broker listens on when nothing says otherwise, and the first a project is
    /// given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is outside both operating systems' ephemeral port ranges</b>, which are where the
    /// operating system hands out ports a broker could fail to bind at logon. <c>PortLedger</c>
    /// allocates projects from a band clear of those ranges.
    /// </para>
    /// <para>
    /// <b>It is the band's first port, which makes the default true rather than merely
    /// sensible.</b> The first project a machine names is given this port, so "the broker to talk
    /// to when nothing says otherwise" and "the broker this machine has" are the same address on
    /// the ordinary machine. A deployment that set its own band in <c>ports.json</c>, or whose
    /// first project has been removed, is the case this cannot cover — and that is what
    /// <c>PARLEY_BROKER</c> and <c>--project</c> are for.
    /// </para>
    /// <para>
    /// Defined here and read by <c>PortLedger</c> rather than the other way round: the band's
    /// extent is an allocation policy and belongs with the allocator, but which port a deployment
    /// answers on is vocabulary, and this project is where the vocabulary lives.
    /// </para>
    /// </remarks>
    public const int DefaultPort = 19100;

    /// <summary>The address used when nothing says otherwise.</summary>
    public static string Default { get; } =
        $"http://127.0.0.1:{DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Reads where the broker is.
    /// </summary>
    /// <param name="read">How to read an environment variable.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The variable is set to something unusable.</exception>
    /// <remarks>
    /// <para>
    /// A bare host and port is accepted and given the <c>http</c> scheme, because that is what
    /// people type and because there is only one scheme available: the transport is plaintext
    /// HTTP/2 by §16's reasoning, so <c>https</c> would be a promise nothing keeps.
    /// </para>
    /// <para>
    /// A value that is set and unusable throws rather than falling back to the default. Somebody
    /// who wrote an address meant to reach a particular broker, and silently using a different
    /// one would put an agent in the wrong deployment — where its peers are absent, its name may
    /// belong to somebody else, and nothing looks broken.
    /// </para>
    /// </remarks>
    public static string Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (read(Variable) is not { Length: > 0 } configured)
        {
            return Default;
        }

        var candidate = configured.Contains("://", StringComparison.Ordinal)
            ? configured
            : $"http://{configured}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{Variable} is set to '{configured}', which is not an address. Give it a host " +
                $"and port, such as '{Default}' or 'broker.example:{DefaultPort}'.");
        }

        return uri.ToString().TrimEnd('/');
    }

    /// <summary>Whether nothing off this machine can address an endpoint.</summary>
    /// <param name="endpoint">The endpoint a project records.</param>
    /// <returns><see langword="true"/> when only this machine can reach it.</returns>
    /// <remarks>
    /// <para>
    /// <b>Here rather than beside either caller, because there are two and the answer must not
    /// differ.</b> The broker warns on every start when a project is not loopback, and the listing
    /// says of each project whether anything off the machine can address it — one rule, asked in
    /// two places, and two copies of it would agree until one was edited.
    /// </para>
    /// <para>
    /// <b>No I/O, which is what lets it live here.</b> It parses a string and asks
    /// <see cref="System.Net.IPAddress"/> about the result; nothing is resolved, connected to or
    /// looked up. A host name that is not an IP literal is therefore *not* loopback as far as this
    /// is concerned even if it resolves to one — deliberately, because resolution is a fact about
    /// this machine's configuration at this moment and the question being asked is about the
    /// address as written.
    /// </para>
    /// <para>
    /// <b>An endpoint nothing can parse is treated as loopback</b>, which is the safe answer for
    /// both callers: a broker will not bind it either, so the start fails with its own message, and
    /// warning about exposure as well would name the wrong fault.
    /// </para>
    /// </remarks>
    public static bool OnlyThisMachine(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address))
        {
            return true;
        }

        return string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(address.Host, out var parsed)
                && System.Net.IPAddress.IsLoopback(parsed));
    }
}
