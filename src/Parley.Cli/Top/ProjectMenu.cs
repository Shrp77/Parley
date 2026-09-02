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
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When the person has gone back.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        SeatBook seats,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        Seat? me = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            // A seat nothing has touched is released, exactly as the console releases one behind
            // a tab left open. Nothing else in this process sweeps, so without this a terminal
            // left open overnight would hold a person present in the registry until somebody
            // closed the window -- which is the failure SeatBook.IdleAfter exists to prevent,
            // reached by a different kind of window.
            seats.Sweep(DateTimeOffset.UtcNow);

            // Asked, not remembered. `me` is only the name last typed; whether it still holds a
            // seat is the book's answer and nobody else's -- including after the sweep above has
            // just taken it away.
            var hold = me is null ? null : seats.Held(project.Name.Value, me);

            if (hold is null)
            {
                me = null;
            }
            else
            {
                // Drawing this menu is a person being here. Without it the sweep above would
                // release the seat of somebody sitting in front of it, since nothing else on
                // this surface reports that they are still there.
                seats.Touch(hold.Seat, DateTimeOffset.UtcNow);
            }

            console.WriteLine();

            var choices = Choices(seated: hold is not null);

            var title = hold is null
                ? $"[bold]{Safe.Text(project.Name.Value)}[/] [dim](not seated)[/]"
                : $"[bold]{Safe.Text(project.Name.Value)}[/] "
                  + $"[dim](seated as {Safe.Text(hold.Name.Value)})[/]";

            var choice = console.Prompt(
                new SelectionPrompt<string>().Title(title).AddChoices(choices));

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
                    me = taken?.Seat;
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
                    return;
            }
        }
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
