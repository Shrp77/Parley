namespace Parley.Core;

/// <summary>
/// The name of the folder a conversation's files live in (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The identifier when it can be a folder name as it is, otherwise the identifier and <c>~2</c>, <c>~3</c> and
/// so on.</b> An identifier cannot be used as it is when Windows reserves it as a device name, when it begins
/// with a dot (hidden on Linux and macOS) or ends in one (Windows strips it), or when a folder differing only in
/// case is already taken — Windows and macOS hold <c>Pricing</c> and <c>pricing</c> as one folder.
/// </para>
/// <para>
/// <b>Why a suffix and not a prefix.</b> A <c>_</c> prefix would collide with a conversation actually called
/// <c>_con</c>, since an identifier may contain <c>_</c>. <c>~</c> cannot occur in an identifier, so a suffixed
/// folder is never another conversation's own name.
/// </para>
/// <para>
/// No I/O: what is taken is the caller's to say, from the store's recorded folders and the directories on disk.
/// </para>
/// </remarks>
public static class ConversationFolder
{
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Whether an identifier can be a folder name as it is, before anything else is considered.</summary>
    /// <param name="conversationId">The identifier.</param>
    /// <returns><see langword="true"/> unless it is reserved or begins or ends with a dot.</returns>
    public static bool IsUsableAsItIs(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        if (conversationId.StartsWith('.') || conversationId.EndsWith('.'))
        {
            return false;
        }

        var dot = conversationId.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? conversationId : conversationId[..dot];

        return !Reserved.Contains(stem.ToUpperInvariant(), StringComparer.Ordinal);
    }

    /// <summary>Chooses a conversation's folder name.</summary>
    /// <param name="conversationId">The identifier.</param>
    /// <param name="taken">Whether a folder name is already taken, compared without regard to case.</param>
    /// <returns>The identifier, or the identifier with the lowest free suffix of 2 or more.</returns>
    public static string Choose(string conversationId, Func<string, bool> taken)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(taken);

        if (IsUsableAsItIs(conversationId) && !taken(conversationId))
        {
            return conversationId;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = FormattableString.Invariant($"{conversationId}~{suffix}");

            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }
}
