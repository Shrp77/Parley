using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>What a background task is called on every surface: its name or a plain stand-in, its kind, and its link's
/// words (SPEC.md §11.6).</summary>
/// <remarks>
/// <para>
/// <b>One place</b>, so the graph, the agent's page and the terminal name a task the same way. Every word returned is plain
/// text: an agent's or a runner's, already held to its bounds by <see cref="BackgroundTaskText"/>, or Parley's own. A
/// surface encodes it for where it draws it and never reads it as markup.
/// </para>
/// <para>
/// A task the node could not name is still a task, drawn as what kind of work it is, so a surface lists as many tasks as
/// the count says.
/// </para>
/// </remarks>
public static class TaskWords
{
    /// <summary>How many tasks make one page: the agent's page lists this many at a time, and the picture's list in words
    /// names this many before it sends the person there.</summary>
    /// <remarks>Twenty, as the person asked.</remarks>
    public const int MostListed = 20;

    /// <summary>What a task is called: its name, or what kind of work it is.</summary>
    /// <param name="task">The task, or null when the node named none at this place.</param>
    /// <returns>The words.</returns>
    public static string Name(BackgroundTask? task) =>
        task is { Name.Length: > 0 } ? task.Name : task?.Kind switch
        {
            TaskKind.Agent => "Background sub-agent",
            TaskKind.Command => "Background command",
            _ => "Background task",
        };

    /// <summary>What kind of work a named task is, to draw beside its name; empty for an unnamed one, whose stand-in already
    /// says it.</summary>
    /// <param name="task">The task.</param>
    /// <returns>The word, or empty.</returns>
    public static string Kind(BackgroundTask? task) =>
        task is not { Name.Length: > 0 } ? string.Empty : task.Kind switch
        {
            TaskKind.Agent => "sub-agent",
            TaskKind.Command => "command",
            _ => "task",
        };

    /// <summary>What a link is called beside its task: where it goes, and for a web address that it opens a new tab.</summary>
    /// <param name="link">The link.</param>
    /// <returns>The words.</returns>
    public static string Link(TaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return link.Kind == TaskLinkKind.Web ? Where(link) + " (opens a new tab)" : Where(link);
    }

    /// <summary>Where a link goes, in the fewest words: <c>exchange #57</c>, the conversation's or file's name, or a web
    /// address's host in punycode.</summary>
    /// <param name="link">The link.</param>
    /// <returns>The words.</returns>
    public static string Where(TaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return link.Kind switch
        {
            TaskLinkKind.Exchange => "exchange #" + link.Target,
            TaskLinkKind.Web => link.Host,
            _ => link.Target,
        };
    }

    /// <summary>A link written so a person can copy it where nothing can be followed (the terminal): a web address by its
    /// host first and then in full, anything else by where it goes in Parley.</summary>
    /// <param name="link">The link.</param>
    /// <returns>The words.</returns>
    public static string Copyable(TaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return link.Kind == TaskLinkKind.Web ? $"{link.Host}: {link.Target}" : Where(link);
    }

    /// <summary>Every task a count says is running, named where the node named it and a stand-in where it did not.</summary>
    /// <param name="named">The tasks the node named, in its order.</param>
    /// <param name="count">How many are running.</param>
    /// <returns>As many entries as <paramref name="count"/>, or as the node named if that were ever more: each task, or null
    /// for one the node did not name.</returns>
    public static IReadOnlyList<BackgroundTask?> Every(IReadOnlyList<BackgroundTask> named, int count)
    {
        ArgumentNullException.ThrowIfNull(named);

        return [.. Enumerable.Range(0, Math.Max(count, named.Count)).Select(i => i < named.Count ? named[i] : null)];
    }
}
