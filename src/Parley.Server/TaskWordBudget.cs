using System.Text;
using Parley.Core;

namespace Parley.Server;

/// <summary>How many bytes of background tasks' words one registry reply may carry (feature 043).</summary>
/// <param name="most">The most bytes, across every row.</param>
/// <remarks>
/// <para>
/// <b>Why it exists</b>: every agent's row travels in one reply, and a gRPC reply past its receiver's limit (4 MiB by
/// default) is refused whole. Each task's words are bounded, but an agent that started a few hundred background tasks and
/// described each could otherwise make the person's registry too large to read, hiding every agent on both surfaces.
/// </para>
/// <para>
/// Spent in the order the rows are drawn. A task past the budget still travels, by id and kind, so the count and the list
/// still agree; only its words are left out, and a surface draws it as a plain stand-in. Not thread-safe: one per reply.
/// </para>
/// </remarks>
internal sealed class TaskWordBudget(int most)
{
    private int _left = most;

    /// <summary>Takes what fits.</summary>
    /// <param name="tasks">One agent's running tasks.</param>
    /// <returns>The same tasks, with the words of those past the budget left out.</returns>
    public IReadOnlyList<BackgroundTask> Spend(IReadOnlyList<BackgroundTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var kept = new List<BackgroundTask>(tasks.Count);

        foreach (var task in tasks)
        {
            var cost = Encoding.UTF8.GetByteCount(task.Name)
                + Encoding.UTF8.GetByteCount(task.Description)
                + (task.Link is { } link ? Encoding.UTF8.GetByteCount(link.Written) : 0);

            if (cost <= _left)
            {
                _left -= cost;
                kept.Add(task);
            }
            else
            {
                _left = 0;
                kept.Add(new BackgroundTask(task.Id, task.Kind, string.Empty));
            }
        }

        return kept;
    }
}
