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
/// <param name="Attach">
/// Whether files may be pasted or dropped into it, to go with the words. Its form must be sent as
/// <c>multipart/form-data</c>, and its handler places what arrives as <c>Attachments</c> through <see cref="ComposerFiles"/>.
/// </param>
/// <param name="Attached">Files an earlier press placed before its send failed, kept so they are not uploaded again.</param>
/// <param name="Draft">
/// Which message this box is for, as the key its draft is kept under in this browser: the project and the exchange or
/// conversation it answers. None for a box whose words are not a message.
/// </param>
/// <param name="Mentions">
/// The agents typing <c>@</c> offers, by full name: the project's, as its page knows them. Choosing one completes the name
/// in the words; where the form also carries a <c>Targets</c> box for that agent, it is ticked too. None for a box whose
/// words are not a message.
/// </param>
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
/// <para>
/// <b>Attaching is the composer's too</b> (owner, 2026-11-13: a screenshot pasted on the conversation page was attached,
/// and the same paste in the Inbox did nothing, because the conversation page carried its own copy). The partial draws
/// the attachment area and one script in the frame runs it for every box that has one.
/// </para>
/// <para>
/// <b>A message box keeps what was typed until it is sent</b> (owner, 2026-11-13: a reply half written in the Inbox was
/// gone after answering another message first). The frame's script saves the words under <see cref="Draft"/> as they are
/// typed, puts them back when the box is drawn empty again, and drops them when the form is sent. A box the server drew
/// with words in it (a refused send kept them) shows those words, since they are what was last sent.
/// </para>
/// <para>
/// <b>Typing <c>@</c> offers the project's agents in every message box</b> (owner, 2026-11-13: it worked on the conversation
/// page and not in the Inbox, which had no copy of it). The name stays in the words and never addresses anybody: who a
/// message goes to is the form's, never parsed out of what it says (SPEC.md §13).
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
    int? MaxLength = null,
    bool Attach = false,
    IReadOnlyList<string>? Attached = null,
    string? Draft = null,
    IReadOnlyList<string>? Mentions = null)
{
    /// <summary>What every composer says about its keys.</summary>
    public const string Keys = "Enter sends. Shift+Enter or Ctrl+Enter makes a new line.";

    /// <summary>How long pasted text may be before it is attached as a file rather than put in the box.</summary>
    /// <remarks>
    /// A log or a stack trace pasted into the box is a long message the agent has to read whole; as a file it is named in a
    /// line and fetched only if wanted. Well under the soft limit, so a paste never makes a message long by itself.
    /// </remarks>
    public const int PasteBecomesAFileOver = 4000;
}
