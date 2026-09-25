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
    /// <b>This was 51707 until 019, and that number had become the one value guaranteed to be
    /// wrong.</b> It was chosen from the IANA dynamic range, on the reasoning that nothing is
    /// registered there — which is true and is exactly the problem: the dynamic range is where the
    /// operating system hands out ephemeral ports, so a broker asking for one is asking for a port
    /// the machine may already have given to somebody else. <c>PortLedger</c> carries the measurement
    /// that settled it: 51707 and 53283, the ports of the deployment that prompted 019, both sit
    /// inside Windows' 49152–65535 and would fail to bind at logon on any machine that had been up
    /// long enough.
    /// </para>
    /// <para>
    /// So 019 moved every project into a band clear of both operating systems' ephemeral ranges,
    /// and left this constant behind pointing into the range it had just abandoned. The default
    /// could then never reach anything: a machine with one project has it on 19100, and every
    /// command that fell back to this one spent two seconds failing to connect to 51707 before
    /// saying no broker was listening.
    /// </para>
    /// <para>
    /// <b>It is the band's first port now, which makes the default true rather than merely
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
}
