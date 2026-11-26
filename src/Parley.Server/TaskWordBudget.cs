using System.Text;
using Parley.Core;

namespace Parley.Server;

/// <summary>How many bytes of background tasks one registry reply may carry (SPEC.md §11.6).</summary>
/// <param name="most">The most bytes, across every row.</param>
/// <remarks>
/// <para>
/// <b>Why it exists</b>: every agent's row travels in one reply, and a gRPC reply past its receiver's limit (4 MiB by
/// default) is refused whole. Each task's words are bounded, but agents that started a few hundred background tasks and
/// described each could otherwise make the person's registry too large to read, hiding every agent on both surfaces.
/// </para>
/// <para>
/// Spent in the order the rows are drawn, by a task's whole size on the wire: its id, its words and a little for the
/// fields around them. A task whose words do not fit still travels, by id and kind, so the count and the list still agree,
/// and a surface draws it as a plain stand-in; later tasks whose words fit keep theirs. Not thread-safe: one per reply.
/// </para>
/// </remarks>
internal sealed class TaskWordBudget(int most)
{
    /// <summary>What a task costs on the wire besides its id and words: its kind and the fields' tags and lengths.</summary>
    internal const int Framing = 16;

    private int _left = most;

    /// <summary>Takes what fits.</summary>
    /// <param name="tasks">One agent's running tasks.</param>
    /// <returns>The same tasks, with the words of those that do not fit left out.</returns>
    public IReadOnlyList<BackgroundTask> Spend(IReadOnlyList<BackgroundTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var kept = new List<BackgroundTask>(tasks.Count);

        foreach (var task in tasks)
        {
            // The id travels whatever happens to the words, so it is paid for first.
            _left = Math.Max(0, _left - Encoding.UTF8.GetByteCount(task.Id) - Framing);

            var words = Encoding.UTF8.GetByteCount(task.Name)
                + Encoding.UTF8.GetByteCount(task.Description)
                + (task.Link is { } link ? Encoding.UTF8.GetByteCount(link.Written) : 0);

            if (words <= _left)
            {
                _left -= words;
                kept.Add(task);
            }
            else
            {
                kept.Add(new BackgroundTask(task.Id, task.Kind, string.Empty));
            }
        }

        return kept;
    }
}
