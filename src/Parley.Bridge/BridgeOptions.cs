using System.Globalization;

namespace Parley.Bridge;

/// <summary>
/// How this bridge was asked to run.
/// </summary>
/// <param name="DatabasePath">The store to watch. The same file every agent's server writes to.</param>
/// <param name="Address">The interface to listen on.</param>
/// <param name="Port">The port to listen on.</param>
/// <param name="Token">The secret a request must present.</param>
/// <remarks>
/// <para>
/// The default bind is loopback and changing it takes a deliberate flag, because this process
/// can start agents and type into them. That is remote code execution offered as a feature, and
/// the blast radius of getting it wrong is every repository in the fleet.
/// </para>
/// </remarks>
public sealed record BridgeOptions(string DatabasePath, string Address, int Port, string Token)
{
    /// <summary>The interface used when none is named.</summary>
    public const string Loopback = "127.0.0.1";

    /// <summary>The port used when none is named.</summary>
    public const int DefaultPort = 7777;

    /// <summary>
    /// Whether this bridge is reachable from another machine.
    /// </summary>
    /// <remarks>
    /// Worth a named property rather than a comparison at the call site: it is the fact the
    /// startup banner has to be loudest about, and the one a reader of that code should not have
    /// to work out from a string.
    /// </remarks>
    public bool IsExposed => !string.Equals(Address, Loopback, StringComparison.Ordinal);

    /// <summary>
    /// Reads the options from a command line and the environment.
    /// </summary>
    /// <param name="arguments">The command line, without the executable name.</param>
    /// <returns>The options, or the reason they could not be read.</returns>
    /// <remarks>
    /// <para>
    /// <c>PARLEY_DB</c> is read from the environment for the same reason every other Parley
    /// binary reads it: the operator's shell already has it set to whatever the agents were
    /// launched with, and a bridge watching a different file from the fleet would report an
    /// empty, healthy-looking deployment.
    /// </para>
    /// <para>
    /// The token is generated per run rather than persisted. A stored token is one that leaks
    /// into a dotfile, gets committed, and outlives the process it authorised; a fresh one is
    /// printed once, lives as long as the bridge, and cannot be replayed against the next.
    /// </para>
    /// </remarks>
    public static (BridgeOptions? Options, string? Error) Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var database = Environment.GetEnvironmentVariable("PARLEY_DB");
        var address = Loopback;
        var port = DefaultPort;

        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--db" when i + 1 < arguments.Count:
                    database = arguments[++i];
                    break;

                case "--port" when i + 1 < arguments.Count:
                    if (!int.TryParse(
                            arguments[++i], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                        || port is < 1 or > 65535)
                    {
                        return (null, $"'{arguments[i]}' is not a port number.");
                    }

                    break;

                // Spelled out rather than --host, so that nobody sets it while thinking they are
                // naming which machine to connect to. What it does is stop refusing strangers.
                case "--listen-on-all-interfaces":
                    address = "0.0.0.0";
                    break;

                default:
                    return (null, $"Unrecognised argument '{arguments[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            return (null,
                "No database. Pass --db <path>, or set PARLEY_DB to the same value the agents' " +
                "servers are launched with.");
        }

        return (new BridgeOptions(database, address, port, AccessToken.Create()), null);
    }
}
