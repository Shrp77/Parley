using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Onboarding repositories as agents, through the code the command runs.
/// </summary>
/// <remarks>
/// <para>
/// FR-015's second half, and the same design as <see cref="Naming"/>: it calls
/// <see cref="EnrollCommand"/> rather than reimplementing enrolment, so what is written is what
/// the command writes and the validation is the command's own.
/// </para>
/// <para>
/// <b>Scan and plan are offered; apply is offered with what it would do already on the screen.</b>
/// Enrolment writes into repositories that are not this one, which is the most consequential
/// thing this surface can do to a machine — so the step that does it is reached by reading the
/// plan first rather than by a keystroke next to the scan.
/// </para>
/// <para>
/// The scan's own picker is not surfaced here. It draws itself with <c>ConsolePicker</c>, which
/// owns the terminal while it runs, and two full-screen drawers sharing one terminal is a fight
/// neither wins. <c>--all</c> and <c>--none</c> exist precisely because the picker cannot run
/// everywhere, and they are what this offers instead.
/// </para>
/// </remarks>
internal static class Onboarding
{
    /// <summary>Runs one enrolment step and shows what it said.</summary>
    /// <param name="console">Where to draw, and where the paths are typed.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>When the step has finished.</returns>
    internal static async Task RunAsync(IAnsiConsole console, CancellationToken cancellationToken)
    {
        var step = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Enrolment")
                .AddChoices(Scan, Plan, Apply, Back));

        if (step == Back)
        {
            return;
        }

        var manifest = console.Prompt(new TextPrompt<string>("Enrolment manifest path:"));

        bool? all = null;

        var arguments = step switch
        {
            Scan => Scanned(console, manifest, out all),
            Plan => (IReadOnlyList<string>)["plan", manifest],
            _ => ["apply", manifest],
        };

        if (step == Apply && !Confirmed(console, manifest))
        {
            return;
        }

        var said = new StringWriter();
        var complained = new StringWriter();

        // The node catalogue is resolved the way every other command resolves it, so a machine
        // configured by environment or by project gets the same answer here as from a shell.
        var nodeConfig = ProjectResolution.NodeConfig(
            null, Environment.GetEnvironmentVariable, null);

        var code = await EnrollCommand
            .RunAsync(arguments, all, nodeConfig, said, complained, cancellationToken)
            .ConfigureAwait(false);

        foreach (var line in said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[dim]{ProjectList.Escape(line.TrimEnd())}[/]");
        }

        foreach (var line in complained.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[red]{ProjectList.Escape(line.TrimEnd())}[/]");
        }

        if (code == 0 && step == Apply)
        {
            console.MarkupLine(
                "[green]Applied.[/] [dim]Each repository still has to be opened once for its "
                + "adapter to attach and register it — nothing here can do that for them.[/]");
        }
    }

    /// <summary>The arguments for a scan, with the flag that avoids a second full-screen picker.</summary>
    /// <param name="console">Where the directory is typed.</param>
    /// <param name="manifest">Where to write what was found.</param>
    /// <param name="all">Whether every repository found is ticked.</param>
    /// <returns>The command's arguments.</returns>
    private static IReadOnlyList<string> Scanned(
        IAnsiConsole console, string manifest, out bool? all)
    {
        var directory = console.Prompt(new TextPrompt<string>("Directory to scan:"));

        all = console.Confirm("Tick every repository found?", defaultValue: false);

        return ["scan", directory, manifest];
    }

    /// <summary>Asks before writing into repositories that are not this one.</summary>
    /// <param name="console">Where to ask.</param>
    /// <param name="manifest">What would be applied.</param>
    /// <returns><see langword="true"/> when the person said so.</returns>
    private static bool Confirmed(IAnsiConsole console, string manifest)
    {
        console.MarkupLine(
            $"[yellow]Applying {ProjectList.Escape(manifest)} writes configuration into every "
            + "ticked repository.[/] Run the plan first if you have not.");

        return console.Confirm("Apply it?", defaultValue: false);
    }

    private const string Scan = "Scan a directory for repositories";
    private const string Plan = "Say what enrolling them would change";
    private const string Apply = "Configure the ticked ones as agents";
    private const string Back = "Back";
}
