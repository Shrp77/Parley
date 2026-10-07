using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// A roadmap being edited: the model both surfaces share, so that editing a proposal or a confirmed roadmap means the
/// same thing on a screen and on a page (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Items are identified by themselves while they are edited, not by their position</b>, and positions come back only
/// when the content is written out. A person moving item four above item two is the ordinary edit, and a model that kept
/// dependencies as positions would have to renumber every one of them on each move and could leave an item needing
/// something that had slid below it without anybody having said so. Kept as references instead, a move changes the order
/// and nothing else, and <see cref="Problem"/> says what the move broke.
/// </para>
/// <para>
/// <b>It judges nothing the broker judges differently.</b> <see cref="Problem"/> is <see cref="RoadmapRules"/>, which is
/// also what the broker applies to the final content, so a surface that tells a person before it sends is not a second
/// opinion. What it cannot know is whether an intended agent is in the project; the broker asks the registry.
/// </para>
/// <para>
/// <b>An item that came from a confirmed roadmap keeps its number</b>, which is how an edit that reorders or rewords it
/// still means the same item and keeps its conversation (SPEC.md §5.7). A new item has none, and one removed here goes
/// from the content, which the broker records as removed and not deleted.
/// </para>
/// </remarks>
public sealed class RoadmapDraft
{
    private readonly List<Entry> _items = [];

    private RoadmapDraft(string title)
    {
        Title = title;
    }

    /// <summary>Gets or sets the roadmap's title.</summary>
    public string Title { get; set; }

    /// <summary>Gets how many items the draft holds.</summary>
    public int Count => _items.Count;

    /// <summary>Starts a draft from a proposal's content, or from content a person has already begun to edit.</summary>
    /// <param name="content">The content, whose needs are positions in its own list.</param>
    /// <returns>The draft.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static RoadmapDraft From(RoadmapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var draft = new RoadmapDraft(content.Title);

        foreach (var item in content.Items)
        {
            draft._items.Add(new Entry(item.Number, item.Text, item.IntendedAgent));
        }

        for (var at = 0; at < content.Items.Count; at++)
        {
            foreach (var needed in content.Items[at].Needs)
            {
                // A reference that points nowhere is kept out rather than invented: a draft cannot hold a dependency
                // on nothing, and the content it came from is judged by the rules, not by this.
                if (needed >= 1 && needed <= draft._items.Count && needed - 1 != at)
                {
                    draft._items[at].Needs.Add(draft._items[needed - 1]);
                }
            }
        }

        return draft;
    }

    /// <summary>Starts a draft from a confirmed roadmap's live items, in order.</summary>
    /// <param name="roadmap">The roadmap as read.</param>
    /// <returns>The draft, with each item keeping its number.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roadmap"/> is <see langword="null"/>.</exception>
    public static RoadmapDraft From(Roadmap roadmap)
    {
        ArgumentNullException.ThrowIfNull(roadmap);

        var live = roadmap.Items.Where(i => !i.Removed).OrderBy(i => i.Position).ToList();
        var draft = new RoadmapDraft(roadmap.Title);

        foreach (var item in live)
        {
            draft._items.Add(new Entry(item.Number, item.Text, item.IntendedAgent));
        }

        for (var at = 0; at < live.Count; at++)
        {
            foreach (var needed in live[at].Needs)
            {
                var target = draft._items.FirstOrDefault(e => e.Number == needed);

                if (target is not null && target != draft._items[at])
                {
                    draft._items[at].Needs.Add(target);
                }
            }
        }

        return draft;
    }

    /// <summary>Appends a new item, or inserts it after another.</summary>
    /// <param name="text">One line saying what is to be done.</param>
    /// <param name="agent">Who it is meant for, or <see langword="null"/>.</param>
    /// <param name="after">The zero-based index it goes after, or <see langword="null"/> for the end.</param>
    /// <returns>Where it went.</returns>
    public int Add(string text, AgentName? agent = null, int? after = null)
    {
        var at = after is { } index ? Math.Clamp(index + 1, 0, _items.Count) : _items.Count;

        _items.Insert(at, new Entry(null, text, agent));

        return at;
    }

    /// <summary>Rewords an item.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <param name="text">What it now says.</param>
    public void Reword(int index, string text) => At(index).Text = text;

    /// <summary>Changes who an item is meant for.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <param name="agent">The agent, or <see langword="null"/> for nobody.</param>
    public void Direct(int index, AgentName? agent) => At(index).Agent = agent;

    /// <summary>Removes an item.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The zero-based indexes, after the removal, of the items that needed it and no longer do.</returns>
    /// <remarks>
    /// A removed item is never the dependency of a live one (SPEC.md §5.7), so what needed it stops needing it and the
    /// caller says which, rather than the removal silently rewriting other items.
    /// </remarks>
    public IReadOnlyList<int> Remove(int index)
    {
        var gone = At(index);
        var affected = new List<int>();

        _items.Remove(gone);

        for (var at = 0; at < _items.Count; at++)
        {
            if (_items[at].Needs.Remove(gone))
            {
                affected.Add(at);
            }
        }

        return affected;
    }

    /// <summary>Moves an item up or down the order.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <param name="by">Minus one for up, plus one for down.</param>
    /// <returns>Its new index, or the old one when it is already at that end.</returns>
    /// <remarks>
    /// Changes the order and nothing else. If the move leaves an item needing one that now comes after it,
    /// <see cref="Problem"/> says so and the person decides which to change.
    /// </remarks>
    public int Move(int index, int by)
    {
        var item = At(index);
        var to = index + by;

        if (to < 0 || to >= _items.Count)
        {
            return index;
        }

        _items.RemoveAt(index);
        _items.Insert(to, item);

        return to;
    }

    /// <summary>Says which items an item needs.</summary>
    /// <param name="index">The zero-based index of the item.</param>
    /// <param name="needs">The zero-based indexes of the items it needs.</param>
    public void Need(int index, IEnumerable<int> needs)
    {
        var item = At(index);

        item.Needs.Clear();

        foreach (var needed in needs.Distinct())
        {
            if (needed != index)
            {
                item.Needs.Add(At(needed));
            }
        }
    }

    /// <summary>Reads what an item needs.</summary>
    /// <param name="index">The zero-based index of the item.</param>
    /// <returns>The zero-based indexes of the items it needs, in the order they now sit.</returns>
    public IReadOnlyList<int> Needs(int index) =>
        [.. At(index).Needs.Select(n => _items.IndexOf(n)).Order()];

    /// <summary>Writes the draft out as content, with needs as one-based positions.</summary>
    /// <returns>The content.</returns>
    public RoadmapContent ToContent() =>
        new(
            Title,
            [
                .. _items.Select(item => new RoadmapItemDraft(
                    item.Number,
                    item.Text,
                    item.Agent,
                    [.. item.Needs.Select(n => _items.IndexOf(n) + 1).Order()])),
            ]);

    /// <summary>Says what is wrong with the draft as it stands, or nothing.</summary>
    /// <returns>A sentence, or <see langword="null"/> when the draft is whole.</returns>
    public string? Problem() => RoadmapRules.Problem(ToContent());

    /// <summary>Says whether the draft differs from content it began as.</summary>
    /// <param name="original">The proposal's content, or the content last read.</param>
    /// <returns><see langword="true"/> when the title, the words, the agents, the order or the needs differ.</returns>
    /// <remarks>
    /// Compared by words and not by flag, as the broker compares them to say whether a roadmap was confirmed as proposed
    /// or edited: a flag a surface set would be right every time somebody remembered to set it.
    /// </remarks>
    public bool ChangedFrom(RoadmapContent original)
    {
        ArgumentNullException.ThrowIfNull(original);

        var now = ToContent();

        if (!string.Equals(now.Title, original.Title, StringComparison.Ordinal) || now.Items.Count != original.Items.Count)
        {
            return true;
        }

        for (var at = 0; at < now.Items.Count; at++)
        {
            var a = now.Items[at];
            var b = original.Items[at];

            if (!string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                || a.IntendedAgent != b.IntendedAgent
                || a.Number != b.Number
                || !a.Needs.Order().SequenceEqual(b.Needs.Order()))
            {
                return true;
            }
        }

        return false;
    }

    private Entry At(int index) =>
        index >= 0 && index < _items.Count
            ? _items[index]
            : throw new ArgumentOutOfRangeException(nameof(index), index, "There is no such item.");

    private sealed class Entry(int? number, string text, AgentName? agent)
    {
        public int? Number { get; } = number;

        public string Text { get; set; } = text;

        public AgentName? Agent { get; set; } = agent;

        public List<Entry> Needs { get; } = [];
    }
}
