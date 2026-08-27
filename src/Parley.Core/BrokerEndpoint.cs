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
    /// The port the broker listens on when nothing says otherwise.
    /// </summary>
    /// <remarks>
    /// Chosen from the IANA dynamic range, where nothing is registered and nothing is expected
    /// to be — a number in the registered range would eventually collide with something that had
    /// a better claim to it.
    /// </remarks>
    public const int DefaultPort = 51707;

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
