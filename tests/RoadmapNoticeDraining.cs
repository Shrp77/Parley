using Parley.Core;
using Parley.Store;

namespace Parley;

/// <summary>Roadmap notices read and marked delivered in one act, as an acknowledged collection does.</summary>
internal static class RoadmapNoticeDraining
{
    internal static async Task<IReadOnlyList<RoadmapNotice>> DrainNoticesAsync(
        this RoadmapRepository repository,
        AgentName agent,
        int limit,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var (notices, ids) = await repository.PeekNoticesAsync(agent, limit, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await repository.MarkNoticesDeliveredAsync(ids, nowUtc, cancellationToken).ConfigureAwait(false);

        return notices;
    }
}
