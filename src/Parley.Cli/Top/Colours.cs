using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.Drawing;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Parley.Cli.Top;

/// <summary>
/// Every colour this surface draws with, and the rules about what a colour may mean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoration, and only decoration — the same rule <see cref="Palette"/> keeps.</b> Every colour
/// here is derived from a word that is already on the line: a project is green because the line
/// says <c>answering</c>, not instead of saying it. That is what keeps SC-006 true, and SC-006 is
/// not a preference — the whole test suite reads the screen as plain text, so a distinction carried
/// only in colour is a distinction no test in this repository can see.
/// </para>
/// <para>
/// <b>The background is the terminal's own.</b> <see cref="Color.None"/> resolves to the default
/// background rather than to black, so the surface sits in whatever theme somebody has chosen
/// instead of painting a dark rectangle over a light terminal. The one exception is the status
/// line, which is a bar by convention and needs a ground of its own to read as one.
/// </para>
/// <para>
/// <b>An agent's colour is <see cref="Palette"/>'s, not a second scheme.</b> The slot comes from
/// the shared derivation the web console uses, so an agent is the same colour in a browser and in
/// a terminal — and the hues below are that stylesheet's own dark-mode values, because a terminal
/// is more often dark than not. Two surfaces colouring agents independently is the drift
/// <c>Parley.Client</c> exists to prevent, arrived at through a stylesheet instead of through a
/// transcript.
/// </para>
/// <para>
/// True colour is written rather than one of the sixteen names: Terminal.Gui maps to the nearest
/// of the sixteen on a terminal that cannot do better, which is a better answer than choosing the
/// coarse one everywhere.
/// </para>
/// </remarks>
internal static class Colours
{
    /// <summary>The terminal's own background, whatever the person has set it to.</summary>
    internal static Color Ground => Color.None;

    /// <summary>Ordinary text, in the terminal's own foreground.</summary>
    internal static Color Text => Color.None;

    /// <summary>Something that is working: a broker answering, an agent present.</summary>
    internal static Color Good { get; } = new(0x46, 0xB5, 0x96);

    /// <summary>Something unfinished or unknown: a broker not yet asked, an exchange still open.</summary>
    internal static Color Warn { get; } = new(0xD9, 0x8B, 0x52);

    /// <summary>Something that is not working: a broker not answering, an agent retired.</summary>
    internal static Color Bad { get; } = new(0xE0, 0x69, 0x8D);

    /// <summary>Something present but set aside: an archived conversation, a body line.</summary>
    internal static Color Quiet { get; } = new(0x80, 0x8A, 0x99);

    /// <summary>A person holding a seat, which is never an agent.</summary>
    internal static Color Seated { get; } = new(0xA9, 0x7C, 0xE0);

    /// <summary>The projects view.</summary>
    internal static Color Projects { get; } = new(0x6D, 0x95, 0xF2);

    /// <summary>The agents half of a project.</summary>
    internal static Color Agents { get; } = new(0x46, 0xB5, 0x96);

    /// <summary>The conversations half of a project.</summary>
    internal static Color Conversations { get; } = new(0xA9, 0x7C, 0xE0);

    /// <summary>A conversation being read.</summary>
    internal static Color Transcript { get; } = new(0x4F, 0xAD, 0xC2);

    /// <summary>The ground the status bar sits on.</summary>
    internal static Color Bar { get; } = new(0x1F, 0x27, 0x33);

    /// <summary>What the status bar writes on it.</summary>
    internal static Color Said { get; } = new(0xC9, 0xD4, 0xE3);

    /// <summary>
    /// The colours of the eight slots <see cref="Palette"/> divides agents between.
    /// </summary>
    /// <remarks>
    /// The console's dark-mode stylesheet, value for value. Kept in the same order rather than
    /// re-picked, because the whole point of a stable slot is that the two surfaces agree.
    /// </remarks>
    private static readonly Color[] Slots =
    [
        new(0x6D, 0x95, 0xF2),
        new(0x46, 0xB5, 0x96),
        new(0xD9, 0x8B, 0x52),
        new(0xA9, 0x7C, 0xE0),
        new(0x4F, 0xAD, 0xC2),
        new(0xE0, 0x69, 0x8D),
        new(0x9A, 0xAB, 0x4F),
        new(0x7F, 0x9A, 0xC4),
    ];

    /// <summary>The colour of one of <see cref="Palette"/>'s slots.</summary>
    /// <param name="slot">The slot, from zero to <see cref="Palette.Slots"/> minus one.</param>
    /// <returns>Its colour.</returns>
    /// <remarks>
    /// For a caller that already has a slot rather than a name — a transcript line carries one,
    /// because the line was written by the shared renderer and the sender's name has already been
    /// folded by then.
    /// </remarks>
    internal static Color Slot(int slot) =>
        Slots[((slot % Slots.Length) + Slots.Length) % Slots.Length];

    /// <summary>The colour an agent is drawn in, everywhere it appears.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its slot's colour.</returns>
    internal static Color Agent(AgentName agent) => Slots[Palette.SlotFor(agent)];

    /// <summary>
    /// The same, for a name that has only ever been read as text.
    /// </summary>
    /// <param name="name">What the row calls it.</param>
    /// <returns>Its slot's colour, or ordinary text when the name is not a well-formed one.</returns>
    /// <remarks>
    /// A catalogue may name a role that is not a valid agent name — the agents region gives it a
    /// row saying so rather than dropping it — and there is no slot for something that is not an
    /// agent. Ordinary text is the honest answer; inventing a fold of its own here would be a
    /// second derivation, which is the thing <see cref="Palette"/> exists to avoid.
    /// </remarks>
    internal static Color Agent(string name) =>
        AgentName.TryCreate(name, out var agent) ? Agent(agent) : Text;

    /// <summary>The scheme a region's frame is drawn in.</summary>
    /// <param name="accent">The colour that names the region.</param>
    /// <returns>The scheme.</returns>
    /// <remarks>
    /// <para>
    /// <b>The accent belongs to the frame, not to the text inside it.</b> A region's rows are the
    /// facts, and tinting all of them would spend the one signal a row has left on saying which
    /// pane it is in — which the frame already says, in words, at the top. So this paints the
    /// border and the title and <see cref="Rows"/> gives the list inside its own neutral ground.
    /// </para>
    /// <para>
    /// <b>Quiet unfocused, the accent when focused — and the first draft had it accented always.</b>
    /// A dump of the drawn screen showed two panes side by side with identical frames while the
    /// status line said which one had focus: the surface knew and the frame did not. It also had
    /// every frame at full strength, which put a saturated rectangle around the rows the colour was
    /// meant to draw an eye to. A frame that recedes and brightens when it is the one being acted
    /// on does both jobs.
    /// </para>
    /// <para>
    /// Only a few roles are set; the toolkit derives the rest. Setting all twenty by hand would be
    /// twenty numbers per region to keep consistent, for no gain over what it derives.
    /// </para>
    /// </remarks>
    internal static Scheme Region(Color accent) => new()
    {
        Normal = new Attribute(Quiet, Ground),
        Focus = new Attribute(accent, Ground),
        HotNormal = new Attribute(Quiet, Ground),
        HotFocus = new Attribute(accent, Ground),
        Disabled = new Attribute(Quiet, Ground),
    };

    /// <summary>The scheme the list inside a region is drawn in.</summary>
    /// <param name="accent">The colour that names the region.</param>
    /// <returns>The scheme.</returns>
    /// <remarks>
    /// Ordinary text on the terminal's own ground, with the highlighted row carrying the region's
    /// accent. That is the one place the accent is worth spending on a row: which row a key would
    /// act on is the question a destructive action turns on, and it is the same question in every
    /// region — so it is the same colour as the frame the person is looking at rather than a
    /// separate convention to learn.
    /// </remarks>
    internal static Scheme Rows(Color accent) => new()
    {
        Normal = new Attribute(Text, Ground),
        Focus = new Attribute(Color.Black, accent),
        HotNormal = new Attribute(accent, Ground),
        HotFocus = new Attribute(Color.Black, accent),
        Disabled = new Attribute(Quiet, Ground),
    };

    /// <summary>The scheme the status line is drawn in.</summary>
    /// <returns>The scheme.</returns>
    internal static Scheme StatusBar() => new()
    {
        Normal = new Attribute(Said, Bar),
        Focus = new Attribute(Said, Bar),
        HotNormal = new Attribute(Said, Bar),
        HotFocus = new Attribute(Said, Bar),
    };

    /// <summary>What a row of ordinary text is drawn in.</summary>
    /// <param name="colour">The colour the row's own words earn it.</param>
    /// <returns>The attribute.</returns>
    internal static Attribute Row(Color colour) => new(colour, Ground);
}
