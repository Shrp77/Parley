using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// What the operator surface answers when its own operator sends a blank conversation identifier.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guard used to live on the command line, and 019 removed the commands it served.</b>
/// `parley export ""` once reached the repository, threw on the way in, and printed a .NET stack
/// trace with exit 127 — the operator's own input coming back at them as a crash. It was fixed
/// there, inside a helper that only the sixteen removed verbs called, so removing them took the
/// guard with them and the defect reappeared one process along.
/// </para>
/// <para>
/// <b>Worse there than it had been here.</b> A shell printing a trace is at least obviously the
/// tool's fault; a broker answering <c>Unknown: Exception was thrown by handler</c> says nothing an
/// operator can act on and reads like a fault in the deployment. Neither the terminal nor the web UI
/// can send a blank — both pass an identifier from a row somebody selected — so it is reachable only
/// by a client written against the service directly, which is exactly the caller with the least
/// context for an internal error.
/// </para>
/// <para>
/// <b>Found by a probe rather than by reading.</b> The test that had held the old guard was being
/// re-pointed at the broker, and failed against real behaviour. That is the argument for the rule
/// living in the service: one enforced by whichever client happens to carry it leaves with that
/// client.
/// </para>
/// </remarks>
public sealed class BlankIdentifierTests
{
    public static TheoryData<string> Blank => ["", "   ", "\t"];

    private static async Task<StatusCode> RefusedAsync(
        Func<Wire.Operators.OperatorsClient, string, CancellationToken, Task> ask,
        string identifier,
        CancellationToken token)
    {
        await using var broker = await LoopbackBroker.StartAsync(token);

        var failure = await Assert.ThrowsAsync<RpcException>(
            () => ask(broker.ConnectAsOperator(), identifier, token));

        return failure.StatusCode;
    }

    [Theory]
    [MemberData(nameof(Blank))]
    public async Task Exporting_nothing_is_refused_rather_than_thrown(string identifier)
    {
        // `InvalidArgument` rather than `Unknown`: the caller sent something wrong, and saying which
        // is the whole difference between a message an operator can act on and one that sends them
        // looking for a broken broker.
        Assert.Equal(
            StatusCode.InvalidArgument,
            await RefusedAsync(
                (client, id, token) => client.ExportAsync(
                    new Wire.ExportRequest { ConversationId = id }, cancellationToken: token).ResponseAsync,
                identifier,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Blank))]
    public async Task Concluding_nothing_is_refused_rather_than_thrown(string identifier) =>
        Assert.Equal(
            StatusCode.InvalidArgument,
            await RefusedAsync(
                (client, id, token) => client.ForceConcludeAsync(
                    new Wire.ForceConcludeRequest { ConversationId = id }, cancellationToken: token).ResponseAsync,
                identifier,
                TestContext.Current.CancellationToken));

    [Theory]
    [MemberData(nameof(Blank))]
    public async Task Archiving_nothing_is_refused_rather_than_thrown(string identifier) =>
        Assert.Equal(
            StatusCode.InvalidArgument,
            await RefusedAsync(
                (client, id, token) => client.ArchiveAsync(
                    new Wire.ConversationRequest { ConversationId = id }, cancellationToken: token).ResponseAsync,
                identifier,
                TestContext.Current.CancellationToken));

    [Theory]
    [MemberData(nameof(Blank))]
    public async Task Unarchiving_nothing_is_refused_rather_than_thrown(string identifier) =>
        Assert.Equal(
            StatusCode.InvalidArgument,
            await RefusedAsync(
                (client, id, token) => client.UnarchiveAsync(
                    new Wire.ConversationRequest { ConversationId = id }, cancellationToken: token).ResponseAsync,
                identifier,
                TestContext.Current.CancellationToken));

    [Theory]
    [MemberData(nameof(Blank))]
    public async Task Destroying_nothing_is_refused_before_a_single_file_is_held(string identifier) =>
        // **The one that is asked before anything is held, and the reason it is.** Destroy takes a
        // hold over every file in the deployment before it does anything else, so a blank identifier
        // refused further down would stop every download in progress on its way to being told no.
        // The refusal is the visible half; that nothing was held is the half this names.
        Assert.Equal(
            StatusCode.InvalidArgument,
            await RefusedAsync(
                (client, id, token) => client.DestroyAsync(
                    new Wire.ConversationRequest { ConversationId = id }, cancellationToken: token).ResponseAsync,
                identifier,
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task An_identifier_that_names_nothing_is_still_an_answer_rather_than_a_refusal()
    {
        // **The control, and the boundary this guard is deliberately drawn at.** Blank is refused;
        // an identifier that is well formed and matches no conversation is not an error at all, it
        // is an empty export. Refusing that would make "no messages in '...'" unreachable and turn
        // every typo into a failure the caller cannot tell from a broken deployment.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var export = await broker.ConnectAsOperator().ExportAsync(
            new Wire.ExportRequest { ConversationId = "nothing-by-that-name" }, cancellationToken: token);

        Assert.Empty(export.Messages);
    }
}
