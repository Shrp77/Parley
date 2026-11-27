using System.Collections.Concurrent;
using Parley.Core;

namespace Parley.Server;

/// <summary>
/// The fetches this broker streamed to completion and has not yet been told were written (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>What lets a report of a fetch be believed.</b> A fetch is recorded, and a gone notice follows it, only once
/// the adapter reports it wrote and verified the content. Without this, an agent could report having fetched a
/// file it never asked for, or a hash it never received, and the broker would record it — harmless noise, but a
/// record saying something that did not happen, which is what this store exists not to hold.
/// </para>
/// <para>
/// <b>Keyed by the content, not only by the file</b>, so two fetches of one name by one agent, with a removal and a
/// new placement between them, are each reportable with the hash each actually streamed.
/// </para>
/// <para>
/// In memory, and forgotten when the agent's last session ends or the broker restarts. A report that arrives
/// after either is refused, the file stays written, and the adapter says no notice will follow — which is an
/// unknown honestly reported rather than a record the broker cannot stand behind.
/// </para>
/// </remarks>
public sealed class ServedFetches
{
    // The unit separator: no agent name, conversation identifier, name key or hash can contain it. Spelled as a
    // number because a raw control character in source is invisible to a reader and to a diff.
    private const char Separator = (char)0x1F;

    // How many streams of exactly that content to that agent await their report. A count, not a mark: one agent
    // fetching one file several times at once streams it several times, and each fetch reports (review, lens 1 run
    // 63: with a mark, every report but the first was told the fetch went unrecorded, though it was recorded).
    private readonly ConcurrentDictionary<string, int> _served = new(StringComparer.Ordinal);

    /// <summary>Records that a fetch streamed its whole content, verified against the row's hash.</summary>
    /// <param name="agent">The agent it was streamed to.</param>
    /// <param name="conversationId">The file's conversation.</param>
    /// <param name="nameKey">The file's name key.</param>
    /// <param name="sha256">The hash of what was streamed.</param>
    public void Served(AgentName agent, string conversationId, string nameKey, ContentHash sha256) =>
        _ = _served.AddOrUpdate(Key(agent, conversationId, nameKey, sha256), 1, (_, waiting) => waiting + 1);

    /// <summary>Takes the record of a completed fetch, if there is one.</summary>
    /// <param name="agent">The agent reporting.</param>
    /// <param name="conversationId">The file's conversation.</param>
    /// <param name="nameKey">The file's name key.</param>
    /// <param name="sha256">The hash the agent says it wrote.</param>
    /// <returns><see langword="true"/> when exactly that content was streamed to that agent and not yet reported.</returns>
    public bool TryConsume(AgentName agent, string conversationId, string nameKey, ContentHash sha256)
    {
        var key = Key(agent, conversationId, nameKey, sha256);

        while (_served.TryGetValue(key, out var waiting))
        {
            if (waiting <= 1
                    ? _served.TryRemove(new KeyValuePair<string, int>(key, waiting))
                    : _served.TryUpdate(key, waiting - 1, waiting))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets every unreported fetch of an agent whose last session has ended.</summary>
    /// <param name="agent">The agent.</param>
    public void Forget(AgentName agent)
    {
        var prefix = agent.Value + Separator;

        foreach (var key in _served.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _ = _served.TryRemove(key, out _);
        }
    }

    private static string Key(AgentName agent, string conversationId, string nameKey, ContentHash sha256) =>
        string.Join(Separator, agent.Value, conversationId, nameKey, sha256.Value);
}
