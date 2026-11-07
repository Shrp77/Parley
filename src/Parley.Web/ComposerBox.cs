namespace Parley.Web;

/// <summary>One box a person types text in, as the shared partial <c>_Composer</c> draws it.</summary>
/// <param name="Name">The form field it posts as.</param>
/// <param name="Value">What is in it when drawn: what a refused send kept, words to edit, or nothing.</param>
/// <param name="Id">Its element identifier, for a label; none when nothing labels it.</param>
/// <param name="Rows">How tall it starts.</param>
/// <param name="Placeholder">The hint shown while it is empty.</param>
/// <param name="Label">The label drawn above it; none when the page labels it itself.</param>
/// <param name="ShowKeys">Whether the partial says which keys send, or the page says it beside its own Send button.</param>
/// <param name="Required">Whether the form may not be sent with it empty.</param>
/// <param name="MaxLength">The most characters it takes, or none; only where the store holds that bound.</param>
/// <remarks>
/// <para>
/// <b>One box and one set of keys everywhere a person types more than a line</b> (owner, 2026-11-06: "Anywhere where you
/// enter in text should have the same behavior - anything else is just a nightmare for usability"). The partial marks the
/// box <c>data-composer</c>, and one handler in the frame gives every such box the same keys: Enter sends; Shift+Enter,
/// Ctrl+Enter, or a backslash then Enter make a line break, the terminal's rule. That holds where a line is an item, such as
/// a roadmap's text, too: a new line there is Shift+Enter like anywhere else.
/// </para>
/// <para>
/// A box that only shows text and takes none (<c>readonly</c>) is not a composer.
/// </para>
/// </remarks>
public sealed record ComposerBox(
    string Name,
    string? Value = null,
    string? Id = null,
    int Rows = 3,
    string? Placeholder = null,
    string? Label = null,
    bool ShowKeys = true,
    bool Required = true,
    int? MaxLength = null)
{
    /// <summary>What every composer says about its keys.</summary>
    public const string Keys = "Enter sends. Shift+Enter or Ctrl+Enter makes a new line.";
}
