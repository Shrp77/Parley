using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>A read that lands after the person moved to another project opens nothing over it.</summary>
/// <remarks>
/// The Inbox had the guard; the exchanges a message names, the still-open list and Media did not, and opened one project's
/// words over another's (review, lens 1 run C3 on ae1d0e0f).
/// </remarks>
[Integration]
public sealed class AReadThatLandsInAnotherProjectOpensNothingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task HeldThenMovedAsync(Seated seated, Action press)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        seated.Shell.Queued(_ => gate.Task);
        press();

        await seated.Shell.GoAsync(Parley.Client.Views.ViewPlace.Into(Seated.Beta));

        gate.SetResult();
        await seated.Shell.Settled;
    }

    [Fact]
    public async Task References_read_after_moving_to_beta()
    {
        await using var seated = await Seated.OpenAsync(Ct, withBeta: true);

        var first = await seated.AsksAsync(Seated.Orders, Seated.Billing, "Which ledger?", "ledgers", Ct);
        _ = await seated.AsksAsync(Seated.Orders, seated.Person, $"See exchange #{first}; it matters.", "notes", Ct);

        await Walking.GoAsync(seated.Shell, Viewing.Conversation, "notes");
        await seated.Shell.Settled;

        var export = await seated.Operators.ExportAsync("notes", Ct);
        var naming = export.Messages.Single(m => m.PayloadJson.Contains("it matters", StringComparison.Ordinal));

        Assert.True(seated.Shell.Transcript.Reach(naming.Id));
        seated.Focus<TranscriptRegion>();

        await HeldThenMovedAsync(seated, () => seated.Session.Press(new Key(Shell.ReferenceKey).WithAlt));

        var shown = seated.Shell.ExchangeShown;

        if (shown is not null)
        {
            Prompting.Close(seated.Session.App, seated.Shell.ExchangeSession);
        }

        Assert.Equal(Seated.Beta, seated.Shell.Place.Project);
        Assert.Null(shown is null ? null : string.Join(" | ", shown.Shown));
    }

    [Fact]
    public async Task Still_open_read_after_moving_to_beta()
    {
        await using var seated = await Seated.OpenAsync(Ct, withBeta: true);

        seated.Focus<RoomRegion>();

        await HeldThenMovedAsync(seated, () => seated.Session.Press(new Key(Shell.StillOpenKey).WithAlt));

        var shown = seated.Shell.StillOpenShown;
        var said = seated.Shell.Said ?? string.Empty;

        if (shown is not null)
        {
            Prompting.Close(seated.Session.App, seated.Shell.StillOpenSession);
        }

        Assert.Equal(Seated.Beta, seated.Shell.Place.Project);

        // The round went out before the person moved, so it is still said; only its list is not opened over beta.
        Assert.True(
            said.StartsWith("Asked about", StringComparison.Ordinal) || said.StartsWith("Nobody was asked.", StringComparison.Ordinal),
            $"the round that went out was not said; the status line read: {said}");
        Assert.Null(shown is null ? null : string.Join(" | ", shown.Shown));
    }

    [Fact]
    public async Task Media_read_after_moving_to_beta()
    {
        await using var seated = await Seated.OpenAsync(Ct, withBeta: true);

        seated.Focus<TranscriptRegion>();

        await HeldThenMovedAsync(seated, () => seated.Session.Press(new Key('e').WithAlt));

        var open = seated.Session.Has(Media.Title);
        var said = seated.Shell.Said;

        Assert.Equal(Seated.Beta, seated.Shell.Place.Project);
        Assert.False(open, $"media chooser open over beta; said: {said}");
    }
}
