using Parley.Core;

namespace Parley.Web;

/// <summary>Where a background task's link goes in the console, and how it is shown (SPEC.md §10.1, §11.6).</summary>
/// <remarks>
/// <para>
/// <b>One place</b>, so the graph and the agent's page send a person to the same place for the same link. The link is the
/// only thing in a task a person may follow, and it reached here already held to its four shapes
/// (<see cref="BackgroundTaskText.Link"/>); nothing else in a task (its name, its description) is ever drawn as a link or
/// read as markup.
/// </para>
/// <para>
/// A conversation's or file's link is opened in the project whose broker named the task: a conversation's name is unique
/// only within its project. An exchange is opened on the agent's page, which shows it in place as a message's reference
/// does. A web address is opened in a new tab with no opener and no referrer, its host shown beside it in punycode so a
/// person sees where it goes before following it.
/// </para>
/// </remarks>
public static class TaskLinks
{
    /// <summary>Where the link goes.</summary>
    /// <param name="link">The task's link.</param>
    /// <param name="project">The project whose broker named the task.</param>
    /// <param name="agentPage">The agent's own page, where an exchange is shown.</param>
    /// <returns>The address, already escaped for a URL, and whether it leaves the console.</returns>
    public static (string Href, bool External) Href(TaskLink link, string project, string agentPage)
    {
        ArgumentNullException.ThrowIfNull(link);

        var p = Uri.EscapeDataString(project);

        return link.Kind switch
        {
            TaskLinkKind.Conversation => ($"/Conversations/Detail?project={p}&id={Uri.EscapeDataString(link.Target)}", false),
            TaskLinkKind.File => ($"/Projects/Files?project={p}", false),
            TaskLinkKind.Exchange => (agentPage + "#background-work", false),
            _ => (link.Target, true),
        };
    }

    /// <summary>What the link is called beside the task: <c>exchange #57</c>, the conversation's or file's name, or the
    /// web address's host.</summary>
    /// <param name="link">The task's link.</param>
    /// <returns>The words, plain text.</returns>
    public static string Words(TaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return link.Kind switch
        {
            TaskLinkKind.Exchange => "exchange #" + link.Target,
            TaskLinkKind.Conversation => link.Target,
            TaskLinkKind.File => link.Target,
            _ => link.Host,
        };
    }

    /// <summary>What a task is called where it has no name: what kind of work it is.</summary>
    /// <param name="task">The task.</param>
    /// <returns>A plain stand-in.</returns>
    public static string Name(BackgroundTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.Name.Length > 0
            ? task.Name
            : task.Kind switch
            {
                TaskKind.Agent => "Background sub-agent",
                TaskKind.Command => "Background command",
                _ => "Background task",
            };
    }
}
