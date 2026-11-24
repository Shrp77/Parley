namespace Parley.Core;

/// <summary>
/// Which placed files are images, what a browser is told they are, and whether their bytes begin like one (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The only place Parley looks inside a placed file.</b> Placement checks a name and never the bytes, and the broker
/// still does not look. A surface that shows another participant's image in a browser checks the leading bytes first,
/// so a file named <c>.png</c> that holds a page or a script is never handed to a browser at all, whatever that
/// browser's sniffing rules would have made of it.
/// </para>
/// <para>
/// Only the signature, never a decode: decoding another participant's bytes in Parley's own process is the risk this
/// avoids. A file that starts right and is broken after is the browser decoder's to refuse.
/// </para>
/// </remarks>
public static class ImageSignature
{
    /// <summary>How many leading bytes <see cref="Matches"/> needs to decide.</summary>
    public const int BytesNeeded = 12;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];

    private static readonly byte[] Gif87 = "GIF87a"u8.ToArray();

    private static readonly byte[] Gif89 = "GIF89a"u8.ToArray();

    private static readonly byte[] Riff = "RIFF"u8.ToArray();

    private static readonly byte[] WebP = "WEBP"u8.ToArray();

    /// <summary>The media type a name's extension says, or <see langword="null"/> when the name is not an image's.</summary>
    /// <param name="name">A placed file's name.</param>
    /// <returns><c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c>, <c>image/webp</c>, or <see langword="null"/>.</returns>
    public static string? ContentType(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var dot = name.LastIndexOf('.');

        return (dot < 0 ? string.Empty : name[dot..].ToUpperInvariant()) switch
        {
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".GIF" => "image/gif",
            ".WEBP" => "image/webp",
            _ => null,
        };
    }

    /// <summary>Whether a name is an image's.</summary>
    /// <param name="name">A placed file's name.</param>
    /// <returns>Whether it ends in one of the four image extensions, in any case.</returns>
    public static bool IsImageName(string name) => ContentType(name) is not null;

    /// <summary>Whether bytes begin as the format a name says.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="leading">At least the first <see cref="BytesNeeded"/> bytes, or the whole file when it is shorter.</param>
    /// <returns><see langword="false"/> for a name that is not an image's, and for bytes that are not its format.</returns>
    public static bool Matches(string name, ReadOnlySpan<byte> leading) => ContentType(name) switch
    {
        "image/png" => leading.StartsWith(Png),
        "image/jpeg" => leading.StartsWith(Jpeg),
        "image/gif" => leading.StartsWith(Gif87) || leading.StartsWith(Gif89),
        "image/webp" => leading.Length >= BytesNeeded && leading.StartsWith(Riff) && leading[8..12].SequenceEqual(WebP),
        _ => false,
    };
}
