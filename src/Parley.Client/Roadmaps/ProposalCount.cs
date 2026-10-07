using Grpc.Core;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// How many roadmap proposals are awaiting a person in a project, for the places that count what needs them (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Unknown is not none.</b> A broker that did not answer, or that is older than roadmaps and answers
/// <c>UNIMPLEMENTED</c>, has an unknown number of proposals, and showing it as zero would read as a project with nothing
/// for the person to decide — which is the containment rule the project summary already holds itself to for agents and
/// conversations. So this returns <see langword="null"/> and the caller says nothing about proposals rather than that
/// there are none.
/// </para>
/// <para>
/// <b>One question for every surface</b>, so the projects view and the web project list cannot count differently.
/// </para>
/// </remarks>
public static class ProposalCount
{
    /// <summary>Counts the waiting proposals, or says they could not be counted.</summary>
    /// <param name="broker">The project's operator client.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number waiting, or <see langword="null"/> when the broker could not say.</returns>
    public static async Task<int?> WaitingAsync(BrokerClient broker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        try
        {
            return (await broker.ProposalsAsync(ProposalState.Waiting, cancellationToken).ConfigureAwait(false)).Count;
        }
        catch (RpcException)
        {
            // Unavailable, unimplemented or anything else: the count is unknown, and unknown is never shown as none.
            return null;
        }
    }

    /// <summary>What a count says, in words, beside the figures of a project.</summary>
    /// <param name="waiting">The count, or <see langword="null"/> when unknown.</param>
    /// <returns>An empty string when there is nothing to say, else a short phrase.</returns>
    public static string Says(int? waiting) =>
        waiting is > 0 and var n
            ? $"{n} roadmap{(n == 1 ? string.Empty : "s")} proposed, for you to decide"
            : string.Empty;
}
