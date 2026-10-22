using System.Globalization;
using Parley.Client.Views;

namespace Parley.Client.Rendering;

/// <summary>
/// The web console's address for a thing, built in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place because a hand-copied address can be wrong without anything noticing.</b> An action
/// that opened a port nothing listens on would still report <c>Opened …</c>: from the launcher's
/// side, the browser started.
/// </para>
/// <para>
/// A hand-copied <i>route</i> is the same defect one layer up, and worse, because a 404 looks like
/// the console being broken rather than the link being wrong. So the paths live here, the host
/// comes from the console's own constant, and a test reflects these against the pages the console
/// actually serves.
/// </para>
/// <para>
/// <b>This assembly cannot reference the console</b> — that would put ASP.NET Core on the
/// terminal's graph, which is most of why <c>Parley.Client</c> was split out in the first place. So
/// the paths are declared here and checked against the console from the test project, which may
/// see both.
/// </para>
/// </remarks>
public static class Deep
{
    /// <summary>The console's front page.</summary>
    public const string Home = "/";

    /// <summary>The page showing one project's agents.</summary>
    public const string Agents = "/Projects/Agents";

    /// <summary>The page listing conversations.</summary>
    public const string Conversations = "/Conversations";

    /// <summary>The page showing one conversation.</summary>
    public const string Conversation = "/Conversations/Detail";

    /// <summary>The page holding a project's ownership review and its Auto Accept switch.</summary>
    /// <remarks>
    /// Where the terminal's configuration view leads, because it is the web UI's one page carrying a project
    /// setting — Auto Accept, stated with what it means, beside the lines it decides about.
    /// </remarks>
    public const string Ownership = "/Projects/Ownership";

    /// <summary>Builds the address for wherever a person currently is.</summary>
    /// <param name="host">Where the console listens, without a trailing slash.</param>
    /// <param name="place">What the person is looking at.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="place"/> is null.</exception>
    /// <remarks>
    /// <b>The thing being looked at, never the front page.</b> A key that always opened the home
    /// page would be a key that makes somebody navigate the console from scratch to reach what was
    /// already on their screen, which is worse than no key at all.
    /// </remarks>
    public static string For(string host, ViewPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        var root = (host ?? string.Empty).TrimEnd('/');

        return place.Viewing switch
        {
            Viewing.Conversation when place.Project is { } project && place.Conversation is { } id =>
                $"{root}{Conversation}?project={Escape(project.Value)}&id={Escape(id)}",
            Viewing.Project when place.Project is { } project =>
                $"{root}{Agents}?project={Escape(project.Value)}",
            Viewing.Configuration when place.Project is { } project =>
                $"{root}{Ownership}?project={Escape(project.Value)}",
            _ => $"{root}{Home}",
        };
    }

    /// <summary>Percent-encodes a value for a query string.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    /// <remarks>
    /// A project name and a conversation identifier are both constrained — no <c>/</c>, no
    /// <c>:</c>, no runs of dots — so there is little here to encode. It is done anyway, because
    /// the constraint is in a schema this assembly does not read, and a link that silently truncates
    /// at an ampersand somebody was allowed to use would be a link nobody could debug.
    /// </remarks>
    private static string Escape(string value) =>
        Uri.EscapeDataString(value ?? string.Empty)
            .Replace("%20", "+", false, CultureInfo.InvariantCulture);
}
