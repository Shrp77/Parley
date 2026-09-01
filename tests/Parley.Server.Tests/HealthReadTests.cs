using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The five reads a console's health page makes, over a real socket, against a fresh store.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these was implemented, unit-tested against a repository, and bound to the wire —
/// and the page that asks for them failed on a deployment two minutes old, because nothing had
/// ever asked for them through the transport. A fresh store is the case: no refusals, no
/// clarifications, no referrals, nothing sent, nobody waiting. Empty is what a broker answers
/// with most of its life and the state least likely to have been tried.
/// </para>
/// <para>
/// Found by starting the console and opening the page rather than by reading anything. That is
/// the argument for walking a quickstart: a suite that passes and a program that runs are
/// different claims.
/// </para>
/// </remarks>
public sealed class HealthReadTests
{
    [Fact]
    public async Task Every_health_read_answers_on_a_deployment_that_has_done_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var operators = broker.ConnectAsOperator();
        var failures = new List<string>();

        await Attempt("refusals", async () =>
            await operators.DescribeRefusalsAsync(new Wire.Empty(), cancellationToken: token));

        await Attempt("clarifications", async () =>
            await operators.DescribeClarificationsAsync(
                new Wire.Empty(), cancellationToken: token));

        await Attempt("referrals", async () =>
            await operators.DescribeReferralsAsync(new Wire.Empty(), cancellationToken: token));

        await Attempt("notifications", async () =>
            await operators.DescribeNotificationsAsync(
                new Wire.Empty(), cancellationToken: token));

        await Attempt("waiting", async () =>
            await operators.DescribeWaitingAsync(new Wire.Empty(), cancellationToken: token));

        Assert.True(
            failures.Count == 0,
            "a health read the console makes does not answer on an empty deployment: "
            + string.Join("; ", failures));

        // Reported together rather than one assertion each, so a run says which of the five are
        // broken instead of stopping at the first.
        async Task Attempt(string named, Func<Task> read)
        {
            try
            {
                await read();
            }
            catch (RpcException failure)
            {
                failures.Add(
                    $"{named} -> {failure.StatusCode} {failure.Status.Detail}".Trim());
            }
        }
    }

    [Fact]
    public async Task The_registry_and_the_conversation_list_answer_on_an_empty_store_too()
    {
        // The two reads a console makes before anything else. An empty registry is what every
        // deployment has for its first few minutes, and it is what an operator is looking at
        // while they follow the registration instructions.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var operators = broker.ConnectAsOperator();

        var registry = await operators.DescribeRegistryAsync(
            new Wire.Empty(), cancellationToken: token);

        var conversations = await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest(), cancellationToken: token);

        Assert.Empty(registry.Rows);
        Assert.Empty(conversations.Conversations);
    }
}
