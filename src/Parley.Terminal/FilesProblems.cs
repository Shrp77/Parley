using System.Globalization;
using Parley.Client;
using Parley.Client.Files;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// What sweeps of the file location found and could not leave as it was, and dismissing each (SPEC.md §9.5, §10.2
/// <c>list_file_problems</c>, <c>dismiss_file_problem</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A list the person chooses from, on the one choice dialog every door here uses</b>, as <see cref="Files"/> is.
/// Each row is the broker's words: what happened, the conversation and the path. Choosing one shows the rest — the
/// hash placed, the hash found and where in the quarantine directory the file went — and offers dismissing it, with
/// going back the answer under the cursor.
/// </para>
/// <para>
/// <b>Every path is somebody else's text.</b> An entry another program put in the location was never checked, so each
/// is neutralised one character for one and indented, keeping only this surface's own words at column zero — the rule
/// the files list and the transcript keep.
/// </para>
/// <para>
/// <b>Dismissing is a choice, not a typed name.</b> It deletes nothing on disk and can be seen again among the
/// dismissed ones, so the door is the description and a second answer, the weight removal has rather than pruning's.
/// </para>
/// </remarks>
internal static class FilesProblems
{
    /// <summary>What the list is called, and the action that opens it.</summary>
    internal const string Title = "File problems";

    /// <summary>What the choice that leads here is called on the files list.</summary>
    internal const string Choice = "File problems…";

    /// <summary>What one problem's dialog is called.</summary>
    internal const string OneTitle = "File problem";

    /// <summary>The answer that dismisses a problem.</summary>
    internal const string DismissIt = "Dismiss it (deletes nothing on disk)";

    /// <summary>The choice that lists the dismissed problems too.</summary>
    internal const string WithDismissed = "Show the dismissed ones too";

    /// <summary>What is said when nothing is waiting and nothing was asked for beyond that.</summary>
    internal const string NoneWaiting = "No file problems are waiting.";

    private const string Close = "Close";

    private const string Back = "Back";

    /// <summary>Reads the problems and opens the list.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for the project.</param>
    /// <param name="includeDismissed">Whether the dismissed problems are listed too.</param>
    /// <param name="queue">Runs work, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when something is over.</param>
    /// <returns>When the read is queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        bool includeDismissed,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async cancellationToken =>
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await client.ListFileProblemsAsync(includeDismissed, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done(read.Problem!).ConfigureAwait(false);
                return;
            }

            Show(app, client, includeDismissed, queue, done, read.Value!);
        });

        return Task.CompletedTask;
    }

    /// <summary>What the list says above its rows: how many are waiting, and where the quarantine directory is.</summary>
    /// <param name="report">What the broker said.</param>
    /// <returns>The description, safe to draw.</returns>
    /// <remarks>
    /// The machine and the path are the broker's text, so they go on a line of their own, indented under this surface's
    /// sentence, rather than inside it.
    /// </remarks>
    internal static string Describe(FileProblemsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            report.Waiting == 0
                ? NoneWaiting
                : string.Create(CultureInfo.InvariantCulture, $"{report.Waiting} waiting. A sweep found these and could not leave them as they were."),
            "Quarantined files are kept here, and Parley never deletes anything there:",
            "  " + Safe.Text(report.Quarantine.Machine) + ": " + Safe.Text(report.Quarantine.Path),
            FileProblemSaid.DismissingDeletesNothing,
        };

        if (report.Problems.Count > 0)
        {
            lines.Add(Parley.Client.Rendering.LocalTime.Note());
        }

        return string.Join('\n', lines);
    }

    /// <summary>One problem's row: its number, what happened, the conversation and the path.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The row, safe to draw.</returns>
    /// <remarks>
    /// The number keeps two rows apart that would otherwise read the same, which is how a choice finds its problem
    /// again. Indented, so only this surface's words sit at column zero.
    /// </remarks>
    internal static string Row(FileProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        return "  " + Safe.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"#{problem.Id}  {FileProblemSaid.Kind(problem.Kind)}  {FileProblemSaid.Conversation(problem)}  {problem.Path}  " +
            $"{Parley.Client.Rendering.LocalTime.Write(problem.RaisedUtc)}{(problem.DismissedUtc is null ? string.Empty : "  (dismissed)")}"));
    }

    /// <summary>Everything said about one problem, for its own dialog.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The description, safe to draw.</returns>
    internal static string Whole(FileProblem problem) =>
        string.Create(CultureInfo.InvariantCulture, $"Problem #{problem.Id}, raised {Parley.Client.Rendering.LocalTime.Write(problem.RaisedUtc)}:\n") +
        string.Join('\n', FileProblemSaid.Lines(problem).Select(line => "  " + Safe.Text(line))) +
        (problem.DismissedUtc is null ? "\n" + FileProblemSaid.DismissingDeletesNothing : string.Empty);

    private static void Show(
        IApplication app,
        BrokerClient client,
        bool includeDismissed,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        FileProblemsReport report)
    {
        var rows = report.Problems.Select(Row).ToList();
        var choices = new List<string> { Close };

        if (!includeDismissed)
        {
            choices.Add(WithDismissed);
        }

        choices.AddRange(rows);

        var choosing = Prompting.Choose(Title, Describe(report), choices);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen is null || chosen == Close)
            {
                return;
            }

            if (chosen == WithDismissed)
            {
                _ = OpenAsync(app, client, includeDismissed: true, queue, done);
                return;
            }

            var index = rows.IndexOf(chosen);

            if (index < 0)
            {
                return;
            }

            var problem = report.Problems[index];

            var acting = Prompting.Choose(
                OneTitle,
                Whole(problem),
                problem.DismissedUtc is null ? [Back, DismissIt] : [Back]);

            var actingToken = Prompting.Open(app, acting);

            acting.Done += (_, action) =>
            {
                Prompting.Close(app, actingToken);

                if (action == DismissIt)
                {
                    queue(t => DismissAsync(client, problem, t, done));
                }
            };
        };
    }

    private static async Task DismissAsync(
        BrokerClient client,
        FileProblem problem,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var read = await BrokerDirectory.ReadAsync(async () =>
            await client.DismissFileProblemAsync(problem.Id, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        await done(!read.Reachable
            ? read.Problem!
            : read.Value
                ? string.Create(CultureInfo.InvariantCulture, $"Dismissed file problem #{problem.Id}. Nothing on disk was changed.")
                : string.Create(CultureInfo.InvariantCulture, $"File problem #{problem.Id} was not waiting any more. Nothing was changed."))
            .ConfigureAwait(false);
    }
}
