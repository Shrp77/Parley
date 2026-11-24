using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>One exchange, as a reference to it opens it (spec 036, FR-020).</summary>
/// <param name="Exchange">Its number.</param>
/// <param name="Conversation">The conversation it is in now, or <see langword="null"/> when there is no such exchange.</param>
/// <param name="Messages">Every message on it the store still holds, oldest first.</param>
/// <param name="Snapshot">Its state, or <see langword="null"/> when there is no such exchange.</param>
/// <param name="WaitingOn">For an open exchange: who it waits on.</param>
public sealed record ExchangeDetail(
    long Exchange,
    string? Conversation,
    IReadOnlyList<Message> Messages,
    ExchangeSnapshot? Snapshot,
    AgentName? WaitingOn)
{
    /// <summary>Whether the store has it.</summary>
    public bool Found => Conversation is not null && Snapshot is not null;

    /// <summary>Who has been on it, in the order they first spoke.</summary>
    public IReadOnlyList<AgentName> Agents => [.. Messages.SelectMany(m => new[] { m.From, m.To }).Distinct()];

    /// <summary>Its state, in words.</summary>
    /// <param name="ended">How it ended, when closed: read the same way the Inbox's Closed view reads it.</param>
    /// <returns>A sentence.</returns>
    public string State(ExchangeEnding? ended) => Snapshot switch
    {
        null => $"There is no exchange #{Exchange} in this project.",
        { State: ExchangeState.Open } when WaitingOn is { } on => $"Open, waiting on {on.Value}.",
        { State: ExchangeState.Open } => "Open.",
        _ when ended is not null => $"Closed: {ended.Words}.",
        _ => "Closed.",
    };

    /// <summary>Reads one exchange by number. Changes nothing: no delivery is taken.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="exchange">Its number.</param>
    /// <param name="conversation">
    /// Where the broker says it is (<see cref="BrokerClient.LocateExchangeAsync"/>), or <see langword="null"/> when it said
    /// there is no such exchange. Located by each surface itself, so both are seen to bind the read.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The exchange, or one that says it was not found.</returns>
    public static async Task<ExchangeDetail> ReadAsync(
        BrokerClient broker, long exchange, string? conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        if (conversation is null)
        {
            return new ExchangeDetail(exchange, null, [], null, null);
        }

        var export = await broker.ExportAsync(conversation, cancellationToken).ConfigureAwait(false);
        var snapshot = export.Exchanges.FirstOrDefault(e => e.ExchangeId == exchange);
        AgentName? waitingOn = null;

        if (snapshot is { State: ExchangeState.Open })
        {
            var edges = await broker.WaitingAsync(cancellationToken).ConfigureAwait(false);

            waitingOn = edges.Where(e => e.ExchangeId == exchange).Select(e => e.WaitedOn).Cast<AgentName?>().FirstOrDefault();
        }

        return new ExchangeDetail(
            exchange,
            snapshot is null ? null : conversation,
            [.. export.Messages.Where(m => m.ExchangeId == exchange).OrderBy(m => m.Id)],
            snapshot,
            waitingOn);
    }

    /// <summary>How it ended, read as the Inbox's Closed view reads it, or <see langword="null"/> while it is open.</summary>
    /// <returns>The ending.</returns>
    public ExchangeEnding? Ending()
    {
        if (Snapshot is not { State: ExchangeState.Closed, ClosedUtc: { } closed } || Messages.Count == 0)
        {
            return null;
        }

        var first = Messages[0];
        var reading = Inbox.Build(
            first.To, [], [new ConversationExport(Messages, [Snapshot])], [], [], 1, 0, closed.AddTicks(-1), closed);

        return reading.Rows.FirstOrDefault()?.Ended;
    }
}
