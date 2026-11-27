using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley;

/// <summary>Places a file over a placement call, the one way a client sends one (SPEC.md §9.5).</summary>
/// <remarks>
/// <para>
/// One file linked into every project that places, for the reason <see cref="InboxCollection"/> is: the generated
/// transport holds no hand-written code, and the adapter and the client library share no other project.
/// </para>
/// <para>
/// <b>No bytes before the go-ahead.</b> The header goes alone, and the broker answers it with a go-ahead or a refusal.
/// Bytes sent on a call the broker has already ended make the server end the whole connection, and with it every other
/// call on it: the adapter's attachment, the person's seat. A refusal that comes while the bytes are going stops the
/// sending, and the broker reads to the end of the stream before it ends the call.
/// </para>
/// <para>
/// <b>A dropped connection is said as what it is.</b> The broker commits nothing until the stream has ended, so a call
/// lost before the whole file was sent placed nothing, and one lost after may have placed it.
/// </para>
/// </remarks>
internal static class FilePlacement
{
    /// <summary>Sends the header, waits for the go-ahead, streams the content and returns the broker's reply.</summary>
    /// <param name="call">A new placement call, agent's or person's.</param>
    /// <param name="header">The header.</param>
    /// <param name="content">The bytes; read only after the go-ahead.</param>
    /// <param name="cancellationToken">Cancels the placement; nothing is placed by a cancelled one.</param>
    /// <returns>The broker's reply: placed, already current, or refused.</returns>
    /// <exception cref="RpcException">The broker refused the call, or could not be reached before it answered.</exception>
    /// <exception cref="DroppedException">The connection was lost after the broker answered the header.</exception>
    /// <exception cref="IOException">The content could not be read; nothing was placed.</exception>
    internal static async Task<Wire.PlaceFileReply> PlaceAsync(
        this AsyncDuplexStreamingCall<Wire.PlaceFileFrame, Wire.PlaceFileAnswer> call,
        Wire.PlaceFileHeader header,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(content);

        // Before the broker has answered, a status is its refusal or no broker at all, and comes as gRPC says it. What
        // the socket says instead is a dropped connection, with nothing sent but the header.
        bool first;

        try
        {
            await call.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Header = header }, cancellationToken)
                .ConfigureAwait(false);

            first = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not RpcException && IsDrop(failure, cancellationToken))
        {
            throw new DroppedException(sentWhole: false, failure);
        }

        if (!first)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "The broker ended the placement without answering."));
        }

        if (call.ResponseStream.Current.AnswerCase == Wire.PlaceFileAnswer.AnswerOneofCase.Reply)
        {
            // Refused before any bytes. Ending the stream lets the broker end the call.
            await EndAsync(call).ConfigureAwait(false);
            return call.ResponseStream.Current.Reply;
        }

        // Read while the bytes go, so a refusal partway stops them.
        var replied = call.ResponseStream.MoveNext(cancellationToken);
        _ = replied.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        var buffer = new byte[FileLimits.ChunkBytes];

        // Whether every byte went: a stream ended after a refusal, or after the connection went, did not carry the file.
        var whole = false;

        try
        {
            while (!replied.IsCompleted)
            {
                // The content's own failures are not the connection's, and pass as they are.
                var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    whole = true;
                    break;
                }

                await Sent(
                    () => call.RequestStream.WriteAsync(
                        new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(buffer, 0, read) },
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            await Sent(() => call.RequestStream.CompleteAsync(), cancellationToken).ConfigureAwait(false);
        }
        catch (DroppedException) when (replied.IsCompletedSuccessfully && replied.Result)
        {
            // The broker answered before the connection went: the answer stands.
        }

        bool answered;

        try
        {
            answered = await replied.ConfigureAwait(false);
        }
        catch (Exception failure) when (IsDrop(failure, cancellationToken))
        {
            throw new DroppedException(whole, failure);
        }

        if (!answered || call.ResponseStream.Current.AnswerCase != Wire.PlaceFileAnswer.AnswerOneofCase.Reply)
        {
            throw new DroppedException(whole, null);
        }

        return call.ResponseStream.Current.Reply;
    }

    // Ends the client's stream after a refusal. The refusal is already in hand, so a failure here changes nothing.
    private static async Task EndAsync(AsyncDuplexStreamingCall<Wire.PlaceFileFrame, Wire.PlaceFileAnswer> call)
    {
        try
        {
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (IsDrop(failure, CancellationToken.None))
        {
        }
    }

    // A write after the go-ahead, before the stream has ended: lost there, nothing was placed.
    private static async Task Sent(Func<Task> send, CancellationToken cancellationToken)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (Exception failure) when (IsDrop(failure, cancellationToken))
        {
            throw new DroppedException(sentWhole: false, failure);
        }
    }

    // What a lost connection surfaces as, from a write or a read on a call that was going. A status the broker set is
    // its answer, never a drop; the caller's own cancellation is the caller's.
    private static bool IsDrop(Exception failure, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && failure switch
        {
            RpcException status => status.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.Cancelled
                                   || string.IsNullOrWhiteSpace(status.Status.Detail),
            _ => failure is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException
                 or OperationCanceledException,
        };

    /// <summary>The connection to the broker was lost while a placement was going.</summary>
    /// <param name="sentWhole">Whether the whole file had been sent, so the broker may have placed it.</param>
    /// <param name="cause">What the transport said.</param>
    internal sealed class DroppedException(bool sentWhole, Exception? cause)
        : Exception("The connection to the broker was lost during a placement.", cause)
    {
        /// <summary>Gets a value indicating whether the whole file had been sent, so it may have been placed.</summary>
        internal bool SentWhole { get; } = sentWhole;
    }
}
