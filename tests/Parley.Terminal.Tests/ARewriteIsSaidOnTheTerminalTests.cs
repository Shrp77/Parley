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

        // Destroyed from somewhere else, as another surface would: the seat's stream is what tells this one.
        _ = await seated.Operators.DestroyAsync("pricing", ct);

        for (var tries = 0; tries < 50 && seated.Shell.Said != Shell.TidiedSaid; tries++)
        {
            await Task.Delay(100, ct);
        }

        Assert.Equal(Shell.TidiedSaid, seated.Shell.Said);
        Assert.False(seated.Shell.Held!.Tidying);
    }

    [Fact]
    public void What_is_said_while_it_rewrites_promises_a_wait_not_a_failure()
    {
        // The whole point of saying it (owner's decision): nothing is refused, so the sentence must not read as one.
        Assert.Contains("waits and goes through when it finishes", Shell.TidyingSaid, StringComparison.Ordinal);
        Assert.DoesNotContain("try again", Shell.TidyingSaid, StringComparison.OrdinalIgnoreCase);
    }
}
