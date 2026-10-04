using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>A flag in words, the same on every surface (SPEC.md §6).</summary>
/// <remarks>
/// <para>
/// <b>One implementation</b>, because the terminal's Health dialog and the web UI's health page each spelled every flag
/// themselves, and both ended in a default arm saying the agent "is retired" — so a kind either had not been taught would
/// have told a person an agent had been retired. 024 caught that once for the refused-permission flag; this ends the
/// shape for every kind.
/// </para>
/// <para>
/// <b>A flag owed by the person is their own to-do and says so</b> (specs/023 US5, SPEC.md §6): <i>waiting on you</i>,
/// naming who asked, and never a remedy for an agent — nothing starts a person, and "ask it" would be asking yourself.
/// </para>
/// </remarks>
public static class FlagWording
{
    /// <summary>What every person-owed flag opens with.</summary>
    public const string WaitingOnYou = "Waiting on you";

    /// <summary>Describes a flag.</summary>
    /// <param name="flag">The flag.</param>
    /// <param name="since">When it began, already written for the reader's clock.</param>
    /// <returns>One sentence naming who is waiting, on whom, and since when; the conversation for a vote.</returns>
    public static string Describe(Flag flag, string since)
    {
        ArgumentNullException.ThrowIfNull(flag);

        if (PersonSeat.Is(flag.WaitingOn))
        {
            return flag.Kind == FlagKind.NotVoted
                ? $"{WaitingOnYou}: the work in {flag.ConversationId} is all closed and {flag.Waiting.Value} is finished; " +
                  $"closing it waits on you to say whether you are too, since {since}."
                : $"{WaitingOnYou}: {flag.Waiting.Value} asked, since {since}.";
        }

        return flag.Kind switch
        {
            FlagKind.NotCollected =>
                $"{flag.WaitingOn.Value} has not collected what {flag.Waiting.Value} sent, since {since}.",
            FlagKind.NoProgress =>
                $"{flag.WaitingOn.Value} collected {flag.Waiting.Value}'s request and has done nothing on it since {since}.",
            FlagKind.NotVoted =>
                $"The work in {flag.ConversationId} is all closed and {flag.Waiting.Value} is finished; closing it has " +
                $"waited on {flag.WaitingOn.Value} to say whether it is too since {since}.",
            FlagKind.PermissionRefused => Views.Activities.RefusedFlag(flag.WaitingOn, flag.Waiting, since),
            FlagKind.Retired =>
                $"{flag.WaitingOn.Value} is retired and still holds {flag.Waiting.Value}'s request, since {since}.",

            // Said plainly rather than guessed at: a kind this build does not know is a newer broker's.
            _ => $"{flag.WaitingOn.Value} holds {flag.Waiting.Value}'s request, flagged since {since}.",
        };
    }
}
