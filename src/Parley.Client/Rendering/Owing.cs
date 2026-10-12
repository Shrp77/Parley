namespace Parley.Client.Rendering;

/// <summary>
/// What sending a Request costs whoever receives it.
/// </summary>
/// <remarks>
/// <para>
/// There was a second constant here, for an announcement that nobody owed anything on. Every message is
/// answered now (principle 2.7), so there is one thing a person can send and one cost to say about it.
/// </para>
/// <para>
/// <b>One statement of it, because two surfaces say it.</b> The terminal puts it in its menu beside the
/// action and the console beside the composer; two surfaces describing one protocol differently is the
/// drift SC-003 forbids.
/// </para>
/// </remarks>
public static class Owing
{
    /// <summary>What a Request costs the participant it is sent to, in the fewest words that are true.</summary>
    /// <remarks>
    /// <para>
    /// "Owe" rather than "should": the exchange stays open until they answer, and work left untouched is shown to the
    /// person running the deployment (§6).
    /// </para>
    /// <para>
    /// <b>Plain words, on the owner's report.</b> It read "opens an exchange they owe a result on",
    /// which says the same thing in the protocol's vocabulary — exchange, Result — to a person who is not reading the
    /// protocol. The cost is unchanged; only the words a person has to translate went.
    /// </para>
    /// </remarks>
    public const string Requesting = "they'll owe you an answer";
}
