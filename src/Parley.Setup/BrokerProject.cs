using Grpc.Core;
using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Setup;

/// <summary>Asks a broker which project it serves, for a machine that holds no project of its own (SPEC.md §4.1.2).</summary>
/// <remarks>
/// <b>Asked, because the name is the only check there is.</b> A machine with only a tunnel has nothing to look a project up
/// in, and an address is a port number: the wrong one reaches a working broker, whose roster looks entirely normal.
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

            var reply = await new Wire.Nodes.NodesClient(channel)
                .ServedProjectAsync(new Wire.Empty(), deadline: DateTime.UtcNow + Patience, cancellationToken: cancellationToken)
                .ResponseAsync.ConfigureAwait(false);

            return (reply.Project, null);
        }
        catch (RpcException failure)
        {
            return (null, $"No broker answered at {endpoint}: {failure.Status.Detail} Is the tunnel up?");
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
            : (null, $"The broker at {address} serves project '{served}', not '{name}'. Nothing has been changed.");
    }

    /// <summary>Whether this machine reaches a project by address and holds nothing of it.</summary>
    /// <param name="project">The project.</param>
    /// <returns><see langword="true"/> when it has no store here.</returns>
    public static bool ReachedByAddress(ProjectSettings? project) =>
        project is { Store.Length: 0 };

    /// <summary>The address in this machine's block, when a repository names exactly one and nothing else was named.</summary>
    /// <param name="workingDirectory">Where the command is running.</param>
    /// <returns>The address, or null.</returns>
    /// <remarks>
    /// So a person inside the repository need not repeat what its file already says, the way a project it names works
    /// today. Only one: with two there is a question, and <c>ProjectResolution</c> answers questions by asking.
    /// </remarks>
    public static string? AddressInRepository(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)
            || !LaunchConfiguration.TryRead(workingDirectory, out var declared, out _)
            || declared.Machines is null
            || !declared.Machines.TryGetValue(MachineName.Current, out var blocks))
        {
            return null;
        }

        var addresses = blocks.Values.Where(block => block.Broker is not null).ToList();

        return addresses.Count == 1 ? addresses[0].Broker : null;
    }
}
