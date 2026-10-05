namespace Parley.Core;

/// <summary>Why an exchange named in a reply's <c>settles</c> could not be settled (SPEC.md §5.6).</summary>
/// <remarks>
/// One per exchange named, so an agent can send its reply again without the ones it may not settle rather than
/// guessing which of several it got wrong.
/// </remarks>
public enum SettleProblem
{
    /// <summary>The reply is not on a Request the person opened with this agent, so it can settle nothing.</summary>
    NotFromThePerson = 1,

    /// <summary>The exchange is in a different conversation from the reply.</summary>
    OtherConversation = 2,

    /// <summary>The exchange is closed, or there is no such exchange; the two are not told apart, as elsewhere.</summary>
    NotOpen = 3,

    /// <summary>The exchange is not between this agent and the person.</summary>
    NotBetweenYou = 4,

    /// <summary>The person does not owe the next move on it.</summary>
    NotTheirMove = 5
}
