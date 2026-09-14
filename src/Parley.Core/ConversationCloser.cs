namespace Parley.Core;

/// <summary>
/// Who closed a conversation (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// A conversation ends by consensus or by the person running the deployment, and in no other way.
/// The two are recorded apart because they are opposite facts about how the work went — its
/// participants finishing it, or a person having to end it — and §15 counts them separately.
/// </remarks>
public enum ConversationCloser
{
    /// <summary>Every participant holding an open exchange agreed it was finished.</summary>
    Consensus = 0,

    /// <summary>The person running the deployment closed it.</summary>
    Person = 1
}
