namespace Parley.Core;

/// <summary>What a conversation's identifier may be, in the one place the broker and the envelope can ask.</summary>
/// <remarks>
/// <para>
/// <b>Held in the broker as well as the schema, because the schema's check can be walked around.</b> SQLite reads a text as a
/// C string in <c>length()</c> and <c>GLOB</c>, so an identifier of <c>a</c>, a NUL and anything after it passed the check whole:
/// any length, a newline, an escape, a backtick. The identifier is written into the header line of every envelope its
/// messages arrive in, outside what the token defends, so one that reached the store forged a second header line in another
/// agent's context (review, quality lens run 17, 0.63.4: found by a differential of the C# and SQL rules; the schema's rule is
/// older than this PR, and the history envelope this PR adds is a second reader of it).
/// </para>
/// <para>
/// The surfaces keep their own copies of the grammar to correct and explain a name as it is typed; this is the one that
/// refuses, in front of the store.
/// </para>
/// </remarks>
public static class ConversationId
{
    /// <summary>The most characters an identifier may have.</summary>
    public const int Longest = 64;

    /// <summary>Whether an identifier is one a conversation may have.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>True for 1 to 64 letters, digits, dots, underscores and hyphens, with no two dots together.</returns>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > Longest || id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
