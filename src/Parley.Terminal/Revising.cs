using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Changing a roadmap that is already confirmed: take an item out, reword one, or add one (SPEC.md §5.7, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>An agent's proposal may carry work the person does not want</b>, so confirming it is not the last word. The same
/// text the proposal was edited in opens here, and what is saved goes through <c>edit_roadmap</c> with the revision that
/// was read.
/// </para>
/// <para>
/// <b>Each line carries <c>| item: N</c> for an item the roadmap already has</b>, which is what lets a reworded or moved
/// line still mean the same item. Delete the line to remove the item; write a line without it to add one. A removed
/// item's conversation stays, and nothing in flight is destroyed.
/// </para>
/// <para>
/// <b>Removing an item whose work is still open is described first</b> (<see cref="Removal"/>), and saved only on a
/// second, deliberate choice: the request stays open and its agent is not told, which deleting a line does not say.
/// </para>
/// <para>
/// <b>A stale revision is said and applied to nothing.</b> The person is offered the roadmap read again; what they typed
/// is not carried onto it, because it was written against items that may have moved.
/// </para>
/// </remarks>
internal static class Revising
{
    /// <summary>The title of the edit dialog and its menu entry.</summary>
    internal const string Title = "Edit this roadmap";

    /// <summary>Leaves the roadmap as it is.</summary>
    internal const string Leave = "Leave it as it is";

    /// <summary>Reads the roadmap again and opens it for editing.</summary>
    private const string Reload = "Read it again and edit that";

    /// <summary>Saves an edit after being told it removes items whose work is still open.</summary>
    internal const string RemoveAnyway = "Remove them and save";

    /// <summary>Said when the person leaves without saving.</summary>
    internal const string Left = "Left as it is. Nothing was changed.";

    /// <summary>Said when the revision read is no longer the roadmap's.</summary>
    internal const string Stale = "The roadmap changed while you were editing it, so nothing was applied.";

    /// <summary>What the edit field asks, including what happens to an item already started.</summary>
    internal const string EditAsks =
        "One line per item, in order. Delete a line to remove that item, change it to reword the item, or write a new line to add one. " +
        "Keep '| item: N' on a line you keep. An item already started keeps its conversation, " +
        "and a removed item's conversation stays; nothing in flight is closed.";

    /// <summary>Opens the roadmap as text a person can change.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="roadmap">The roadmap as read, with the revision the save will be checked against.</param>
    /// <param name="queue">Runs the save.</param>
    /// <param name="done">Told what to say when it is over.</param>
    internal static void Edit(
        IApplication app,
        BrokerClient client,
        Roadmap roadmap,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roadmap);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var form = new FormDialog(
            Title,
            [
                new FormField(
                    EditAsks,
                    RoadmapTextCodec.Write(RoadmapDraft.From(roadmap).ToContent()),
                    Problem: text => RoadmapTextCodec.Parse(text).Problem,
                    Long: true),
            ]);

        var open = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, open);

            if (values is null)
            {
                _ = done(Left);

                return;
            }

            var (content, problem) = RoadmapTextCodec.Parse(values[0]);

            if (content is null || problem is not null)
            {
                _ = done(Safe.Text(problem ?? "That is not a roadmap."));

                return;
            }

            var opened = Removal.StillOpen(roadmap, content);

            if (Removal.Describe(opened) is not { } warning)
            {
                queue(t => SaveAsync(app, client, roadmap, content, queue, done, t));

                return;
            }

            // The item's words were proposed by an agent, so they are made safe to draw like any other text of theirs.
            var choosing = Prompting.Choose(Title, Safe.Text(warning), [Leave, RemoveAnyway]);
            var asking = Prompting.Open(app, choosing);
            var shown = Removal.Numbers(opened);

            choosing.Done += (_, chosen) =>
            {
                Prompting.Close(app, asking);

                if (chosen == RemoveAnyway)
                {
                    queue(async t =>
                    {
                        // **Going ahead covers what was described, read again now.** The question waits on a person, and
                        // work started on another item meanwhile was removed without a word (the web checks the same).
                        var fresh = await BrokerDirectory.ReadAsync(
                            async () => await client.RoadmapAsync(roadmap.Id, t).ConfigureAwait(false)).ConfigureAwait(false);

                        if (!fresh.Reachable || fresh.Value is not { } now)
                        {
                            await done($"The roadmap could not be read, so nothing was saved: {Safe.Text(fresh.Problem ?? "there is no such roadmap.")}")
                                .ConfigureAwait(false);

                            return;
                        }

                        if (!Removal.Covers(Removal.StillOpen(now, content), shown))
                        {
                            await done(Proposing.OpenedSince).ConfigureAwait(false);

                            return;
                        }

                        await SaveAsync(app, client, roadmap, content, queue, done, t).ConfigureAwait(false);
                    });
                }
                else
                {
                    _ = done(Left);
                }
            };
        };
    }

    private static async Task SaveAsync(
        IApplication app,
        BrokerClient client,
        Roadmap roadmap,
        RoadmapContent content,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.EditRoadmapAsync(roadmap.Id, roadmap.Revision, content, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(Safe.Text(outcome.Problem!)).ConfigureAwait(false);

            return;
        }

        var change = outcome.Value!;

        if (change.Done)
        {
            await done($"Saved '{Safe.Text(content.Title)}'.").ConfigureAwait(false);

            return;
        }

        // A refusal is the broker's words, unless the roadmap moved under the person: that is said once, in one sentence,
        // and the roadmap is offered again as it now stands.
        var again = await BrokerDirectory.ReadAsync(
            async () => await client.RoadmapAsync(roadmap.Id, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        if (again.Value is not { } current || current.Revision == roadmap.Revision)
        {
            await done(Safe.Text(change.Problem ?? "It could not be saved.")).ConfigureAwait(false);

            return;
        }

        await done(Stale).ConfigureAwait(false);

        var choosing = Prompting.Choose(Title, Stale + " Read it again?", [Leave, Reload]);
        var open = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            if (chosen == Reload)
            {
                Edit(app, client, current, queue, done);
            }
        };
    }
}
