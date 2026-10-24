using Grpc.Core;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// The roadmap proposals awaiting a person in a project, for the pane that lists them (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Unknown is not none.</b> A broker that did not answer has unknown proposals, and an empty list would read as a
/// project with nothing for the person to decide. So this returns <see langword="null"/> and the caller leaves what it
/// was showing. A surface counts with <see cref="BrokerClient.ProposalCountAsync"/> and the same rule.
/// </para>
/// <para>
/// <b>A list reads headings</b>, the newest <see cref="RoadmapProposal.HeadingLimit"/>, because it runs on every
/// refresh, roadmap text has no bound and nothing bounds how many proposals an agent makes. A proposal is read whole
/// when it is opened.
/// </para>
/// </remarks>
public static class ProposalCount
{
    /// <summary>The headings of the proposals awaiting a decision, or null when they could not be read.</summary>
    /// <param name="broker">The project's operator client.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The proposals, which may be none; null when the broker could not say.</returns>
    /// <remarks>
    /// Null is not none: a surface that could not ask must not
    /// empty the list it was showing.
    /// </remarks>
    public static async Task<IReadOnlyList<RoadmapProposal>?> ListAsync(
        BrokerClient broker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        try
        {
            return await broker.ProposalsAsync(ProposalState.Waiting, headings: true, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException)
        {
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
