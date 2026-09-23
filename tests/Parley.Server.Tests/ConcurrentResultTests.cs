using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That one exchange cannot be answered twice, however closely the two answers arrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review gate, lens 1.</b> <c>ConcurrencyTests</c> probes simultaneous sends, simultaneous attaches
/// and two sessions of one agent sending together. What it does not ask is whether the **turn** rules
/// survive two callers arriving at once — and those are the rules SPEC.md §5.3 rests on: a Result answers
/// the exchange and closes it, so a second one has nothing to answer.
/// </para>
/// <para>
/// <b>Why concurrency is the interesting case rather than repetition.</b> Sending a Result and then
/// another is already refused, and is a check anybody would write. The question worth asking is whether
/// the refusal is a property of the exchange's recorded state or merely of the order two calls happened
/// to arrive in — because an agent with two sessions, or one retrying a call whose reply it never saw,
/// produces exactly this. If both are admitted the transcript holds two answers to one ask, and §5.3's
/// closure is decided by whichever wrote last.
/// </para>
/// <para>
/// <b>Repeated, because one attempt at a race proves nothing.</b> The window between reading an
/// exchange's state and writing the answer is short, so a single pass can miss it and report a guarantee
/// that is not there — this repository's practice for a suspected race is many attempts.
/// </para>
/// </remarks>
public sealed class ConcurrentResultTests
{
    private const int Attempts = 25;

    [Fact]
    public async Task Two_results_arriving_together_answer_one_exchange_exactly_once()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var admittedTwice = 0;
        var admittedNever = 0;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var sent = Assert.Single(
                (await docs.Client.RequestAsync(
                    Requests.Open(docs.Name, infra.Name, $"who owns dns, attempt {attempt}"),
                    cancellationToken: token)).Outcomes);

            // The legitimate receiver, answering twice at once. Not a forgery and not a third party —
            // both calls are entitled to be made, and exactly one of them may be admitted.
            var first = infra.Client.ResultAsync(
                Requests.Result(infra.Name, sent.ExchangeId), cancellationToken: token);

            var second = infra.Client.ResultAsync(
                Requests.Result(infra.Name, sent.ExchangeId), cancellationToken: token);

            var replies = await Task.WhenAll(first.ResponseAsync, second.ResponseAsync);

            var admitted = replies.Count(reply => reply.Ok);

            if (admitted > 1)
            {
                admittedTwice++;
            }
            else if (admitted == 0)
            {
                admittedNever++;
            }
        }

        // **Both failures are named, because they are different defects.** Two admitted is one exchange
        // with two answers. None admitted is a legitimate answer lost to a race with itself, which is
        // worse for the agent that sent it: the work was done and the exchange is still owed.
        Assert.True(
            admittedTwice == 0,
            $"{admittedTwice} of {Attempts} exchanges were answered twice");

        Assert.True(
            admittedNever == 0,
            $"{admittedNever} of {Attempts} exchanges refused both answers, losing a Result that was owed");
    }
}
