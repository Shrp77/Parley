namespace Parley.Terminal;

/// <summary>What one of this surface's actions is about.</summary>
/// <remarks>
/// <para>
/// <b>Categories rather than File, Edit and Window</b>, which is the owner's decision of 2026-09-25.
/// This is not a document editor: there is no file to open, nothing to cut and no windows to arrange,
/// so those titles would each be empty or padded with things that do not belong under them.
/// </para>
/// <para>
/// <b>An action states what it is about and no more.</b> It does not know what a menu is — the owner's
/// words: <i>"The action can be a self-sustained entity with metadata that guides the UI, but does NOT
/// dictate the UI."</i> The status line, the <c>?</c> dialog and the menu bar are three readings of one
/// list, and each ignores what it does not need.
/// </para>
/// <para>
/// <b>Declared in the order the bar shows them</b>, because <c>MenuRow</c> reads that order rather than
/// keeping a second list. Project is first on the owner's call of 2026-09-27: a person opens this surface
/// to act on a project, so that is the menu at the left edge.
/// </para>
/// </remarks>
internal enum ActionCategory
{
    /// <summary>One project: naming, removing, pruning, enrolling into.</summary>
    Project,

    /// <summary>The deployment as a whole: refreshing it, watching it, leaving.</summary>
    Deployment,

    /// <summary>One agent, or the seat a person holds among them.</summary>
    Agent,

    /// <summary>One conversation, and the files that belong to it.</summary>
    Conversation,

    /// <summary>What is on the screen rather than what is in the deployment.</summary>
    View,

    /// <summary>What the keys are, and which build this is.</summary>
    Help,
}

/// <summary>A run of actions that belong together, and where the run sits.</summary>
/// <param name="Name">What the run is called, or empty when it needs no heading.</param>
/// <param name="Order">Where it sits among the other runs in its category.</param>
/// <remarks>
/// <b>A record rather than a string, and that is the whole reason it exists as a type.</b> Group
/// ordering is a property of the group: a bare string would have every action in a run repeating that
/// run's position, and two actions in one run would disagree about it the first time one was edited.
/// This repository's most-repeated defect is two lists that agree until one is changed.
/// </remarks>
internal sealed record ActionGroup(string Name, int Order)
{
    /// <summary>The run an action falls in when it names none.</summary>
    /// <remarks>
    /// Ordered last, so an action nobody has grouped sinks below the ones somebody has rather than
    /// displacing them. It is deliberately not an error: a new action must reach the screen from the
    /// moment it exists, and <c>EveryKeyIsOfferedTests</c> is what holds that.
    /// </remarks>
    public static ActionGroup Ungrouped { get; } = new(string.Empty, int.MaxValue);
}
