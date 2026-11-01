using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>That the terminal says when its project's broker is rewriting its store, and when it has finished (SPEC.md §9.1).</summary>
/// <remarks>
/// Found by review, lens 1 run 115: a rewrite holds every call for seconds on a large store. The owner decided nothing is
/// refused meanwhile and the person is told, so a send that waits reads as a wait. The seat's stream says it; the
/// terminal's message line shows it.
/// </remarks>
[Integration]
public sealed class ARewriteIsSaidOnTheTerminalTests
{
    [Fact]
    public async Task A_destroy_anywhere_in_the_project_is_said_while_it_rewrites_and_when_it_has_finished()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seated = await Seated.OpenAsync(ct);

        _ = await seated.AsksAsync(Seated.Orders, Seated.Billing, "Is the total gross or net?", "pricing", ct);

        // What the line said while the rewrite ran (lens 3 run 128: only the end was held). Read from a handler added
        // after the shell's own, so it runs once the shell has said its piece, while the seat still says underway.
        var whileItRan = new List<string>();
        var seat = seated.Shell.Held!;
        seat.TidyingChanged += (_, _) =>
        {
            if (seat.Tidying)
            {
                lock (whileItRan)
                {
                    whileItRan.Add(seated.Shell.Said);
                }
            }
        };

        // Destroyed from somewhere else, as another surface would: the seat's stream is what tells this one.
        _ = await seated.Operators.DestroyAsync("pricing", ct);

        // Up to fifteen seconds: a hosted runner can be slow to deliver the stream's two events.
        for (var tries = 0; tries < 150 && seated.Shell.Said != Shell.TidiedSaid; tries++)
        {
            await Task.Delay(100, ct);
        }

        string heard;

        lock (whileItRan)
        {
            heard = string.Join(" | ", whileItRan);
        }

        Assert.True(
            seated.Shell.Said == Shell.TidiedSaid,
            $"said '{seated.Shell.Said}'; the seat is {(seat.Lost ? "lost" : "held")}, tidying {seat.Tidying}, the shell holds "
            + $"{(ReferenceEquals(seated.Shell.Held, seat) ? "the same seat" : "another seat")}; said while it ran: '{heard}'");
        Assert.False(seated.Shell.Held!.Tidying);

        lock (whileItRan)
        {
            Assert.Equal([Shell.TidyingSaid], whileItRan);
        }
    }

    [Fact]
    public void What_is_said_while_it_rewrites_promises_a_wait_not_a_failure()
    {
        // The whole point of saying it (owner's decision): nothing is refused, so the sentence must not read as one.
        Assert.Contains("waits and goes through when it finishes", Shell.TidyingSaid, StringComparison.Ordinal);
        Assert.DoesNotContain("try again", Shell.TidyingSaid, StringComparison.OrdinalIgnoreCase);
    }
}
