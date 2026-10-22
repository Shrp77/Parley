using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Web.Notifications;

namespace Parley.Web.Pages;

/// <summary>Everything the console has told the person since it started, newest first.</summary>
/// <remarks>
/// Opening it is what marks the bell as read, so the page is read once and the count then goes to zero. Which were new
/// is taken before that, so the page can still show the person which ones they had not seen.
/// </remarks>
/// <param name="log">What the console has said.</param>
public sealed class NotificationsModel(NotificationLog log) : PageModel
{
    /// <summary>Each notification, and whether it was new when the page opened.</summary>
    public IReadOnlyList<(Notification Note, bool New)> Items { get; private set; } = [];

    /// <summary>Lists them, then counts them as seen.</summary>
    public void OnGet()
    {
        Items = [.. log.All().Select(one => (one, log.IsUnread(one)))];

        log.MarkAllRead();
    }

    /// <summary>Forgets them all.</summary>
    /// <returns>The page, empty.</returns>
    public IActionResult OnPostClear()
    {
        log.Clear();

        return RedirectToPage();
    }
}
