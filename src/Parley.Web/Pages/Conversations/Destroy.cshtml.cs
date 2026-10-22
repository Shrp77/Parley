using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;

namespace Parley.Web.Pages.Conversations;

/// <summary>
/// Destroying one conversation, behind the door a purge stands behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>A page of its own rather than a button on the list, and that is the whole design.</b>
/// A destructive act needs a description and a deliberate act, and a typed name is a deliberate act;
/// a purge behind a keystroke is a different risk from one behind a page that describes it first.
/// </para>
/// <para>
/// <b>Whoever is waiting is named before the identifier is asked for.</b> An open exchange in a
/// destroyed conversation is not closed -- it ceases to exist, so the agent
/// waiting on it is never told anything at all. That is a different outcome from every other way
/// an exchange can end, and the one an operator is least likely to predict.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class DestroyModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project the conversation belongs to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>Which conversation.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The identifier typed back.</summary>
    [BindProperty]
    public string? Typed { get; set; }

    /// <summary>The conversation as the list describes it, for the description.</summary>
    public ConversationRow? Conversation { get; private set; }

    /// <summary>What went wrong, when something did.</summary>
    public string? Problem { get; private set; }

    /// <summary>What happened, once something has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>The lines describing what will be lost.</summary>
    public IReadOnlyList<string> Describes =>
        ConversationsView.Describe(Conversation);

    /// <summary>Reads the conversation so the page can describe it.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        await LoadAsync(project).ConfigureAwait(false);

        return Page();
    }

    /// <summary>Destroys it, but only when the identifier typed back is exactly right.</summary>
    /// <returns>The page, saying what happened.</returns>
    /// <remarks>
    /// Ordinal and whole. A comparison that trimmed or ignored case would accept a near-miss, and
    /// the point of asking is that somebody has read what is in front of them.
    /// </remarks>
    public async Task<IActionResult> OnPostAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Index");
        }

        await LoadAsync(project).ConfigureAwait(false);

        if (!string.Equals(Typed, Id, StringComparison.Ordinal))
        {
            Outcome = "That is not this conversation's identifier. Nothing changed.";

            return Page();
        }

        var token = HttpContext.RequestAborted;
        var broker = new BrokerClient(directory.ClientFor(project));

        var destroyed = await BrokerDirectory.ReadAsync(async () =>
            await broker.DestroyAsync(Id, token).ConfigureAwait(false)).ConfigureAwait(false);

        if (!destroyed.Reachable)
        {
            Outcome = destroyed.Problem;

            return Page();
        }

        var result = destroyed.Value!;

        // Per table rather than as a total, because a total is the one number that carries no
        // warning -- and this is the only account of those rows that will ever exist.
        Outcome = result.Verdict == DestroyVerdict.NoSuchConversation
            ? $"There is no conversation called {Id} any more."
            : result.Destroyed.Count == 0
                ? $"Destroyed {Id}, and nothing else went with it."
                : $"Destroyed {Id}, and with it "
                  + string.Join(", ", result.Destroyed.Select(t => $"{t.Rows} {t.Table}")) + ".";

        Conversation = null;

        return Page();
    }

    private async Task LoadAsync(ProjectSettings project)
    {
        var token = HttpContext.RequestAborted;
        var broker = new BrokerClient(directory.ClientFor(project));

        // Read with archived included, because an archived conversation is exactly the kind
        // somebody is most likely to be destroying and a page that could not find it would say the
        // conversation was gone when it was only filed away.
        var listed = await BrokerDirectory.ReadAsync(async () =>
            await broker.ConversationsAsync(includeArchived: true, cancellationToken: token)
                .ConfigureAwait(false)).ConfigureAwait(false);

        Problem = listed.Problem;

        Conversation = listed.Value is { } page
            ? ConversationsView.Rows(page.Items).FirstOrDefault(row => row.Id == Id)
            : null;
    }
}
