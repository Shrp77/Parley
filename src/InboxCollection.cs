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
/// <b>The reply is the caller's only once the broker has recorded it delivered</b>, which it says by ending the call. A
/// collection cancelled or broken before then is handed over again by the next one, so a caller that shows nothing
/// until this returns shows everything exactly when it is delivered. The one way to lose a collection is to give up
/// after the acknowledgement has been sent and before the broker's answer arrives.
/// </para>
/// </remarks>
internal static class InboxCollection
{
    /// <summary>Collects what is waiting and acknowledges it.</summary>
    /// <param name="agents">The agent client.</param>
    /// <param name="request">Who is collecting, and the limit.</param>
    /// <param name="cancellationToken">Cancels the collection; before the acknowledgement, nothing is lost by it.</param>
    /// <returns>The collection, recorded delivered.</returns>
    /// <exception cref="RpcException">The broker refused the collection or ended it without a reply.</exception>
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

        await call.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Collect = request }, cancellationToken)
            .ConfigureAwait(false);

        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "The broker ended the collection without a reply."));
        }

        var reply = call.ResponseStream.Current;

        await call.RequestStream.WriteAsync(
            new Wire.DrainInboxFrame { Acknowledge = new Wire.InboxAcknowledgement() },
            CancellationToken.None).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);

        // The broker ends the call once what it handed over is recorded delivered; a failure there arrives here.
        while (await call.ResponseStream.MoveNext(CancellationToken.None).ConfigureAwait(false))
        {
        }

        return reply;
    }
}
