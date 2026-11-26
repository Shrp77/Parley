using Parley.Core;

namespace Parley.Web;

/// <summary>Where a background task's link goes in the console (SPEC.md §10.1, §11.6).</summary>
/// <remarks>
/// <para>
/// <b>One place</b>, so the graph and the agent's page send a person to the same place for the same link; what the link is
/// called is <see cref="Parley.Client.Rendering.TaskWords"/>'s. The link is the only thing in a task a person may follow,
/// and nothing else in a task (its name, its description) is ever drawn as a link or read as markup.
/// </para>
/// <para>
/// <b>Held to its shape again here</b>, as it is drawn: a web address is followed only when reading its spelling again
/// gives the same https address, and a kind this console does not know is not followed at all. A surface does not lean on
/// every path that built the link having checked it.
/// </para>
/// <para>
/// A conversation's or file's link opens in the project whose broker named the task, since a conversation's name is
/// unique only within its project. An exchange opens at the task on the agent's page, which shows it in place as a
/// message's reference does. A web address opens in a new tab with no opener and no referrer.
/// </para>
/// </remarks>
public static class TaskLinks
{
    /// <summary>Where the link goes.</summary>
    /// <param name="link">The task's link.</param>
    /// <param name="project">The project whose broker named the task.</param>
    /// <param name="atTask">The task's place on the agent's page, where an exchange is shown.</param>
    /// <returns>The address, already escaped for a URL, and whether it leaves the console; a null address when the link
    /// is not one to follow.</returns>
    public static (string? Href, bool External) Href(TaskLink link, string project, string atTask)
    {
        ArgumentNullException.ThrowIfNull(link);

        var p = Uri.EscapeDataString(project);

        return link.Kind switch
        {
            TaskLinkKind.Conversation when ConversationId.IsValid(link.Target) =>
                ($"/Conversations/Detail?project={p}&id={Uri.EscapeDataString(link.Target)}", false),
            TaskLinkKind.File => ($"/Projects/Files?project={p}", false),
            TaskLinkKind.Exchange => (atTask, false),
            TaskLinkKind.Web when BackgroundTaskText.Link(link.Written) is { Kind: TaskLinkKind.Web } again && again == link =>
                (link.Target, true),
            _ => (null, false),
        };
    }
}
