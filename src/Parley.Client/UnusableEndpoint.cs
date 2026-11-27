using Grpc.Core;

namespace Parley.Client;

/// <summary>The invoker a client is given when its project's endpoint cannot be dialled: every call fails as unavailable.</summary>
/// <remarks>
/// <para>
/// <b>Making a client never throws.</b> An endpoint the manifest accepts and one gRPC can dial are not the same set
/// ("localhost:5301", with no scheme, is the usual way to find out), and opening the channel for such an endpoint throws.
/// Thrown where the client is made, it escaped every guard around the call that followed: one project's pages answered
/// with an error and the terminal drew no project at all. Thrown as the call is made, it reaches the caller as a broker
/// that is unavailable, which every read already contains (<see cref="BrokerDirectory.ReadAsync{T}"/>).
/// </para>
/// </remarks>
/// <param name="reason">Why the endpoint cannot be dialled, from the failed open.</param>
internal sealed class UnusableEndpoint(string reason) : CallInvoker
{
    /// <summary>The start of the detail a call carries when its endpoint cannot be dialled as written.</summary>
    internal const string Said = "This project's endpoint cannot be reached as written: ";

    /// <inheritdoc/>
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw Failure();

    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw Failure();

    /// <inheritdoc/>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw Failure();

    /// <inheritdoc/>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) => throw Failure();

    /// <inheritdoc/>
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) => throw Failure();

    private RpcException Failure() => new(new Status(StatusCode.Unavailable, Said + reason));
}
