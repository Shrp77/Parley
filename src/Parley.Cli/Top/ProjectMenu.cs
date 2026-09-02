using Grpc.Core;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// What can be done inside one project.
/// </summary>
/// <remarks>
/// <para>
/// Everything below this point is scoped to the project chosen above it. SPEC.md §4.1 gives each
/// its own store and endpoint, so two deployments may each hold an <c>orders</c> — and a screen
/// that showed both would present colliding names with nothing saying which was which. There is
/// no view here that is not the view of one project.
/// </para>
/// <para>
/// <b>The menu changes with the seat, and the seat is asked for rather than remembered.</b> What
/// is offered comes from asking the book whether a seat is held right now. A flag set when
/// somebody joined would go on saying they were seated after the broker restarted underneath
/// them — precisely the cookie-outliving-a-seat failure the console shipped, which is available
/// here to anybody who caches the answer.
/// </para>
/// </remarks>
internal static class ProjectMenu
{
    /// <summary>Shows the menu until the person goes back to the project list.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="seats">Where a seat is held, if one is.</param>
    /// <param name="project">The project everything below is scoped to.</param>
    /// <param name="me">Who is seated in this session, if anybody is.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>Who is seated when the person goes back, so the session keeps it.</returns>
    /// <remarks>
    /// <b>The seat is passed in and handed back, because it belongs to the session and not to one
    /// visit to this menu.</b> An earlier version held it in a local, so somebody who took a seat,
    /// went back to the project list and came into the same project again was told they were not
    /// seated -- while the registry still had them present and the book still held their
    /// attachment. The screen and the deployment disagreed about a person, which is the failure
    /// this menu's own remarks are about, arrived at from the one direction they did not cover.
    /// </remarks>
    internal static async Task<Seat?> ShowAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        SeatBook seats,
        ProjectSettings project,
        Seat? me,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Who they are, and whether they are seated *here*, are two different questions and
            // the second one is the broker's. `me` is deliberately not overwritten from the hold:
            // it was, and walking into a project you happen not to be seated in forgot who you
            // were -- so coming back out and into the one you *are* seated in showed a join,
            // while the book still held the seat and the registry still had you present. The same
            // screen-and-deployment disagreement this menu was already fixed for, reached through
            // the door of a second project. Only Leave clears a person.
            var hold = Refresh(seats, me, project, DateTimeOffset.UtcNow);

            console.WriteLine();

            var choices = Choices(seated: hold is not null);

            var title = hold is null
                ? $"[bold]{Safe.Text(project.Name.Value)}[/] [dim](not seated)[/]"
                : $"[bold]{Safe.Text(project.Name.Value)}[/] "
                  + $"[dim](seated as {Safe.Text(hold.Name.Value)})[/]";

            var choice = console.Prompt(
                new SelectionPrompt<string>().Title(title).AddChoices(choices));

            try
            {
                switch (choice)
                {
                    case Agents:
                        await AgentsView.ShowAsync(console, directory, project, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case Conversations:
                        await ConversationList.ShowAsync(console, directory, project, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case Logs:
                        await LogTail.ShowAsync(console, project, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case WebConsole:
                        // The console's own address, not this project's broker. They are different
                        // processes on different ports, and opening the broker's would show a browser
                        // a gRPC endpoint that answers nothing it can render.
                        Launch.Open(console, Parley.Web.ConsoleHost.LoopbackAddress);
                        break;

                    case Join:
                        var taken = await Seating
                            .JoinAsync(console, directory, seats, project, cancellationToken)
                            .ConfigureAwait(false);

                        // Kept on a refusal rather than cleared. A join that the broker turned
                        // down -- an unreadable registry, a name the registry holds for a real
                        // agent -- has not unseated anybody, and somebody already seated in
                        // another project would have lost that seat's name to a failed attempt
                        // to take a second one.
                        me = taken?.Seat ?? me;
                        break;

                    case Inbox:
                        await Waiting
                            .ShowAsync(console, Voice(directory, project, hold!), hold!, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case Ask:
                        await Composer
                            .AskAsync(
                                console, directory, Voice(directory, project, hold!), project,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case Tell:
                        await Composer
                            .NotifyAsync(
                                console, directory, Voice(directory, project, hold!), project, null,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case Leave:
                        Seating.Leave(console, seats, hold!.Seat);
                        me = null;
                        break;

                    default:
                        return me;
                }
            }
            catch (RpcException failure)
            {
                // **The broker going away underneath a seated person, which the spec names as an
                // edge case and which nothing else here contained.** Every operator read on this
                // surface goes through BrokerDirectory.ReadAsync, which turns an unreachable
                // broker into a sentence. The agent operations behind a seat do not: they throw,
                // and in a terminal that is the process ending -- taking every other project's
                // view with it -- rather than a page returning 500.
                //
                // Said and not acted on, which is the part that took a second look. An earlier
                // version released the seat here, reasoning that a broker that cannot answer has
                // no seat to answer about. But SeatHold retries a broker that is merely down, on
                // purpose and for good reasons written there, and it is the book -- not this
                // screen's last exception -- that knows whether a seat survived. Dropping it here
                // would turn a two-second restart into a departure the person has to undo, and
                // would race the reattach already putting it right. When the seat really is over,
                // the hold says so and Refresh reports it on the next loop.
                console.MarkupLine(
                    $"[red]This project's broker did not answer.[/] "
                    + $"[dim]{Safe.Text(failure.Status.Detail)}[/]");
            }
        }

        return me;
    }

    /// <summary>Keeps the seat of whoever is here, and releases the seats of whoever is not.</summary>
    /// <param name="seats">The book.</param>
    /// <param name="me">The name last seated, if any.</param>
    /// <param name="project">The project this menu is for.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The seat still held, or <see langword="null"/> when there is none.</returns>
    /// <remarks>
    /// <para>
    /// <b>Touch first, then sweep, and the order is the whole of it.</b> Swept first, a person who
    /// spent longer than <see cref="SeatBook.IdleAfter"/> on a screen that is not this one comes
    /// back to find their seat gone — and the screen they were on is the inbox, reading a question
    /// and writing an answer, which is the slowest and most valuable thing this surface is for.
    /// Being unseated for thinking about the question is the exact opposite of what an idle
    /// timeout is for.
    /// </para>
    /// <para>
    /// Sweeping at all is what stops a terminal left open overnight holding somebody present in
    /// the registry, which is the failure <c>IdleAfter</c> exists to prevent — reached by a
    /// different kind of window than the tab it was written for. Both halves are needed and only
    /// in this order.
    /// </para>
    /// <para>
    /// The seat is then <i>asked for</i> rather than assumed: whether the name still holds one is
    /// the book's answer, including after the sweep has just taken it.
    /// </para>
    /// </remarks>
    internal static SeatHold? Refresh(
        SeatBook seats, Seat? me, ProjectSettings project, DateTimeOffset now)
    {
        if (me is not null)
        {
            // Being at this menu is being here. Nothing else on this surface reports that a
            // person is still present, so without this the sweep below releases the seat of
            // somebody sitting in front of it.
            seats.Touch(me, now);
        }

        seats.Sweep(now);

        return me is null ? null : seats.Held(project.Name.Value, me);
    }

    /// <summary>What the menu offers, in the order it offers it.</summary>
    /// <param name="seated">Whether a seat is held in this project right now.</param>
    /// <returns>The entries.</returns>
    /// <remarks>
    /// One list, built here and used both to draw the menu and to drive it from a test. The
    /// alternative is a test that counts keystrokes against a menu it cannot see, which is a test
    /// that silently starts navigating somewhere else the next time an entry is added — and this
    /// menu has already grown twice while it was being written.
    /// </remarks>
    internal static IReadOnlyList<string> Choices(bool seated)
    {
        var choices = new List<string> { Agents, Conversations, Logs };

        if (!seated)
        {
            choices.Add(Join);
        }
        else
        {
            choices.Add(Inbox);
            choices.Add(Ask);
            choices.Add(Tell);
            choices.Add(Leave);
        }

        choices.Add(WebConsole);
        choices.Add(Back);

        return choices;
    }

    /// <summary>The person's own agent operations, for the project they are seated in.</summary>
    /// <param name="directory">The clients.</param>
    /// <param name="project">The project.</param>
    /// <param name="hold">The seat being spoken from.</param>
    /// <returns>The voice.</returns>
    /// <remarks>
    /// Built from <see cref="BrokerDirectory.AgentsFor"/>, which is the Agents service and not the
    /// Operators one. contracts/operations.md makes that separation the guarantee rather than the
    /// naming: a screen that reads the deployment must not reach the agent client, and a screen
    /// that speaks as the person must not reach the operator client.
    /// </remarks>
    private static SeatVoice Voice(
        BrokerDirectory directory, ProjectSettings project, SeatHold hold) =>
        new(directory.AgentsFor(project), hold.Seat);

    internal const string Agents = "Agents";
    internal const string Conversations = "Conversations";
    private const string Join = "Take a seat";
    private const string Inbox = "What is waiting for you";
    private const string Ask = "Ask somebody a question";
    private const string Tell = "Tell somebody something";
    private const string Leave = "Leave your seat";
    private const string Logs = "Follow the broker log";
    private const string WebConsole = "Open the web console in a browser";
    internal const string Back = "Back to projects";
}
