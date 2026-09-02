using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// A person taking a seat in a project, and leaving it.
/// </summary>
/// <remarks>
/// <para>
/// A seat is the one thing this surface has that no operator operation grants. §10.2 lets an
/// operator retire, purge and force-conclude anybody in the deployment, so a console has never
/// been short of power — what it could not do is <em>speak as</em> somebody, and a transcript
/// recording words an agent never said is a different kind of wrong from an operator visibly
/// instructing it.
/// </para>
/// <para>
/// So that is the one thing guarded, and the guard is <see cref="SeatBook"/>'s rather than a copy
/// of it. It refuses a name the registry holds for anything whose adapter is not a seat, and
/// refuses outright when the registry could not be read — a guard that skipped the check on an
/// unreachable broker would pass hardest exactly when the deployment is least well known.
/// </para>
/// <para>
/// The adapter presented is <see cref="Seat.Adapter"/>, unchanged and shared with the web console.
/// Research R2 settled that: a marker of its own would mean somebody seated in a browser could not
/// seat themselves here under their own name, which is a restriction SPEC.md §14 does not have and
/// which nothing but a naming choice would have invented.
/// </para>
/// </remarks>
internal static class Seating
{
    /// <summary>Seats a person under a name they choose.</summary>
    /// <param name="console">Where to draw, and where the name is typed.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="seats">The book that holds seats.</param>
    /// <param name="project">The project to sit in.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The seat taken, or <see langword="null"/> when it was refused.</returns>
    /// <remarks>
    /// The registry is read first and the read is <b>not</b> contained into a shrug. Everywhere
    /// else on this surface an unreachable broker greys out one project and leaves the rest
    /// usable; here it has to stop the join, because the check that makes seating safe is a
    /// question asked of that broker.
    /// </remarks>
    internal static async Task<SeatHold?> JoinAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        SeatBook seats,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        var person = console.Prompt(
            new TextPrompt<string>("Join as:")
                .Validate(typed => Seat.TryRead(typed, out _, out var why)
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]{Safe.Text(why ?? "not a name")}[/]")));

        if (!Seat.TryRead(person, out var seat, out _))
        {
            return null;
        }

        var registry = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .RegistryAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        var outcome = await seats
            .TakeAsync(project, seat, registry.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Seated)
        {
            console.MarkupLine($"[red]{Safe.Text(outcome.Problem ?? "Not seated.")}[/]");

            return null;
        }

        console.MarkupLine(
            $"[green]Seated as {Safe.Text(seat.Name.Value)}.[/] "
            + "[dim]You are present in this project until you leave.[/]");

        return outcome.Hold;
    }

    /// <summary>Releases a person's seat in every project at once.</summary>
    /// <param name="console">Where to say so.</param>
    /// <param name="seats">The book holding it.</param>
    /// <param name="seat">Whose seat.</param>
    /// <remarks>
    /// Every project, not the one on screen. A person leaves a surface rather than a view, and
    /// releasing only the project they happened to be looking at would leave them present in the
    /// others with nothing anywhere saying so.
    /// </remarks>
    internal static void Leave(IAnsiConsole console, SeatBook seats, Seat seat)
    {
        var released = seats.Leave(seat);

        console.MarkupLine(
            released == 0
                ? "[yellow]You were not seated anywhere.[/]"
                : $"[green]Left.[/] [dim]{released} seat(s) released.[/]");
    }
}
