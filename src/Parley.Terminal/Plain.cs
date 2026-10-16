using System.Text;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// A label that draws what it is given, including an underscore.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Label"/> eats <c>_</c>.</b> The toolkit's labels take an
/// underscore as the marker for a hotkey — <c>T_itle</c> means Alt+I — so the character is removed
/// on the way to the screen and the letter after it is underlined instead. The rule this surface
/// states for a conversation name contains <c>'_'</c>, so a label that dropped it would show a rule
/// with one of its own characters missing.
/// </para>
/// <para>
/// <b>It is not a typographic nuisance; it is the same class of defect as the ones this surface
/// already guards against.</b> Almost every label here draws text this program did not write — a
/// path in the folder picker, a project's description before it is destroyed, a conversation
/// identifier on the status line, a broker's own words on the message line. An underscore is legal
/// in all of those, so a label that silently drops one shows a person a value that differs from
/// the value being acted on. <c>Safe</c> exists because the screen must show what is really there;
/// this is the other half of it.
/// </para>
/// <para>
/// A type rather than a line repeated at each construction: there are labels in four files, and
/// the next one somebody adds gets this by writing <c>Plain</c> rather than by remembering.
/// <c>UnderscoresSurviveTests</c> is what keeps it that way.
/// </para>
/// </remarks>
internal sealed class Plain : Label
{
    /// <summary>What disables hotkey parsing, per the toolkit's own documentation.</summary>
    /// <remarks>
    /// <c>TextFormatter.HotKeySpecifier</c>: "Set to '\xffff' to disable hot key support for this
    /// View instance." The default on <see cref="Label"/> is <c>_</c>, which is what this undoes.
    /// </remarks>
    internal static readonly Rune NoHotKey = (Rune)0xFFFF;

    internal Plain() => HotKeySpecifier = NoHotKey;
}
