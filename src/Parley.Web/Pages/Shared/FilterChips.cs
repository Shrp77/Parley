using Parley.Web.Seating;

namespace Parley.Web.Pages.Shared;

/// <summary>The addresses behind a list's filter chips (Pages/Shared/_FilterChips.cshtml).</summary>
/// <remarks>
/// Feature 042: what a list is filtered on is shown as chips, each a link to the same list without that one filter. One
/// place builds those links, so every list keeps the rest of its address the same way: every other part of the query is
/// kept, and the page's own reload mark is dropped, so the link reads as somebody arriving rather than as a reload. A name
/// is matched without regard to case, as model binding reads it.
/// </remarks>
public static class FilterChips
{
    /// <summary>The page the request asked for, with one filter taken off.</summary>
    /// <param name="request">The request the page is drawn for.</param>
    /// <param name="key">The query name of the filter to take off.</param>
    /// <returns>A path and query on this console.</returns>
    public static string Without(HttpRequest request, string key)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kept = request.Query
            .Where(pair => !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(pair.Key, PersonSeats.Reloaded, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));

        return (request.PathBase + request.Path).ToString() + QueryString.Create(kept);
    }
}
