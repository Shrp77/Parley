using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Removing a project from inside the surface: forgetting it, or destroying it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one destructive thing this surface does, and why it is here.</b> Every operator
/// operation that destroys something is kept off the terminal, on the grounds that a purge behind
/// a keystroke is a different risk from one behind a page that describes it first. Destroying a
/// project was kept off on the same grounds until the owner pointed out that a surface which can
/// create a project and cannot destroy one is half a surface — and that what the rule actually
/// asks for is a description first and a deliberate act, neither of which a keystroke is.
/// </para>
/// <para>
/// So it is not a keystroke. The project is described — store, broker, catalogue — and forgetting
/// it, which deletes nothing, is offered beside destroying it. Destroying takes the project's own
/// name typed back, and nothing else is accepted: not a "y", not Enter. That is the same act
/// <c>parley project remove --destroy</c> is, reached through the command it runs, which is the
/// only writer of the manifest and the only thing that deletes a store.
/// </para>
/// </remarks>
internal static class Removing
{
    /// <summary>Offers to forget or destroy a project, and does whichever was chosen.</summary>
    /// <param name="console">Where to draw, and where the choice is made.</param>
    /// <param name="manifestPath">The manifest the project is named in.</param>
    /// <param name="project">The project.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="true"/> when the project is no longer named on this machine.</returns>
    internal static async Task<bool> RunAsync(
        IAnsiConsole console,
        string manifestPath,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        var name = project.Name.Value;

        console.WriteLine();
        console.MarkupLine($"[bold]{Safe.Text(name)}[/]");
        console.MarkupLine($"  broker     [dim]{Safe.Text(project.Endpoint)}[/]");
        console.MarkupLine($"  store      [dim]{Safe.Text(project.Store)}[/]");
        console.MarkupLine(
            $"  catalogue  [dim]{Safe.Text(project.NodeConfig ?? "none: nothing is started for it")}[/]");
        console.WriteLine();

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("What should happen to it?")
                .AddChoices(Choices));

        if (choice == Keep)
        {
            return false;
        }

        if (choice == Forget)
        {
            console.MarkupLine(
                "[dim]Forgetting deletes nothing. The store and catalogue stay where they are, and "
                + "the broker serving the manifest stops this project's within a few seconds.[/]");

            return await RemoveAsync(console, manifestPath, name, destroy: false, cancellationToken)
                .ConfigureAwait(false);
        }

        console.MarkupLine(
            "[yellow]Destroying stops this project's broker, deletes its store and catalogue, and "
            + "forgets it.[/] Every conversation in it goes with the store. Repositories that were "
            + "agents in it keep their launch configuration and will find no broker.");

        var typed = console.Prompt(
            new TextPrompt<string>("Type the project's name to destroy it, or nothing to keep it:")
                .AllowEmpty()
                .Validate(answer => answer.Length == 0 || string.Equals(answer, name, StringComparison.Ordinal)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]That is not this project's name. Nothing was done.[/]")));

        if (typed.Length == 0)
        {
            console.MarkupLine("[dim]Kept.[/]");

            return false;
        }

        return await RemoveAsync(console, manifestPath, name, destroy: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs the command, and shows what it said in its own words.</summary>
    private static async Task<bool> RemoveAsync(
        IAnsiConsole console,
        string manifestPath,
        string name,
        bool destroy,
        CancellationToken cancellationToken)
    {
        var said = new StringWriter();
        var complained = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["remove", name],
            manifestPath,
            new ProjectFields(null, null, null, null),
            said,
            complained,
            cancellationToken,
            destroy).ConfigureAwait(false);

        foreach (var line in said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[dim]{Safe.Text(line.TrimEnd())}[/]");
        }

        foreach (var line in complained.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[red]{Safe.Text(line.TrimEnd())}[/]");
        }

        // The command's exit code is the answer. Destroying that removed the entry and could not
        // delete a file reports a failure, and the project is nonetheless gone from the list --
        // which is what the command said, line by line, above.
        return code == 0 || !ProjectStillNamed(manifestPath, name);
    }

    private static bool ProjectStillNamed(string manifestPath, string name) =>
        Configuration.ProjectManifest.TryRead(manifestPath, out var projects, out _)
        && projects.Any(p => string.Equals(p.Name.Value, name, StringComparison.Ordinal));

    /// <summary>What is offered, in the order it is offered.</summary>
    /// <remarks>
    /// Keeping first, so that Enter on a menu somebody reached by accident does nothing. Built
    /// here and read by the test that drives it, for the reason every other list here is.
    /// </remarks>
    internal static readonly IReadOnlyList<string> Choices = [Keep, Forget, Destroy];

    internal const string Keep = "Keep it";
    internal const string Forget = "Forget it (stops its broker; deletes nothing)";
    internal const string Destroy = "Destroy it (deletes its store and catalogue)";
}
