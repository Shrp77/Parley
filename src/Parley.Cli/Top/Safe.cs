using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Turning text this program did not write into text a terminal will not act on.
/// </summary>
/// <remarks>
/// <para>
/// Almost everything this surface draws was written by somebody else: agent names, conversation
/// identifiers, message payloads, project names from a manifest, refusals in a broker's own words,
/// and lines out of a log. Spectre reads square brackets as style tags, so any of those reaching a
/// renderer unescaped lets its author choose how the operator's screen is drawn.
/// </para>
/// <para>
/// <b>It applies to choices as much as to panels, which is the part that was missed.</b>
/// <c>SelectionPrompt</c> renders its entries as markup — that is how anyone puts a colour in one
/// — so a conversation named with a tag reaches the chooser as styling rather than as characters.
/// Panels were escaped from the first draft and choosers were not, and the difference was not a
/// judgement: it was that the panel was the place the hazard had been noticed.
/// </para>
/// <para>
/// A type of its own rather than a helper on whichever view needed it first. It was
/// <c>ProjectList.Escape</c>, called from eleven files that have nothing to do with listing
/// projects, which is how a security property comes to look like a formatting convenience.
/// </para>
/// </remarks>
internal static class Safe
{
    /// <summary>Makes text inert, so a terminal shows it rather than obeying it.</summary>
    /// <param name="text">Whatever came from a manifest, a broker, an agent or a log.</param>
    /// <returns>The same text, with nothing in it a renderer will interpret.</returns>
    internal static string Text(string? text) => Markup.Escape(text ?? string.Empty);
}
