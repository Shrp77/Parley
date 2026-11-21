using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;

namespace Parley.Web.Pages.Projects;

/// <summary>Ends several conversations at once, after saying which and taking a typed word.</summary>
/// <remarks>
/// A person's close, the same operation <c>Act</c> performs for one (SPEC.md §10.2's <c>force_conclude</c>), so it is not
/// one click like archiving: it cannot be reversed and each participant holding open work is told. The first post only
/// lists what would end; nothing happens until the word is typed back. Each conversation is its own broker call, so one
/// that fails is counted and the rest go on.
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class EndManyModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The word that has to be typed to end them.</summary>
    public const string Word = "end";

    /// <summary>The most conversations one act takes.</summary>
    private const int MostAtOnce = 100;

    /// <summary>Which project the conversations are in.</summary>
    [BindProperty, FromForm]
    public string? Project { get; set; }

    /// <summary>The conversations to end.</summary>
    [BindProperty, FromForm]
    public string[] Ids { get; set; } = [];

    /// <summary>What was typed to confirm.</summary>
    [BindProperty, FromForm]
    public string? Typed { get; set; }

    /// <summary>Why they are being closed, told to each participant holding open work.</summary>
    [BindProperty, FromForm]
    public string? Reason { get; set; }

    /// <summary>What was done, once it has been.</summary>
    public string? Outcome { get; private set; }

    /// <summary>The conversations, cleaned of blanks and repeats.</summary>
    public IReadOnlyList<string> Chosen { get; private set; } = [];

    /// <summary>The wrong confirmation, when one was typed.</summary>
    public string? Wrong { get; private set; }

    /// <summary>There is nothing to describe without a post.</summary>
    /// <returns>A redirect to the conversations.</returns>
    public IActionResult OnGet() => RedirectToPage("/Projects/Agents", new { project = Project });

    /// <summary>Describes what would end, and ends it once the word has been typed.</summary>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        Chosen = [.. Ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(MostAtOnce)];

        if (Chosen.Count == 0)
        {
            return RedirectToPage("/Projects/Agents", new { project = Project });
        }

        if (Typed is null)
        {
            return Page();
        }

        if (!string.Equals(Typed.Trim(), Word, StringComparison.Ordinal))
        {
            Wrong = $"Type {Word} exactly to end them. Nothing was changed.";

            return Page();
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;
        int done = 0, failed = 0;

        foreach (var id in Chosen)
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await broker.ForceConcludeAsync(id, string.IsNullOrWhiteSpace(Reason) ? null : Reason, token)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            if (read.Value == true)
            {
                done++;
            }
            else
            {
                failed++;
            }
        }

        Outcome = $"{done} ended." + (failed > 0 ? $" {failed} could not be ended: already closed, or gone since the list was drawn." : "");

        return Page();
    }
}
