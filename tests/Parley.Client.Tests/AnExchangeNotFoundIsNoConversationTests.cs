using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That an exchange the broker cannot place reads as no conversation at all, never as a conversation with an empty name
/// (<c>BrokerClient.LocateExchangeAsync</c>, spec 036).
/// </summary>
/// <remarks>
/// The wire says "not found" with an empty string. Every caller (the exchange panel, the terminal's references, the Inbox's
/// read of what waits on the person) treats null as not found and anything else as a conversation to export, and the
/// broker refuses an export of an empty name, so a number that names nothing would read as the broker failing. Returning the
/// empty string passed every test, because each stand-in broker exports whatever it is asked for.
/// </remarks>
[Unit]
public sealed class AnExchangeNotFoundIsNoConversationTests
{
    [Fact]
    public async Task An_empty_answer_is_no_conversation()
    {
        var broker = new BrokerClient(new Wire.Operators.OperatorsClient(new Answering(new Wire.LocateExchangeReply())));

        Assert.Null(await broker.LocateExchangeAsync(999, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_named_answer_is_that_conversation()
    {
        var broker = new BrokerClient(new Wire.Operators.OperatorsClient(
            new Answering(new Wire.LocateExchangeReply { ConversationId = "pricing" })));

        Assert.Equal("pricing", await broker.LocateExchangeAsync(50, TestContext.Current.CancellationToken));
    }

    private sealed class Answering(object reply) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            new(
                Task.FromResult((TResponse)reply),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }
}
