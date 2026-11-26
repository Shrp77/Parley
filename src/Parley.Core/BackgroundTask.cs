namespace Parley.Core;

/// <summary>What kind of background work a task is, as the runner says.</summary>
public enum TaskKind
{
    /// <summary>Some other kind the runner names, or none.</summary>
    Other,

    /// <summary>A sub-agent.</summary>
    Agent,

    /// <summary>A background command.</summary>
    Command,
}

/// <summary>Where a task's link goes.</summary>
public enum TaskLinkKind
{
    /// <summary>An exchange of the same project.</summary>
    Exchange,

    /// <summary>A conversation of the same project.</summary>
    Conversation,

    /// <summary>A file placed in Parley.</summary>
    File,

    /// <summary>An https address.</summary>
    Web,
}

/// <summary>A link a task carries, in one of the shapes Parley shows (<see cref="BackgroundTaskText.Link"/>).</summary>
/// <param name="Kind">Where it goes.</param>
/// <param name="Target">The exchange number, the conversation's or file's name, or the address.</param>
/// <param name="Host">For an https address, its host as a person should read it (in punycode); otherwise empty.</param>
public sealed record TaskLink(TaskLinkKind Kind, string Target, string Host = "")
{
    /// <summary>The link written in its one spelling, as it travels: <c>exchange #57</c>, <c>conversation:id</c>,
    /// <c>file:name</c> or the address.</summary>
    public string Written => Kind switch
    {
        TaskLinkKind.Exchange => "exchange #" + Target,
        TaskLinkKind.Conversation => "conversation:" + Target,
        TaskLinkKind.File => "file:" + Target,
        _ => Target,
    };
}

/// <summary>One background task an agent has running, as the person is shown it (SPEC.md §11.6).</summary>
/// <param name="Id">The runner's id for it.</param>
/// <param name="Kind">What kind of work it is.</param>
/// <param name="Name">What it is doing, in a few words: the agent's own, else the runner's label; empty when neither
/// said, and a surface then draws a plain stand-in.</param>
/// <param name="Description">One or two sentences the agent gave about it, or empty.</param>
/// <param name="Link">Where the person can read more, or null.</param>
/// <remarks>
/// Every word in it was cut and neutralised by <see cref="BackgroundTaskText"/> before the broker held it, so a surface
/// encodes it as text and draws it on one line, and does nothing else with it. Operator-only, like the count beside it.
/// </remarks>
public sealed record BackgroundTask(string Id, TaskKind Kind, string Name, string Description = "", TaskLink? Link = null);
