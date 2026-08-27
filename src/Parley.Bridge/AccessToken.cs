using System.Security.Cryptography;

namespace Parley.Bridge;

/// <summary>
/// The secret a caller must present, and the checks that stand in front of every endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loopback is not a security boundary for a web application.</b> That is the whole reason
/// this type exists. Any page in any tab of the operator's browser can issue a request to
/// <c>http://127.0.0.1:7777</c>, and if the bridge answered on the strength of the address alone,
/// a visited advertisement could retire an agent or type into a running Claude Code process.
/// The classic form of that attack does not need to read the reply to do damage.
/// </para>
/// <para>
/// Two independent controls, because each covers what the other cannot:
/// </para>
/// <para>
/// The token travels in a header, never a cookie. A browser attaches cookies to cross-site
/// requests on its own, which is what makes cookie-authenticated localhost services forgeable;
/// a custom header cannot be set cross-origin without a successful preflight, and no CORS
/// headers are ever emitted, so the preflight fails.
/// </para>
/// <para>
/// The <c>Origin</c> check refuses anything a page sent, on any origin. Requests from the
/// bridge's own page carry its origin; requests from a script on another site carry theirs;
/// requests from <c>curl</c> carry none. So a foreign origin is rejected outright even if a
/// token were somehow known.
/// </para>
/// </remarks>
public static class AccessToken
{
    /// <summary>The header a caller presents the token in.</summary>
    public const string Header = "X-Parley-Token";

    /// <summary>
    /// Mints a token for one run of the bridge.
    /// </summary>
    /// <returns>A URL-safe secret.</returns>
    /// <remarks>
    /// 256 bits from the cryptographic generator. Not a GUID: several GUID versions are
    /// substantially structure and timestamp, and this is a bearer credential for a process that
    /// can start agents.
    /// </remarks>
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>
    /// Decides whether one request may proceed.
    /// </summary>
    /// <param name="presented">The token the caller presented, if any.</param>
    /// <param name="expected">The token this run of the bridge minted.</param>
    /// <param name="origin">The request's <c>Origin</c> header, if any.</param>
    /// <param name="allowedOrigin">This bridge's own origin.</param>
    /// <returns><see langword="true"/> if the request may be served.</returns>
    /// <remarks>
    /// <para>
    /// The comparison is fixed-time. An ordinary string comparison returns as soon as two bytes
    /// differ, which lets a caller who can time many attempts recover the secret one byte at a
    /// time. That attack is unglamorous and entirely practical against a local service, where the
    /// noise floor is microseconds rather than milliseconds.
    /// </para>
    /// <para>
    /// A missing <c>Origin</c> is allowed and a present, foreign one is not. That ordering is
    /// deliberate: browsers always send the header on the requests this needs to stop, and tools
    /// that are not browsers -- <c>curl</c>, a script, the tests below -- never send it and were
    /// never the threat, since anything that can set a header can set any header.
    /// </para>
    /// </remarks>
    public static bool Admits(
        string? presented, string expected, string? origin, string allowedOrigin)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(allowedOrigin);

        if (!string.IsNullOrEmpty(origin)
            && !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
