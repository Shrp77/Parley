using Parley.Broker;
using Parley.Core;

namespace Parley;

/// <summary>A collection taken and acknowledged in one act, as a caller that received its reply does.</summary>
/// <remarks>
/// In process there is no reply to lose, so a test that is not about acknowledgement collects this way and reads the
/// broker as it was before collections were acknowledged. A test about acknowledgement calls
/// <see cref="Broker.Broker.CollectInboxAsync"/> and <see cref="Broker.Broker.AcknowledgeInboxAsync"/> itself.
/// </remarks>
internal static class BrokerInboxDraining
{
    internal static async Task<Inbox> DrainInboxAsync(
        this Broker.Broker broker,
        AgentName agent,
        DateTimeOffset nowUtc,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var holds = new InboxHolds();
        var (inbox, hold) = await broker.CollectInboxAsync(agent, holds, limit, cancellationToken).ConfigureAwait(false);

        await broker.AcknowledgeInboxAsync(hold, nowUtc, cancellationToken).ConfigureAwait(false);
        holds.Release(hold);

        return inbox;
    }
}
