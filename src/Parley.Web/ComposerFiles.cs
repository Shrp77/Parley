using Parley.Client;
using Parley.Client.Files;
using Parley.Client.Rendering;
using Parley.Core;

namespace Parley.Web;

/// <summary>Places the files a person attached in a message box, before the message naming them is sent.</summary>
/// <remarks>
/// <para>
/// <b>One implementation for every message box</b> (SPEC.md §5.6: every place a person writes a message to an agent
/// behaves the same). The conversation page carried this alone, and the Inbox's boxes took no files at all.
/// </para>
/// <para>
/// <b>Placed before anything is sent</b>, so a message never names a file that is not there. Each file placed moves from
/// <c>attachments</c> to <c>attached</c> as its name and hash, so a send refused afterwards keeps it and a retry does not
/// upload it again: a browser cannot fill a file field a second time.
/// </para>
/// </remarks>
public static class ComposerFiles
{
    /// <summary>Places each attachment in a conversation, stopping at the first that cannot be placed.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="conversation">The conversation the message goes in.</param>
    /// <param name="attachments">What the browser sent; each placed file is taken out.</param>
    /// <param name="attached">Files already placed, as entries; each placed file is added.</param>
    /// <param name="token">Cancels the work.</param>
    /// <returns>Why nothing may be sent, or nothing; and whether the conversation does not exist yet.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task<(string? Problem, bool ConversationNotStarted)> PlaceAsync(
        BrokerClient broker,
        string conversation,
        IList<IFormFile> attachments,
        IList<string> attached,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(attached);

        foreach (var file in attachments.ToList())
        {
            var content = file.OpenReadStream();

            await using (content.ConfigureAwait(false))
            {
                var placed = await BrokerDirectory.ReadAsync(
                    () => broker.PlacePersonsFileAsync(conversation, Path.GetFileName(file.FileName), content, token)).ConfigureAwait(false);

                if (placed.Value is { Rejection: FileRejection.ConversationNotAssignable })
                {
                    return (null, true);
                }

                if (placed.Value is { Outcome: PlaceOutcome.Placed or PlaceOutcome.AlreadyCurrent, Sha256: { } hash } done)
                {
                    attached.Add(Attaching.Entry(done.Name, hash.Value));
                    attachments.Remove(file);
                    continue;
                }

                var why = placed.Reachable
                    ? PlacementSaid.Of(placed.Value!, file.FileName, bytes => $"{bytes} bytes")
                    : placed.Problem;

                return ($"Nothing was sent. {file.FileName} could not be attached, so attach it again: {why}", false);
            }
        }

        return (null, false);
    }

    /// <summary>Places the attached files in a conversation the words have just gone to, then names them to everybody reached.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="voice">The person's voice.</param>
    /// <param name="notices">What the console tells the person.</param>
    /// <param name="project">The project's name.</param>
    /// <param name="conversation">The conversation the words went to.</param>
    /// <param name="attachments">What the browser sent.</param>
    /// <param name="attached">Files placed, as entries.</param>
    /// <param name="reached">Who took the words.</param>
    /// <param name="token">Cancels the work.</param>
    /// <returns>When done.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a page whose send may start the conversation: one that does not exist yet has nowhere to hold a file, so the
    /// words go first (SPEC.md §9.5). Such a page then moves to the conversation, so what went wrong is said in the bell.
    /// </remarks>
    public static async Task SendAfterWordsAsync(
        BrokerClient broker,
        Parley.Client.Seating.SeatVoice voice,
        Parley.Web.Notifications.NotificationLog notices,
        string project,
        string conversation,
        IList<IFormFile> attachments,
        IList<string> attached,
        IReadOnlyList<AgentName> reached,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(reached);

        if (attachments.Count == 0)
        {
            return;
        }

        var placing = await PlaceAsync(broker, conversation, attachments, attached, token).ConfigureAwait(false);

        if (placing.Problem is not null || placing.ConversationNotStarted)
        {
            notices.Add(project, conversation, "Files",
                $"Your words went, and the files did not. {placing.Problem ?? "The conversation could not be found."}", false);

            return;
        }

        foreach (var agent in reached)
        {
            var said = await voice.RequestAsync(agent, Attaching.Message(null, attached), conversation, token).ConfigureAwait(false);

            notices.Add(project, conversation, agent.Value, said, said.StartsWith("Sent.", StringComparison.Ordinal));
        }
    }
}
