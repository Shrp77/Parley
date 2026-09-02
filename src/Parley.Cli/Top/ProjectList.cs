using Parley.Client;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// The projects this machine knows, and whether each one's broker is answering.
/// </summary>
/// <remarks>
/// <para>
/// The first screen, and the one that makes SC-001 possible: a person goes from launching this to
/// reading a conversation without typing an identifier, so the list has to be chosen from rather
/// than typed into.
/// </para>
/// <para>
/// Every project is asked at once and each is contained. In sequence the cost of the screen is the
/// sum of the timeouts rather than the longest of them — a machine with a dozen projects and two
/// of them down would take twenty seconds to say so, which is a screen nobody waits for and
/// therefore a fact nobody reads. The console learned this on the same list; this is the same
/// shape, deliberately.
/// </para>
/// </remarks>
internal static class ProjectList
{
    /// <summary>Draws the list and returns what the person chose.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>The chosen project, or <see langword="null"/> when the person left.</returns>
    internal static async Task<ProjectSettings?> ChooseAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        CancellationToken cancellationToken)
    {
        var projects = directory.Projects;

        if (directory.ManifestProblem is { } broken)
        {
            // Said rather than swallowed, and the list is empty rather than stale. Somebody who
            // has just broken their manifest needs to be told; a surface that went on showing the
            // last good list would hide the edit that caused it.
            console.MarkupLine($"[red]The manifest could not be read.[/] {Escape(broken)}");

            return null;
        }

        if (projects.Count == 0)
        {
            console.MarkupLine(
                "[yellow]This machine names no projects, and no broker was offered.[/]");
            console.MarkupLine(
                "Name one with [bold]parley project add <name> --db <path> --listen <url>[/].");

            return null;
        }

        var health = await HealthAsync(directory, projects, cancellationToken).ConfigureAwait(false);

        console.Write(Table(projects, health));

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Which project?")
                .AddChoices(Choices(projects)));

        if (choice == Name)
        {
            if (directory.ManifestPath is not { } manifest)
            {
                // Nothing to write into. A directory built from an explicit list is somebody
                // else's list, and inventing a path to add to would put a project somewhere they
                // were not looking.
                console.MarkupLine("[yellow]This surface was given its projects, so there is no "
                    + "manifest here to add one to.[/]");

                return await ChooseAsync(console, directory, cancellationToken).ConfigureAwait(false);
            }

            await Naming.AddAsync(console, manifest, cancellationToken)
                .ConfigureAwait(false);

            return await ChooseAsync(console, directory, cancellationToken).ConfigureAwait(false);
        }

        if (choice == Onboard)
        {
            await Onboarding.RunAsync(console, cancellationToken).ConfigureAwait(false);

            return await ChooseAsync(console, directory, cancellationToken).ConfigureAwait(false);
        }

        return choice == Leave ? null : directory.Find(choice);
    }

    /// <summary>Asks every project's broker what it speaks, all at once.</summary>
    /// <param name="directory">The clients.</param>
    /// <param name="projects">The projects to ask.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>What each said, by project name.</returns>
    /// <remarks>
    /// Through <see cref="BrokerDirectory.ReadAsync{T}"/>, which is where FR-017's containment
    /// lives: a broker that is not answering makes its own project unreadable and leaves every
    /// other one alone. Calling the client directly here would reintroduce that coupling by
    /// omission, which is the failure the method was extracted to prevent.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, ProjectHealth>> HealthAsync(
        BrokerDirectory directory,
        IReadOnlyList<ProjectSettings> projects,
        CancellationToken cancellationToken)
    {
        var asked = projects.Select(async project =>
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .ImplementsAsync(cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            return (project.Name.Value, Health: new ProjectHealth(read.Value, read.Problem));
        });

        var answers = await Task.WhenAll(asked).ConfigureAwait(false);
        var health = new Dictionary<string, ProjectHealth>(StringComparer.Ordinal);

        foreach (var (name, said) in answers)
        {
            // Assigned rather than added, so a manifest naming one project twice is a duplicate
            // row and not an exception that takes the screen away.
            health[name] = said;
        }

        return health;
    }

    /// <summary>The projects as a table, with what each broker said.</summary>
    /// <param name="projects">The projects.</param>
    /// <param name="health">What each broker said.</param>
    /// <returns>The table.</returns>
    /// <remarks>
    /// The state column is a word before it is a colour. SC-007 forbids anything here conveying
    /// meaning by colour alone, so the colour repeats what the text already said and a reader who
    /// cannot see it loses nothing.
    /// </remarks>
    private static Table Table(
        IReadOnlyList<ProjectSettings> projects,
        IReadOnlyDictionary<string, ProjectHealth> health)
    {
        var table = new Table().Border(TableBorder.Rounded);

        table.AddColumn("project");
        table.AddColumn("broker");
        table.AddColumn("endpoint");

        foreach (var project in projects)
        {
            var said = health.TryGetValue(project.Name.Value, out var found)
                ? found
                : new ProjectHealth(null, "not asked");

            table.AddRow(
                Escape(project.Name.Value),
                Describe(said),
                $"[dim]{Escape(project.Endpoint)}[/]");
        }

        return table;
    }

    /// <summary>One project's broker, in words.</summary>
    /// <param name="health">What it said.</param>
    /// <returns>Markup naming the state and, when they differ, both versions.</returns>
    /// <remarks>
    /// Three states and not two. A broker that is down and a broker speaking a version this build
    /// was not made against both leave a screen looking empty, and the remedies are opposite: one
    /// is a process to start, the other a build to reconcile. Collapsing them costs somebody an
    /// afternoon, so both numbers are named — "incompatible" without them cannot be acted on.
    /// </remarks>
    internal static string Describe(ProjectHealth health) => health switch
    {
        { Reachable: false } => "[red]not answering[/]",
        { Agrees: true } => "[green]answering[/]",
        _ => $"[yellow]answering, speaks {Escape(health.Speaks ?? "nothing")}, "
             + $"this build speaks {Escape(Parley.Core.ProtocolVersion.Current)}[/]",
    };

    /// <summary>Keeps a name that contains markup characters from being read as markup.</summary>
    /// <param name="text">Whatever came from a manifest or a broker.</param>
    /// <returns>The same text, inert.</returns>
    /// <remarks>
    /// Project names and endpoints are operator-supplied and agent-supplied text. A square
    /// bracket in one would otherwise be parsed as a style tag, which is the terminal's version
    /// of the injection the console refuses by never emitting raw output.
    /// </remarks>
    internal static string Escape(string text) => Markup.Escape(text ?? string.Empty);

    /// <summary>What the first screen offers, in the order it offers it.</summary>
    /// <param name="projects">The projects this machine names.</param>
    /// <returns>The entries.</returns>
    /// <remarks>
    /// Built here and used both to draw the list and to drive it from a test, for the reason
    /// <c>ProjectMenu.Choices</c> gives: a test counting keystrokes against a list it cannot see
    /// is a test that quietly navigates somewhere else the next time an entry is added.
    /// </remarks>
    internal static IReadOnlyList<string> Choices(IReadOnlyList<ProjectSettings> projects) =>
        [.. projects.Select(p => p.Name.Value), Name, Onboard, Leave];

    /// <summary>Naming a new project, which is FR-015 and not a navigation.</summary>
    private const string Name = "Name a new project";

    /// <summary>Enrolling repositories as agents.</summary>
    private const string Onboard = "Onboard repositories as agents";

    /// <summary>The choice that ends the surface.</summary>
    internal const string Leave = "Leave";
}
