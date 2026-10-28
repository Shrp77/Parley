using Grpc.Core;
using Wire = Parley.Transport;

namespace Parley;

/// <summary>Collects an inbox and acknowledges it, the one way a client takes delivery (SPEC.md §6).</summary>
/// <remarks>
/// <para>
/// One file linked into every project that collects, because the generated transport holds no hand-written code and
/// the adapter and the client library share no other project. A second way to collect would be a second place to
/// forget the acknowledgement.
/// </para>
/// <para>
/// <b>The reply is the caller's once it has arrived whole.</b> A collection cancelled or broken before then is handed
/// over again by the next one. After it, nothing is given up: the acknowledgement is sent, and the reply is returned
/// whatever becomes of the call, because the broker may have recorded it. If the broker had not, the next collection
/// hands it over again, so it arrives twice, which is what at least once means (SPEC.md §6). Only the caller's own
/// process ending between the reply and its use loses a collection.
/// </para>
/// </remarks>
internal static class InboxCollection
{
    /// <summary>Collects what is waiting and acknowledges it.</summary>
    /// <param name="agents">The agent client.</param>
    /// <param name="request">Who is collecting, and the limit.</param>
    /// <param name="cancellationToken">Cancels the collection; before the acknowledgement, nothing is lost by it.</param>
    /// <returns>The collection: recorded delivered, or, when the call broke after it arrived, kept anyway.</returns>
    /// <exception cref="RpcException">The broker refused the collection or ended it without a reply.</exception>
    /// <exception cref="OperationCanceledException">The caller gave the collection up before its reply arrived.</exception>
    internal static async Task<Wire.InboxReply> DrainInboxAsync(
        this Wire.Agents.AgentsClient agents,
        Wire.DrainInboxRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(request);

        // Cancellable until the reply has arrived, and not after: the call itself takes no token, so giving up once the
        // acknowledgement may be on its way cannot leave the broker recording a collection this caller then dropped.
        using var call = agents.DrainInbox();

        bool arrived;

        try
        {
            await call.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Collect = request }, cancellationToken)
                .ConfigureAwait(false);

            arrived = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException aborted) when (cancellationToken.IsCancellationRequested)
        {
            // Giving up can surface as the call aborted rather than cancelled, depending on where it lands. It is the
            // caller's own cancellation either way, never the broker being unreachable.
            throw new OperationCanceledException("The collection was given up before its reply arrived.", aborted, cancellationToken);
        }

        if (!arrived)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "The broker ended the collection without a reply."));
        }

        var reply = call.ResponseStream.Current;

        try
        {
            await call.RequestStream.WriteAsync(
                new Wire.DrainInboxFrame { Acknowledge = new Wire.InboxAcknowledgement() },
                CancellationToken.None).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);

            // The broker ends the call once what it handed over is recorded delivered.
            while (await call.ResponseStream.MoveNext(CancellationToken.None).ConfigureAwait(false))
            {
            }
        }
        catch (RpcException)
        {
            // Kept: the broker may have recorded it, and if it had not, the next collection brings it again.
        }

        return reply;
    }
}
