using Parley.Client;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// What can be done inside one project.
/// </summary>
/// <remarks>
/// Everything below this point is scoped to the project chosen above it. SPEC.md §4.1 gives each
/// its own store and endpoint, so two deployments may each hold an <c>orders</c> — and a screen
/// that showed both would present colliding names with nothing saying which was which. There is
/// no view here that is not the view of one project.
/// </remarks>
internal static class ProjectMenu
{
    /// <summary>Shows the menu until the person goes back to the project list.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="project">The project everything below is scoped to.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When the person has gone back.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            console.WriteLine();

            var choice = console.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]{ProjectList.Escape(project.Name.Value)}[/]")
                    .AddChoices(Agents, Conversations, Back));

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

                default:
                    return;
            }
        }
    }

    private const string Agents = "Agents";
    private const string Conversations = "Conversations";
    private const string Back = "Back to projects";
}
