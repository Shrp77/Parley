using Parley.Core;

namespace Parley.Client;

/// <summary>
/// How a surface says where an ownership line came from, and where a question from Parley stands (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of words, read by both surfaces</b>, for the reason <see cref="OwnershipAsk"/> keeps one ask: the review's
/// whole worth is that a person can tell a line nobody read from one a person decided, and two surfaces describing the
/// same record differently would make one of them wrong about exactly that.
/// </para>
/// <para>
/// <b>A line accepted automatically is marked as nobody having read it</b>, in words rather than a colour or a
/// glyph, so the distinction survives a terminal with no colour and a screen reader alike — the rule every other state
/// on these surfaces is drawn by.
/// </para>
/// </remarks>
public static class Provenance
{
    /// <summary>The words for an automatic line, which a test holds the surfaces to.</summary>
    public const string Automatic = "accepted automatically — the agent's own words, which no person has read";

    /// <summary>How an accepted line was arrived at, in words a person reads.</summary>
    /// <param name="source">The route, or <see langword="null"/> when nobody has accepted a line.</param>
    /// <returns>The words.</returns>
    public static string Of(OwnershipSource? source) => source switch
    {
        OwnershipSource.Automatic => Automatic,
        OwnershipSource.Proposed => "the agent's own words, accepted by a person",
        OwnershipSource.Edited => "the agent's words, edited by a person",
        OwnershipSource.Composed => "written by a person",
        _ => "no line has been accepted"
    };

    /// <summary>What is shown when no question from Parley is on record for an agent.</summary>
    public const string NoQuestion =
        "no question from Parley on record; it asks each agent once, when it first attaches, so use Ask again if its " +
        "question's conversation was destroyed";

    /// <summary>Where a question from Parley stands, in words a person reads.</summary>
    /// <param name="question">The agent's latest question, or <see langword="null"/> when none is on record.</param>
    /// <returns>The words.</returns>
    /// <remarks>
    /// <b>No question on record promises no asking.</b> It is an agent never asked, which Parley asks when it attaches,
    /// or one whose question's conversation was destroyed, which Parley never asks again by itself (SPEC.md §10.2). A
    /// surface cannot tell the two apart, so the words say what is true of both (lens 3 run 104).
    /// </remarks>
    public static string Of(OwnershipQuestion? question) => question?.State switch
    {
        null => NoQuestion,
        QuestionState.Asked => "Parley asked, and it has not answered",
        QuestionState.AskedBack => "it asked Parley something back, and a person needs to reply",
        QuestionState.Answered => "it answered, and Parley is deciding",
        QuestionState.Waiting => "its answer waits for a person: " + question.WaitingBecause,
        QuestionState.Accepted => "its answer was accepted",
        QuestionState.Discarded => "its answer was discarded",
        _ => "Parley's question is in a state this surface does not know"
    };
}
