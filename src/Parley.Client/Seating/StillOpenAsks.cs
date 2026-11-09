using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>One Request asking an agent which of its questions to the person are still open.</summary>
/// <param name="Project">The project it was sent in.</param>
/// <param name="Agent">Who was asked.</param>
/// <param name="Conversation">The conversation it was sent in, which is where its questions are.</param>
/// <param name="Questions">The exchanges it asked about.</param>
/// <param name="Exchange">The exchange the Request opened, or <see langword="null"/> when it was not sent.</param>
/// <param name="Said">How the agent was reached when it was sent, or why it was not sent.</param>
public sealed record StillOpenAsk(
    string Project,
    AgentName Agent,
    string Conversation,
    IReadOnlyList<long> Questions,
    long? Exchange,
    string Said);

/// <summary>Where one ask stands now.</summary>
/// <param name="Ask">What was asked, of whom and where.</param>
/// <param name="Closed">How many of the questions asked about are closed now, whoever closed them.</param>
/// <param name="State">Where its answer stands, in a few words.</param>
/// <param name="Detail">More about it: how the agent was reached, what is left, or why it was not sent.</param>
public sealed record StillOpenStanding(StillOpenAsk Ask, int Closed, string State, string Detail);

/// <summary>What one surface last asked about which questions are still open, so it can show where each answer stands.</summary>
/// <remarks>
/// <para>
/// <b>Only which exchanges were asked about, never where they stand.</b> Where each stands is read from the broker each time
/// it is shown, so the list is never what the surface happened to see last. A press replaces the record for the projects it
/// asked in and keeps the others: the web console asks in every project at once, the terminal in the one it shows.
/// </para>
/// <para>
/// <b>Kept in memory, on purpose.</b> A restart forgets it and loses nothing: the Requests and their replies are in their
/// conversations, and the questions still open are still listed.
/// </para>
/// </remarks>
public sealed class StillOpenAsks
{
    private readonly Lock _lock = new();

    private IReadOnlyList<StillOpenAsk> _latest = [];

    private int _asking;

    /// <summary>Claims the one round this surface may run at a time.</summary>
    /// <returns>True when no round was running; the caller then owes <see cref="EndRound"/>.</returns>
    /// <remarks>
    /// <b>A round reads what was asked, sends, and only then records it</b>, so two rounds at once both found nothing asked
    /// and both sent: a double click asked every agent twice. The terminal's rounds already run one at a time on its work
    /// queue; the web console's arrive as concurrent requests, so a press made while one runs sends nothing.
    /// </remarks>
    public bool TryBeginRound() => Interlocked.CompareExchange(ref _asking, 1, 0) == 0;

    /// <summary>Releases the round <see cref="TryBeginRound"/> claimed.</summary>
    public void EndRound() => Interlocked.Exchange(ref _asking, 0);

    /// <summary>Replaces what was last asked in the projects given.</summary>
    /// <param name="projects">The projects the press asked in.</param>
    /// <param name="asks">Every Request of the press, sent, kept from before, or refused.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void Replace(IReadOnlyCollection<string> projects, IReadOnlyList<StillOpenAsk> asks)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(asks);

        lock (_lock)
        {
            _latest = [.. _latest.Where(ask => !projects.Contains(ask.Project)), .. asks];
        }
    }

    /// <summary>What was last asked.</summary>
    /// <returns>Every Request of the latest press in each project, or none.</returns>
    public IReadOnlyList<StillOpenAsk> Latest()
    {
        lock (_lock)
        {
            return _latest;
        }
    }
}
