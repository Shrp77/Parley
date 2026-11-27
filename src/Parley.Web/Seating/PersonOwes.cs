using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;

namespace Parley.Web.Seating;

/// <summary>What a project's agents are waiting on the person for, across its conversations.</summary>
/// <remarks>One implementation for the Inbox and the conversation page, so the two cannot list different things.</remarks>
internal static class PersonOwes
{
    /// <summary>Counts what waits on the person in every project given.</summary>
    /// <param name="seats">The book, for the seat this console took in a project.</param>
    /// <param name="directory">The projects, and a client for each.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>How many exchanges wait on the person's answer, and whether every project answered.</returns>
    /// <remarks>
    /// <para>
    /// <b>Read from the edges alone</b>, one call a project, because the frame asks it on every page. The Inbox reads the
    /// same edges through <see cref="WaitingOnThePerson.OnMe"/> and then each conversation for the words, so the count is the number of rows
    /// that page draws.
    /// </para>
    /// <para>
    /// The name is the seat this console took when it has taken one, and otherwise the one the broker provides
    /// (SPEC.md §12.3): what waits on the person waits against the name, and a project whose registry could not be read
    /// may still answer this.
    /// </para>
    /// </remarks>
    public static async Task<(int Waiting, bool Certain)> CountAsync(
        SeatBook seats, BrokerDirectory directory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(directory);

        // All at once: in sequence the cost is the sum of the timeouts of the brokers that are down.
        var reads = await Task.WhenAll(directory.Projects.Select(async project =>
        {
            var me = seats.Known(project.Name.Value)?.Name ?? PersonSeat.On(project.Name);

            return await BrokerDirectory.ReadAsync(async () =>
                await WaitingOnThePerson.CountAsync(new BrokerClient(directory.ClientFor(project)), me, token).ConfigureAwait(false))
                .ConfigureAwait(false);
        })).ConfigureAwait(false);

        return (reads.Where(read => read.Reachable).Sum(read => read.Value), reads.All(read => read.Reachable));
    }

    /// <summary>Reads what is waiting on the person, each as the agent's latest message in that exchange.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="me">The person's seat.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>The messages, oldest first, in the shape a page draws a message in.</returns>
    /// <remarks>The shared read, <see cref="WaitingOnThePerson.ReadAsync"/>, so the terminal lists the same questions.</remarks>
    public static Task<IReadOnlyList<Delivered>> ReadAsync(BrokerClient broker, AgentName me, CancellationToken token) =>
        WaitingOnThePerson.ReadAsync(broker, me, token);
}
