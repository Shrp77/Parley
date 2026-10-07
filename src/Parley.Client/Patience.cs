using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Parley.Client;

/// <summary>Gives every single-reply call to a broker a deadline when its caller set none.</summary>
/// <remarks>
/// <para>
/// <b>A broker that accepts a connection and never answers held a surface for ever</b> (the combined review gate, lens 1,
/// run 4). The connect timeout covers only the connection; a call over it waited without end, and the terminal runs its
/// work one item at a time — so one wedged broker stopped every send, open and toggle in every project, with nothing on
/// screen saying why. A wedged process still answers HTTP/2 keep-alive pings, so the connection never looked dead either.
/// </para>
/// <para>
/// Thirty seconds, which nothing an operator or a seat asks for should approach on a broker that is working. Streams are left alone: a seat's attachment is meant to stay open.
/// </para>
/// </remarks>
/// <param name="ordinary">The longest an ordinary call may take; a test's seam, never set by a surface.</param>
internal sealed class Patience(TimeSpan? ordinary = null) : Interceptor
{
    /// <summary>The longest any ordinary call may take.</summary>
    internal static readonly TimeSpan Ordinary = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        if (context.Options.Deadline is null)
        {
            var allowed = ordinary ?? Ordinary;

            context = new ClientInterceptorContext<TRequest, TResponse>(
                context.Method, context.Host, context.Options.WithDeadline(DateTime.UtcNow.Add(allowed)));
        }

        return continuation(request, context);
    }
}
