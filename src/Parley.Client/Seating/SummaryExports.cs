namespace Parley.Client.Seating;

/// <summary>Reads the summary of many conversations from one broker without queueing the whole project behind them.</summary>
/// <remarks>
/// The broker serves one call at a time and in turn, so two hundred exports asked at once put every other call, an agent's
/// among them, behind all two hundred: measured at 1.4 s for a bystander's trivial call, against 40 to 150 ms with eight in
/// flight and the same total time. Both the Inbox and the list of what waits on the person read this way.
/// </remarks>
internal static class SummaryExports
{
    private const int InFlight = 8;

    /// <summary>Reads each conversation's summary, a few at a time.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="conversations">The conversations.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The summaries, in the order asked.</returns>
    public static async Task<IReadOnlyList<Core.ConversationExport>> ReadAsync(
        BrokerClient broker, IReadOnlyList<string> conversations, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(InFlight);

        var reads = conversations.Select(async id =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await broker.ExportAsync(id, cancellationToken, latestOnly: true).ConfigureAwait(false);
            }
            finally
            {
                _ = slots.Release();
            }
        });

        return await Task.WhenAll(reads).ConfigureAwait(false);
    }
}
