namespace Parley.Terminal;

/// <summary>Where on the surface a command can be live.</summary>
/// <remarks>
/// <para>
/// <b>A place is named, never inferred from what is on screen.</b> The check that no two commands share a key reads
/// only these, so it can run before anything is drawn: a clash that showed only once a roadmap was confirmed and a
/// message was cut short is exactly the one nobody stumbles on until a person does.
/// </para>
/// <para>
/// <b>Two of them are layers rather than places</b>: <see cref="MessageLine"/> and <see cref="Everywhere"/> belong to no
/// pane and are live on top of every one, which is why a layer's key is checked against every place it is live in.
/// </para>
/// <para>
/// <b>Each dialog is a place of its own</b>, with no layer: a dialog is a session on top of the surface and takes every
/// key first, so nothing of the surface's is live in it.
/// </para>
/// <para>
/// <b>Every view the terminal opens as a session has one</b>, read-only ones included: the surface's panes, and each
/// dialog its own, declared by the table its type builds (<see cref="IKeyTable{TSelf}"/>) and listed in
/// <see cref="CommandTables"/>. <c>KeysTakenBeforeTheTableTests</c> finds the sessions by reflection over this assembly
/// and fails on one that builds no table, or one that is not listed and pressed, so a dialog added without a table
/// fails a unit test.
/// </para>
/// </remarks>
internal enum KeyContext
{
    /// <summary>The projects list.</summary>
    Projects,

    /// <summary>A project's agents.</summary>
    Agents,

    /// <summary>A project's conversations.</summary>
    Conversations,

    /// <summary>A conversation's transcript.</summary>
    Transcript,

    /// <summary>The column beside a transcript saying who a line goes to.</summary>
    Room,

    /// <summary>The box a person writes in.</summary>
    Line,

    /// <summary>A project's confirmed roadmaps.</summary>
    Plans,

    /// <summary>A project's proposed roadmaps.</summary>
    Roadmaps,

    /// <summary>A project's settings.</summary>
    Configuration,

    /// <summary>A pane no command belongs to.</summary>
    Elsewhere,

    /// <summary>A layer: what can be done about the message line.</summary>
    MessageLine,

    /// <summary>A layer: what can be done wherever the cursor is.</summary>
    Everywhere,

    /// <summary>A dialog: one confirmed roadmap and its items.</summary>
    RoadmapDialog,

    /// <summary>A dialog: one proposed roadmap, to confirm, edit or decline.</summary>
    ProposalDialog,

    /// <summary>A dialog: a list of things to tick.</summary>
    TickDialog,

    /// <summary>A dialog: what one agent is responsible for.</summary>
    ResponsibilityDialog,

    /// <summary>A dialog: one line to type.</summary>
    TextDialog,

    /// <summary>A dialog: one choice from a list.</summary>
    ChoiceDialog,

    /// <summary>A dialog: several fields to fill in.</summary>
    FormDialog,

    /// <summary>A dialog: walking the machine's folders to a repository, or to a folder of them.</summary>
    BrowsingDialog,

    /// <summary>A dialog: the broker's log, followed as it is written.</summary>
    LogDialog,

    /// <summary>A dialog: a project's health, read-only.</summary>
    HealthDialog,

    /// <summary>A dialog: the last thing the message line said, whole, read-only.</summary>
    SayingDialog,
}

/// <summary>A place on the surface, and the layers that are live in it as well.</summary>
/// <param name="Context">The place.</param>
/// <param name="Layers">The layers whose commands are live there too.</param>
/// <remarks>A record of its own so a place and its layers are declared together and read in one call.</remarks>
internal sealed record KeyPlace(KeyContext Context, IReadOnlyList<KeyContext> Layers);
