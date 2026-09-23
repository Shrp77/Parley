using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Parley.Core;

/// <summary>
/// The SHA-256 of a shared file's content, as 64 lowercase hexadecimal characters (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The broker's, never a placer's.</b> A hash an adapter supplied would let it claim content it did not
/// send, so nothing on the wire carries one inward; this type is how the broker's value is spelled on the
/// way out and how an expected value is parsed on the way in.
/// </para>
/// <para>
/// It identifies content and attests nothing about who wrote it (§13). The same hash is the same bytes.
/// </para>
/// <para>
/// Hashing a buffer is computation rather than I/O, so it belongs here; hashing a stream as it is written
/// to disk is the store's and the broker's.
/// </para>
/// </remarks>
public readonly record struct ContentHash
{
    /// <summary>The number of hexadecimal characters in a hash.</summary>
    public const int Length = 64;

    private ContentHash(string value) => Value = value;

    /// <summary>The hash, in lowercase hexadecimal.</summary>
    public string Value { get; }

    /// <summary>The first twelve characters, which is what a list shows by default.</summary>
    public string Short => Value[..12];

    /// <summary>The hash of empty content, which a zero-byte file has.</summary>
    public static ContentHash Empty { get; } = Of([]);

    /// <summary>Hashes content held in memory.</summary>
    /// <param name="content">The bytes.</param>
    /// <returns>Their hash.</returns>
    public static ContentHash Of(ReadOnlySpan<byte> content) => FromDigest(SHA256.HashData(content));

    /// <summary>Spells a SHA-256 digest computed elsewhere, such as incrementally over a stream.</summary>
    /// <param name="digest">The 32-byte digest.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentException">The digest is not 32 bytes.</exception>
    public static ContentHash FromDigest(ReadOnlySpan<byte> digest) =>
        digest.Length == SHA256.HashSizeInBytes
            ? new ContentHash(Convert.ToHexStringLower(digest))
            : throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(digest));

    /// <summary>Tries to read a hash written as text.</summary>
    /// <param name="text">The text. Upper case is accepted and folded, since a person may copy either.</param>
    /// <param name="hash">The hash, when the text is one.</param>
    /// <returns><see langword="true"/> when it is 64 hexadecimal characters.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, out ContentHash hash)
    {
        hash = default;

        if (text is null || text.Length != Length)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        hash = new ContentHash(text.ToLowerInvariant());
        return true;
    }

    /// <summary>Reads a hash written as text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentException">The text is not 64 hexadecimal characters.</exception>
    public static ContentHash Parse(string text) =>
        TryParse(text, out var hash)
            ? hash
            : throw new ArgumentException("A content hash is 64 hexadecimal characters.", nameof(text));

    /// <inheritdoc/>
    public override string ToString() => Value;
}
