using System.Globalization;
using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// The roadmaps agents have proposed, and what a person does about each (SPEC.md §5.7, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here needs a seat.</b> Confirming, editing and declining are operator decisions about a list an agent put
/// forward: they send nobody a request and speak as nobody. What a person says to an agent about an item is a different
/// act, with a seat, and lives with the roadmap.
/// </para>
/// <para>
/// <b>Which of the three happened is recorded by the broker from what was sent.</b> Confirming as proposed and editing
/// first both end in the same call, with the content in it; whether the roadmap began as the agent's words or as the
/// person's edit of them is the broker's comparison and never a choice made on this screen.
/// </para>
/// <para>
/// <b>Declining sits behind its description and a deliberate act</b>, the surface rule this repository applies to anything
/// that discards: the description says what happens, the safe choice is first, and the proposer is told.
/// </para>
/// </remarks>
internal static class Proposing
{
    /// <summary>The door the review passes through.</summary>
    internal const string Title = "Proposed roadmaps";

    /// <summary>Said when no proposal is waiting.</summary>
    internal const string NoneWaiting = "No roadmap is awaiting your decision.";

    /// <summary>Leaves a proposal as it is: the first choice of the waiting list and of the decline door.</summary>
    internal const string Leave = "Leave it as it is";

    /// <summary>Declines it.</summary>
    internal const string Decline = "Decline it";

    /// <summary>Confirms it as a roadmap of its own.</summary>
    internal const string NewRoadmap = "Make it a new roadmap";

    /// <summary>Confirms a revision after being told it removes items whose work is still open.</summary>
    internal const string RemoveAndConfirm = "Remove them and confirm";

    /// <summary>Said when a person leaves a proposal alone.</summary>
    internal const string Left = "Left as it is. It is still awaiting your decision.";

    /// <summary>What the edit field asks.</summary>
    private const string EditAsks =
        "One line per item, in order: a number and a dot, then what is to be done. Add | for: role@machine and | needs: 1, 2 " +
        "where you know them, and, when it revises a roadmap, | item: n to keep that roadmap's item n. Move a line to move " +
        "the item. The first line is the title.";

    /// <summary>The door declining passes through.</summary>
    internal const string DeclineTitle = "Decline a proposed roadmap";

    /// <summary>Opens the list of proposals.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="queue">Runs reads and decisions, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <param name="conversation">
    /// The conversation the person is in, or null. A proposal that came from it is shown first, and when exactly one did
    /// the list is skipped: they are looking at the discussion it came out of, and asking them to choose between a list
    /// and the thing in front of them is a step nobody wanted.
    /// </param>
    /// <returns>When the read has been queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        string? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async t =>
        {
            var read = await BrokerDirectory.ReadAsync(
                async () => await client.ProposalsAsync(ProposalState.Waiting, headings: true, t).ConfigureAwait(false)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done($"The proposals could not be read: {Safe.Text(read.Problem!)}").ConfigureAwait(false);

                return;
            }

            var waiting = read.Value!;

            if (waiting.Count == 0)
            {
                await done(NoneWaiting).ConfigureAwait(false);

                return;
            }

            // From here: the proposals that came out of the conversation the person is in. One of them goes straight to its
            // view; several are listed first, ahead of the rest.
            if (conversation is { Length: > 0 })
            {
                var here = waiting.Where(p => string.Equals(p.SourceConversation, conversation, StringComparison.Ordinal)).ToList();

                if (here.Count == 1)
                {
                    await ReadWholeAsync(app, client, here[0], queue, done, t).ConfigureAwait(false);

                    return;
                }

                if (here.Count > 1)
                {
                    waiting = [.. here, .. waiting.Where(p => !here.Contains(p))];
                }
            }

            var rows = new List<string> { Leave };
            rows.AddRange(waiting.Select(p => Safe.Text(ProposalText.Row(p))));

            var choosing = Prompting.Choose(
                Title,
                $"{waiting.Count} roadmap{(waiting.Count == 1 ? " is" : "s are")} awaiting your decision.",
                rows);

            var open = Prompting.Open(app, choosing);

            choosing.Done += (_, chosen) =>
            {
                Prompting.Close(app, open);

                var at = chosen is null ? -1 : rows.IndexOf(chosen) - 1;

                if (at < 0)
                {
                    _ = done(Left);

                    return;
                }

                Open(app, client, waiting[at], queue, done);
            };
        });

        return Task.CompletedTask;
    }

    /// <summary>Reads one proposal whole from its heading, then shows it in its own view.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="heading">The proposal as a list read it: its heading, with no items.</param>
    /// <param name="queue">Runs the read and the decision.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <remarks>A list reads headings, since roadmap text has no bound; the proposal is read whole only when opened.</remarks>
    internal static void Open(
        IApplication app,
        BrokerClient client,
        RoadmapProposal heading,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(heading);

        queue(t => ReadWholeAsync(app, client, heading, queue, done, t));
    }

    /// <summary>Reads one proposal whole and shows it, from work already queued.</summary>
    private static async Task ReadWholeAsync(
        IApplication app,
        BrokerClient client,
        RoadmapProposal heading,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(
            async () => await client.ProposalAsync(heading.Id, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        if (!read.Reachable)
        {
            await done($"The proposal could not be read: {Safe.Text(read.Problem!)}").ConfigureAwait(false);

            return;
        }

        if (read.Value is not { State: ProposalState.Waiting } whole)
        {
            await done("That proposal is no longer awaiting your decision.").ConfigureAwait(false);

            return;
        }

        // What its hint names, so the view says so rather than that there is no such roadmap: a person told that would
        // decline a revision of one that exists. A read that fails opens nothing, as on the web, because the view could
        // only guess.
        RoadmapSummary? shown = null;

        if (whole.TargetTitle is { Length: > 0 })
        {
            var roadmaps = await BrokerDirectory.ReadAsync(
                async () => await client.RoadmapsAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            if (!roadmaps.Reachable)
            {
                await done($"The roadmaps could not be read: {Safe.Text(roadmaps.Problem!)}").ConfigureAwait(false);

                return;
            }

            shown = ProposalText.Matching(whole, roadmaps.Value!);
        }

        Review(app, client, whole, queue, done, shown);
    }

    /// <summary>Shows one proposal in its own view and does what the person chooses.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="proposal">What was proposed.</param>
    /// <param name="queue">Runs the decision.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <param name="shown">The live roadmap its hint names, when there is one: the one Confirm must still find.</param>
    /// <remarks>
    /// The view is <see cref="ProposalView"/>: the roadmap, who it names and the choices all on screen. Declining
    /// passes through its own confirmation, since it tells the agent and cannot be taken back.
    /// </remarks>
    internal static void Review(
        IApplication app,
        BrokerClient client,
        RoadmapProposal proposal,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        RoadmapSummary? shown)
    {
        var view = new ProposalView(proposal, shown?.Title);
        var open = Prompting.Open(app, view);

        view.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            switch (chosen)
            {
                case ProposalChoice.Confirm:
                    queue(t => ConfirmAsync(app, client, proposal, proposal.Content, shown, queue, done, t));
                    break;

                case ProposalChoice.Edit:
                    Edit(app, client, proposal, shown, queue, done);
                    break;

                case ProposalChoice.Decline:
                    ConfirmDecline(app, client, proposal, queue, done);
                    break;

                default:
                    _ = done(Left);
                    break;
            }
        };
    }

    /// <summary>Opens the proposal as text a person can change.</summary>
    private static void Edit(
        IApplication app,
        BrokerClient client,
        RoadmapProposal proposal,
        RoadmapSummary? shown,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var form = new FormDialog(
            Title,
            [
                new FormField(
                    EditAsks,
                    RoadmapTextCodec.Write(proposal.Content),
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

            queue(t => ConfirmAsync(app, client, proposal, content, shown, queue, done, t));
        };
    }

    /// <summary>Asks, behind its description, whether to decline.</summary>
    private static void ConfirmDecline(
        IApplication app,
        BrokerClient client,
        RoadmapProposal proposal,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        // What declining does first and the agent's title last, quoted short: the dialog cannot scroll and cuts its tail,
        // so a long title put first hid what the choice does.
        var description =
            "Declining keeps no roadmap. The agent that proposed it is told, in its own inbox, that it was declined; "
            + "nothing it was working on changes, and the conversation it came from is untouched."
            + $"\n\nProposed by {Safe.Text(proposal.Proposer.Value)}: "
            + Safe.Text(AgentWords.Shortened(proposal.Content.Title));

        var choosing = Prompting.Choose(DeclineTitle, description, [Leave, Decline]);
        var open = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            if (chosen != Decline)
            {
                _ = done(Left);

                return;
            }

            queue(async t =>
            {
                var outcome = await BrokerDirectory.ReadAsync(
                    async () => await client.DeclineProposalAsync(proposal.Id, t).ConfigureAwait(false)).ConfigureAwait(false);

                await done(outcome.Reachable
                    ? outcome.Value!.Done
                        ? $"Declined '{Safe.Text(proposal.Content.Title)}'. {Safe.Text(proposal.Proposer.Role)} was told."
                        : Safe.Text(outcome.Value.Problem ?? "It could not be declined.")
                    : Safe.Text(outcome.Problem!)).ConfigureAwait(false);
            });
        };
    }

    /// <summary>Confirms the content, first asking whether it revises a roadmap the proposal hints at.</summary>
    private static async Task ConfirmAsync(
        IApplication app,
        BrokerClient client,
        RoadmapProposal proposal,
        RoadmapContent content,
        RoadmapSummary? shown,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(
            async () => await client.RoadmapsAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        // Unread is not unmatched: a person who might have been offered a revision is not handed a new roadmap instead.
        if (!read.Reachable)
        {
            await done($"The roadmaps could not be read, so nothing was confirmed: {Safe.Text(read.Problem!)}").ConfigureAwait(false);

            return;
        }

        var match = ProposalText.Matching(proposal, read.Value!);

        // **The view said the person would choose, about one roadmap.** Renamed elsewhere while it was open, that roadmap
        // is not answered with a new one nobody asked for, nor with another that happens to share its old title.
        if (shown is not null && match?.Id != shown.Id)
        {
            await done(
                (match is null
                    ? $"The roadmap '{Safe.Text(shown.Title)}' is no longer called that, so nothing was confirmed. "
                    : $"Another roadmap is now called '{Safe.Text(match.Title)}', so nothing was confirmed. ")
                + "Open the proposal again to choose.").ConfigureAwait(false);

            return;
        }

        if (match is null)
        {
            await SendAsync(client, proposal, content, null, done, cancellationToken).ConfigureAwait(false);

            return;
        }

        // The title is a hint and a person decides what it means: shown with the match, never acted on.
        // Quoted short, so a long title cannot push what a revision does off a dialog that cannot scroll, and named by its
        // number, as the roadmaps pane names it: titles are not unique and two may share every character quoted.
        var number = "#" + match.Number.ToString(CultureInfo.InvariantCulture);
        var matched = Safe.Text(AgentWords.Shortened(match.Title));
        var revise = $"Revise roadmap {number}";

        // What a revision also decides: the other proposals waiting to revise it go too, and are shown nowhere after.
        var waiting = await WaitingProposals.ListAsync(client, cancellationToken).ConfigureAwait(false);
        var superseding = ProposalText.Superseding(proposal, match, waiting);

        var choosing = Prompting.Choose(
            Title,
            $"This proposal's title matches roadmap {number}, '{matched}', which has "
            + match.LiveItems.ToString(CultureInfo.InvariantCulture) + (match.LiveItems == 1 ? " item" : " items")
            + ". " + (superseding.Length > 0 ? superseding + " " : string.Empty) + ProposalText.RevisionReplaces,
            [NewRoadmap, revise]);

        var open = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            // Escape is leaving it as it is, as in every other dialog, never a choice of either.
            if (chosen is null)
            {
                _ = done(Left);

                return;
            }

            if (chosen != revise)
            {
                queue(t => SendAsync(client, proposal, content, null, done, t));

                return;
            }

            queue(t => ReviseAsync(app, client, proposal, content, match.Id, match.Revision, queue, done, t));
        };
    }

    /// <summary>Confirms the proposal as a revision, first describing any open work the revision removes.</summary>
    /// <remarks>
    /// A proposal's items carry no item numbers, so a revision kept as proposed replaces every item of the roadmap. An
    /// item whose request is still open loses its place and its agent is not told, which is described here as it is when
    /// the person edits the roadmap (<see cref="Revising"/>), and saved only on a second, deliberate choice.
    /// </remarks>
    private static async Task ReviseAsync(
        IApplication app,
        BrokerClient client,
        RoadmapProposal proposal,
        RoadmapContent content,
        long roadmapId,
        int revision,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(
            async () => await client.RoadmapAsync(roadmapId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        // A roadmap that could not be read cannot be said to hold no open work, so nothing is confirmed.
        if (!read.Reachable || read.Value is not { } roadmap)
        {
            await done($"The roadmap could not be read, so nothing was confirmed: {Safe.Text(read.Problem ?? "there is no such roadmap.")}").ConfigureAwait(false);

            return;
        }

        var opened = Removal.StillOpen(roadmap, content);

        if (Removal.Describe(opened) is not { } warning)
        {
            await SendAsync(client, proposal, content, (roadmapId, revision), done, cancellationToken).ConfigureAwait(false);

            return;
        }

        // The items' words were proposed by an agent, so they are made safe to draw like any other text of theirs.
        var choosing = Prompting.Choose(Title, Safe.Text(warning), [Leave, RemoveAndConfirm]);
        var asking = Prompting.Open(app, choosing);
        var shown = Removal.Numbers(opened);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, asking);

            if (chosen == RemoveAndConfirm)
            {
                queue(async t =>
                {
                    // Going ahead covers what was described, read again now (Revising.StillCoveredAsync says why).
                    if (await Revising.StillCoveredAsync(client, roadmapId, content, shown, "confirmed", done, t).ConfigureAwait(false))
                    {
                        await SendAsync(client, proposal, content, (roadmapId, revision), done, t).ConfigureAwait(false);
                    }
                });
            }
            else
            {
                _ = done(Left);
            }
        };
    }

    private static async Task SendAsync(
        BrokerClient client,
        RoadmapProposal proposal,
        RoadmapContent content,
        (long Id, int Revision)? target,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.ConfirmProposalAsync(
                proposal.Id, content, target?.Id, target?.Revision, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(Safe.Text(outcome.Problem!)).ConfigureAwait(false);

            return;
        }

        var change = outcome.Value!;

        await done(change.Done
            ? $"Confirmed '{Safe.Text(content.Title)}'. {Safe.Text(proposal.Proposer.Role)} was told."
            : Safe.Text(change.Problem ?? "It could not be confirmed.")).ConfigureAwait(false);
    }
}
