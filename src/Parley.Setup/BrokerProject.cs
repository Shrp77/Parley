using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Setup;

/// <summary>Which project a broker is, as an operator's tool on a machine holding no project of its own can tell (SPEC.md §4.1.2).</summary>
/// <remarks>
/// <para>
/// <b>Read from the person's seat, because the protocol has no concept of a project</b> (§4.1): membership is which endpoint an
/// agent reaches. Every project's registry holds <c>human-user@&lt;project&gt;</c>, and a client comparing that name with the one it
/// was given sends the broker nothing new and asks it to decide nothing.
/// </para>
/// <para>
/// <b>A help to a person setting a machine up, and no guard.</b> An address is a port number, and the wrong one reaches a working
/// broker whose roster looks entirely normal, so enrolling and installing an entry look first. An adapter does not: it
/// connects to the address its block gives, because that is what membership is.
/// </para>
/// </remarks>
public static class BrokerProject
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Which project the broker at an address serves.</summary>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <returns>The project, or null with why it could not be asked.</returns>
    public static async Task<(string? Project, string? Problem)> AskAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        string endpoint;

        try
        {
            endpoint = BrokerEndpoint.Read(_ => address);
        }
        catch (InvalidOperationException invalid)
        {
            return (null, invalid.Message);
        }

        try
        {
            using var channel = GrpcChannel.ForAddress(endpoint);

            var registry = await new Wire.Operators.OperatorsClient(channel)
                .DescribeRegistryAsync(new Wire.Empty(), deadline: DateTime.UtcNow + Patience, cancellationToken: cancellationToken)
                .ResponseAsync.ConfigureAwait(false);

            var seat = registry.Rows
                .Select(row => row.Name)
                .FirstOrDefault(name => name.StartsWith(PersonSeat.Role + "@", StringComparison.Ordinal));

            return seat is null
                ? (null, $"The broker at {endpoint} has no seat for a person, so which project it is cannot be told.")
                : (seat[(PersonSeat.Role.Length + 1)..], null);
        }
        catch (RpcException failure)
        {
            return (null, $"No broker answered at {endpoint} ({failure.StatusCode}). Is the tunnel up?");
        }
    }

    /// <summary>The project a machine reaches by address, once its broker has said it serves that project.</summary>
    /// <param name="name">The project named.</param>
    /// <param name="address">The address given.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <returns>The project, or null with why not.</returns>
    /// <remarks>
    /// <b>A project with no store</b>, which is what makes it this kind: <see cref="ReachedByAddress"/> reads that, and nothing
    /// on this machine ever opens one.
    /// </remarks>
    public static async Task<(ProjectSettings? Project, string? Problem)> ConfirmAsync(
        string name,
        string address,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!ProjectName.TryCreate(name, out var project))
        {
            return (null, $"'{name}' is not a project's name.");
        }

        var (served, problem) = await AskAsync(address, cancellationToken).ConfigureAwait(false);

        if (served is null)
        {
            return (null, problem);
        }

        return string.Equals(served, name, StringComparison.Ordinal)
            ? (new ProjectSettings(project, string.Empty, BrokerEndpoint.Read(_ => address), null), null)
            : (null, $"The broker at {BrokerEndpoint.Read(_ => address)} serves project '{(ProjectName.TryCreate(served, out var named) ? named.Value : "with a name that cannot be shown")}', not '{name}'. Nothing has been changed.");
    }

    /// <summary>Whether this machine reaches a project by address and holds nothing of it.</summary>
    /// <param name="project">The project.</param>
    /// <returns><see langword="true"/> when it has no store here.</returns>
    public static bool ReachedByAddress(ProjectSettings? project) =>
        project is { Store.Length: 0 };

}
