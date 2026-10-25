using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>Where one roadmap item stands, derived from what the broker holds about its conversation (SPEC.md §5.7).</summary>
public enum ItemStanding
{
    /// <summary>No conversation exists, so nobody has been asked.</summary>
    NotStarted,

    /// <summary>An agent has collected the request and is working on it.</summary>
    InProgress,

    /// <summary>An agent has been asked and has not collected it, or is waiting on another agent.</summary>
    WaitingOnAgent,

    /// <summary>An agent has asked the person something in the item's conversation and is waiting for the answer.</summary>
    WaitingOnPerson,

    /// <summary>The work ended blocked, with the blocker the agent gave, in a conversation still open.</summary>
    Blocked,

    /// <summary>Work was done or answered, and no agent's latest done-or-blocked result on the person's request was blocked.</summary>
    Done,

    /// <summary>
    /// The conversation was closed, or destroyed, without the work done: a request ended before its agent answered, or a
    /// part was blocked, whose blocker is kept. Never read as not started.
    /// </summary>
    Closed,

    /// <summary>The state could not be read. Never shown as not started.</summary>
    CouldNotBeRead,
}

/// <summary>Why a roadmap names no next item, or that it does.</summary>
public enum NextKind
{
    /// <summary>An item is next.</summary>
    Item,

    /// <summary>Every live item is done.</summary>
    Complete,

    /// <summary>Nothing is ready: what remains is waiting, blocked, or needs something that is not done.</summary>
    NothingReady,

    /// <summary>The roadmap holds no live item.</summary>
    Empty,
}

/// <summary>One item and where it stands.</summary>
/// <param name="Item">The item as the broker returned it.</param>
/// <param name="Standing">Where it stands.</param>
/// <param name="Holders">The agents with something owed on it, when there are any.</param>
/// <param name="Blocker">
/// The blocker an agent gave, while any agent's part is blocked: on a blocked item, on a closed one, and beside open work.
/// </param>
/// <param name="TextChanged">Whether the item's text was edited after its conversation began.</param>
/// <param name="IsNext">Whether it is the roadmap's next item.</param>
public sealed record ItemState(
    RoadmapItem Item,
    ItemStanding Standing,
    IReadOnlyList<AgentName> Holders,
    string? Blocker,
    bool TextChanged,
    bool IsNext);

/// <summary>A roadmap's items with where each stands, and which is next.</summary>
/// <param name="Items">The live items, in order.</param>
/// <param name="Next">Whether an item is next, and if not why.</param>
/// <param name="NextNumber">The next item's number, when there is one.</param>
/// <param name="WaitingOnYou">How many items an agent is waiting on the person for.</param>
public sealed record RoadmapStanding(
    IReadOnlyList<ItemState> Items,
    NextKind Next,
    int? NextNumber,
    int WaitingOnYou);

/// <summary>
/// The one derivation of where a roadmap item stands, and which item is next (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived here and stored nowhere, and read by every surface.</b> The broker returns facts about each live item's
/// conversation (<see cref="ItemFacts"/>) and this turns them into a state, so the terminal, the web console and the
/// command line cannot disagree about it, and no column exists that could disagree with it. Two surfaces implementing
/// this separately is how they would come to differ about what a person has seen.
/// </para>
/// <para>
/// <b>A state that could not be read is never not started</b>. An item whose facts were not returned, or whose
/// open exchanges cannot be placed because the wait-for graph was not read, is <see cref="ItemStanding.CouldNotBeRead"/>:
/// telling a person that work had not begun when it had is a lie that sends them to start it twice.
/// </para>
/// <para>
/// <b>No operation sets a state</b> (spec clarification 1). An item finished or made irrelevant elsewhere is removed or
/// reworded by editing the roadmap, so a roadmap never shows a state no conversation supports.
/// </para>
/// </remarks>
public static class ItemStates
{
    /// <summary>Derives where each live item stands and which is next.</summary>
    /// <param name="roadmap">The roadmap as the broker returned it.</param>
    /// <param name="waiting">The wait-for graph, or <see langword="null"/> when it could not be read.</param>
    /// <param name="me">The person's name, whose move an edge naming them is.</param>
    /// <returns>The standing of every live item, and the next one.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static RoadmapStanding Of(Roadmap roadmap, IReadOnlyList<WaitingEdge>? waiting, AgentName me)
    {
        ArgumentNullException.ThrowIfNull(roadmap);

        var live = roadmap.Items.Where(i => !i.Removed).OrderBy(i => i.Position).ToList();
        var states = live.Select(item => Derive(item, waiting, me)).ToList();

        var done = states.Where(s => s.Standing == ItemStanding.Done).Select(s => s.Item.Number).ToHashSet();

        // The first item, in order, that has not been started and whose needs are all done. An item that needs one
        // not done is not next however early it comes, and one already started is not next at all.
        var next = states.FirstOrDefault(s =>
            s.Standing == ItemStanding.NotStarted && s.Item.Needs.All(done.Contains));

        var kind = states.Count == 0
            ? NextKind.Empty
            : next is not null
                ? NextKind.Item
                : states.All(s => s.Standing == ItemStanding.Done)
                    ? NextKind.Complete
                    : NextKind.NothingReady;

        if (next is not null)
        {
            states[states.IndexOf(next)] = next with { IsNext = true };
        }

        return new RoadmapStanding(
            states,
            kind,
            next?.Item.Number,
            states.Count(s => s.Standing == ItemStanding.WaitingOnPerson));
    }

    private static ItemState Derive(RoadmapItem item, IReadOnlyList<WaitingEdge>? waiting, AgentName me)
    {
        if (item.Facts is not { } facts)
        {
            return new ItemState(item, ItemStanding.CouldNotBeRead, [], null, false, false);
        }

        // From the item's edit record, never by comparing words: the person may change the words before sending them.
        var changed = facts.TextEditedSinceStarted;

        if (facts.Destroyed)
        {
            return new ItemState(item, ItemStanding.Closed, [], null, changed, false);
        }

        if (!facts.ConversationExists)
        {
            return new ItemState(item, ItemStanding.NotStarted, [], null, false, false);
        }

        // Named whatever else the item is doing, so open work beside a blocked part never hides the block.
        var blocker = facts.BlockedExchanges > 0 ? facts.Blocker : null;

        if (facts.OpenExchanges > 0)
        {
            // Open work outranks a blocked part: asking the blocked agent again reads as that request, not as the block
            // it may clear, and an item sent to two agents reads as the one still working. The blocker stays named.
            //
            // The graph is the only thing that says whose move an open exchange is. Without it the item is not known to
            // be anything: working, waiting on somebody, and waiting on the person all look the same from the facts.
            if (waiting is null)
            {
                return new ItemState(item, ItemStanding.CouldNotBeRead, [], null, changed, false);
            }

            var open = facts.OpenIds.ToHashSet();
            var edges = waiting.Where(e => open.Contains(e.ExchangeId)).ToList();

            if (edges.Any(e => e.WaitedOn == me))
            {
                return new ItemState(
                    item, ItemStanding.WaitingOnPerson, [.. edges.Where(e => e.WaitedOn == me).Select(e => e.Waiter).Distinct()],
                    blocker, changed, false);
            }

            var owing = edges.Select(e => e.WaitedOn).Distinct().OrderBy(a => a.Value, StringComparer.Ordinal).ToList();

            // An agent waiting on another agent, or a request nobody has collected yet: somebody else's move.
            var onAgent = edges.Any(e => e.Waiter != me) || !facts.Collected;

            return new ItemState(
                item, onAgent ? ItemStanding.WaitingOnAgent : ItemStanding.InProgress, owing, blocker, changed, false);
        }

        // With nothing open, a blocked part decides the item, so it is never done. Blocked while the conversation is open,
        // because asking again is what clears it; closed, by the person or by its participants, nobody can ask again, so it
        // reads closed, still naming the blocker: one agent may close a conversation that holds nothing open, and a close
        // that dropped the blocker hid it from the person.
        if (facts.BlockedExchanges > 0)
        {
            return new ItemState(
                item, facts.ClosedBy is null ? ItemStanding.Blocked : ItemStanding.Closed, [], facts.Blocker, changed, false);
        }

        if (facts.CompleteExchanges > 0 && facts.ClosedExchanges == 0)
        {
            return new ItemState(item, ItemStanding.Done, [], null, changed, false);
        }

        // A request ended before its agent answered (closed by a person or by the broker), or a conversation closed or
        // destroyed with nothing done: not the work done.
        return facts.CompleteExchanges + facts.ClosedExchanges > 0 || facts.ClosedBy is not null
            ? new ItemState(item, ItemStanding.Closed, [], null, changed, false)
            : new ItemState(item, ItemStanding.NotStarted, [], null, false, false);
    }

    /// <summary>The character at the front of an item, from where it stands. Never the only thing that says it.</summary>
    /// <param name="standing">Where it stands.</param>
    /// <returns>Three characters, for example <c>[x]</c>.</returns>
    public static string Mark(ItemStanding standing) => standing switch
    {
        ItemStanding.NotStarted => "[ ]",
        ItemStanding.InProgress => "[>]",
        ItemStanding.WaitingOnAgent => "[~]",
        ItemStanding.WaitingOnPerson => "[?]",
        ItemStanding.Blocked => "[!]",
        ItemStanding.Done => "[x]",
        ItemStanding.Closed => "[-]",
        ItemStanding.CouldNotBeRead => "[*]",
        _ => throw new ArgumentOutOfRangeException(nameof(standing), standing, "Not a standing."),
    };
}
