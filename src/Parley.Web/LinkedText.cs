using System.Text.RegularExpressions;

namespace Parley.Web;

/// <summary>One stretch of a message: plain words, or a web address to follow.</summary>
/// <param name="Text">The words as written. Always encoded by the page, never written as markup.</param>
/// <param name="Link">The address when this stretch is one a person may follow; otherwise null.</param>
public sealed record TextPart(string Text, Uri? Link);

/// <summary>Finds the web addresses in a message so the page can make them followable.</summary>
/// <remarks>
/// <para>
/// <b>Only http and https.</b> A payload is inert data written by a peer, so what it names is not trusted to be a
/// place worth going: a <c>javascript:</c> or <c>data:</c> address is left as the plain words it was written as.
/// </para>
/// <para>
/// <b>Parts, not markup.</b> The page draws each part through Razor, which encodes it, so nothing a peer wrote can
/// become an element. The anchor is built by the page, with the address as an attribute value.
/// </para>
/// </remarks>
public static partial class LinkedText
{
    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    /// <summary>Splits words into plain stretches and followable addresses, in order, losing nothing.</summary>
    /// <param name="text">The message's words.</param>
    /// <returns>Parts which, joined, give <paramref name="text"/> exactly.</returns>
    public static IReadOnlyList<TextPart> Split(string? text)
    {
        var parts = new List<TextPart>();

        if (string.IsNullOrEmpty(text))
        {
            return parts;
        }

        var at = 0;

        foreach (Match match in Address().Matches(text))
        {
            // Punctuation that ends a sentence or closes a bracket is not part of the address.
            var found = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');

            if (!Uri.TryCreate(found, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || found.Length <= uri.Scheme.Length + 3)
            {
                continue;
            }

            if (match.Index > at)
            {
                parts.Add(new TextPart(text[at..match.Index], null));
            }

            parts.Add(new TextPart(found, uri));
            at = match.Index + found.Length;
        }

        if (at < text.Length)
        {
            parts.Add(new TextPart(text[at..], null));
        }

        return parts;
    }
}
