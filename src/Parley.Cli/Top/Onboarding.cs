using Terminal.Gui.App;

namespace Parley.Cli.Top;

/// <summary>
/// Enrolling a folder of repositories, from inside the surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>It calls <see cref="EnrollCommand"/> rather than reimplementing enrolment</b>, so what is
/// written is what the command writes. The three steps are the command's own — scan, plan, apply —
/// and they are separate here for the reason they are separate there: applying writes into
/// repositories that are not this one, and a person should have read the plan first.
/// </para>
/// <para>
/// The one thing this adds is the confirmation before applying, because a full-screen surface
/// makes it easy to arrive at a step without having read what it does.
/// </para>
/// </remarks>
internal static class Onboarding
{
    /// <summary>What the dialog is called.</summary>
    internal const string Title = "Enrolment";

    /// <summary>Finding repositories under a directory and writing them into a manifest.</summary>
    private const string Scan = "Scan a directory for repositories";

    /// <summary>Saying what enrolling the ticked ones would change, without changing anything.</summary>
    internal const string Plan = "Say what enrolling them would change";

    /// <summary>Doing it.</summary>
    internal const string Apply = "Configure the ticked ones as agents";

    /// <summary>What is offered, in the order it is offered.</summary>
    internal static readonly IReadOnlyList<string> Choices = [Plan, Scan, Apply];

    /// <summary>What is said after a successful apply.</summary>
    private const string Applied =
        "Applied. Each repository still has to be opened once for its adapter to attach and "
        + "register it — nothing here can do that for them.";

    /// <summary>Opens the enrolment dialog.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="queue">Runs the command, so the caller can wait for it.</param>
    /// <param name="done">Told what the command said.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(done);

        var choosing = Prompting.Choose(
            Title,
            "Scanning writes a manifest of what it found. The plan says what enrolling the ticked "
            + "ones would change. Applying writes configuration into every ticked repository.",
            Choices);

        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, step) =>
        {
            Prompting.Close(app, token);

            if (step is null)
            {
                _ = done(string.Empty);

                return;
            }

            Ask(app, step, queue, done);
        };

        return Task.CompletedTask;
    }

    /// <summary>Asks for the paths a step needs, then runs it.</summary>
    private static void Ask(
        IApplication app,
        string step,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var fields = step == Scan
            ?
            [
                new FormField("Directory to scan:", string.Empty, Participation.RequiredProblem),
                new FormField("Enrolment manifest to write:", string.Empty, Participation.RequiredProblem),
                new FormField("Tick every repository found? (yes / no)", "no", YesOrNo),
            ]
            : new List<FormField>
            {
                new("Enrolment manifest path:", string.Empty, Participation.RequiredProblem),
            };

        var form = new FormDialog(step, fields);
        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null)
            {
                _ = done(string.Empty);

                return;
            }

            if (step == Apply)
            {
                Confirm(app, values[0], queue, done);

                return;
            }

            var arguments = step == Scan
                ? (IReadOnlyList<string>)["scan", values[0], values[1]]
                : ["plan", values[0]];

            var all = step == Scan ? Yes(values[2]) : (bool?)null;

            queue(_ => RunAsync(arguments, all, step, done));
        };

        static string? YesOrNo(string typed) =>
            Yes(typed) || string.Equals(typed.Trim(), "no", StringComparison.OrdinalIgnoreCase)
                ? null
                : "yes or no";
    }

    /// <summary>Asks before writing into repositories that are not this one.</summary>
    private static void Confirm(
        IApplication app,
        string manifest,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var asking = Prompting.Choose(
            Apply,
            $"Applying {manifest} writes configuration into every ticked repository. Run the plan "
            + "first if you have not.",
            ["Not now", Apply]);

        var token = Prompting.Open(app, asking);

        asking.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Apply)
            {
                _ = done(string.Empty);

                return;
            }

            queue(_ => RunAsync(["apply", manifest], null, Apply, done));
        };
    }

    /// <summary>Runs the command and reports what it said, in its own words.</summary>
    private static async Task RunAsync(
        IReadOnlyList<string> arguments,
        bool? all,
        string step,
        Func<string, Task> done)
    {
        var said = new StringWriter();
        var complained = new StringWriter();

        // The node catalogue is resolved the way every other command resolves it, so a machine
        // configured by environment or by project gets the same answer here as from a shell.
        var nodeConfig = ProjectResolution.NodeConfig(
            null, Environment.GetEnvironmentVariable, null);

        var code = await EnrollCommand
            .RunAsync(arguments, all, nodeConfig, said, complained, CancellationToken.None)
            .ConfigureAwait(false);

        var lines = (said.ToString() + complained)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .ToList();

        if (code == 0 && step == Apply)
        {
            lines.Add(Applied);
        }

        await done(string.Join(Environment.NewLine, lines)).ConfigureAwait(false);
    }

    private static bool Yes(string typed) =>
        string.Equals(typed.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
}
