namespace Parley.Web.Notifications;

/// <summary>One thing the console told the person, kept so it can be read again.</summary>
/// <param name="Id">Counts up from one for as long as the console runs.</param>
/// <param name="At">When it was said.</param>
/// <param name="Project">The project it happened in.</param>
/// <param name="Conversation">The conversation it concerns, or null when it concerns none.</param>
/// <param name="Source">Who it is about: the agent that was written to.</param>
/// <param name="Text">What the broker answered, as the person was shown it.</param>
/// <param name="Good">Whether it went through. A refusal is kept too, because it is the one the person must not miss.</param>
public sealed record Notification(
    long Id,
    DateTimeOffset At,
    string Project,
    string? Conversation,
    string Source,
    string Text,
    bool Good);

/// <summary>What the console has told the person, newest last, for the bell at the top of every page.</summary>
/// <remarks>
/// <para>
/// <b>In this process's memory and nowhere else.</b> It is a record of what this console said, not of what the
/// broker holds: the conversation is the record of a message, and a copy of its words stored somewhere the prune
/// does not reach would outlive it (SPEC.md §9.4). A restart empties it, and that is acceptable for a list whose job
/// is "what just happened".
/// </para>
/// <para>
/// One console serves one person, so what has been seen is one marker and not one per browser tab. Bounded, oldest
/// out first, so a console left open for a month does not grow without limit.
/// </para>
/// </remarks>
public sealed class NotificationLog
{
    /// <summary>How many are kept.</summary>
    public const int Capacity = 200;

    private readonly Lock gate = new();
    private readonly List<Notification> kept = [];
    private readonly TimeProvider clock;
    private long last;
    private long seen;

    /// <summary>Creates an empty log.</summary>
    /// <param name="clock">The clock to stamp with; null for the system's.</param>
    public NotificationLog(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    /// <summary>Records one.</summary>
    /// <param name="project">The project.</param>
    /// <param name="conversation">The conversation it concerns, or null.</param>
    /// <param name="source">Who it is about.</param>
    /// <param name="text">What was said.</param>
    /// <param name="good">Whether it went through.</param>
    /// <returns>What was recorded.</returns>
    public Notification Add(string project, string? conversation, string source, string text, bool good)
    {
        lock (gate)
        {
            var one = new Notification(++last, clock.GetUtcNow(), project, conversation, source, text, good);

            kept.Add(one);

            if (kept.Count > Capacity)
            {
                kept.RemoveAt(0);
            }

            return one;
        }
    }

    /// <summary>The most recent, newest first.</summary>
    /// <param name="count">How many at most.</param>
    /// <returns>A copy, so the caller can enumerate it while others add.</returns>
    public IReadOnlyList<Notification> Latest(int count)
    {
        lock (gate)
        {
            return [.. Enumerable.Reverse(kept).Take(count)];
        }
    }

    /// <summary>Everything kept, newest first.</summary>
    /// <returns>A copy.</returns>
    public IReadOnlyList<Notification> All() => Latest(Capacity);

    /// <summary>How many have been recorded since the person last looked.</summary>
    /// <returns>The count.</returns>
    public int Unread()
    {
        lock (gate)
        {
            return kept.Count(one => one.Id > seen);
        }
    }

    /// <summary>Marks everything so far as seen.</summary>
    public void MarkAllRead()
    {
        lock (gate)
        {
            seen = last;
        }
    }

    /// <summary>Whether one was recorded after the person last looked.</summary>
    /// <param name="one">The notification.</param>
    /// <returns><see langword="true"/> when it has not been seen.</returns>
    public bool IsUnread(Notification one)
    {
        ArgumentNullException.ThrowIfNull(one);

        lock (gate)
        {
            return one.Id > seen;
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (gate)
        {
            kept.Clear();
            seen = last;
        }
    }
}
