using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Removing a project from inside the surface: forgetting it, or destroying it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one destructive thing this surface did first, and why it is here.</b> Every operator
/// operation that destroys something was kept off the terminal, on the grounds that a purge behind
/// a keystroke is a different risk from one behind a page that describes it first. Destroying a
/// project was kept off on the same grounds until the owner pointed out that a surface which can
/// create a project and cannot destroy one is half a surface — and that what the rule actually
/// asks for is a description first and a deliberate act, neither of which a keystroke is.
/// </para>
/// <para>
/// So it is not a keystroke. The project is described — store and broker — and forgetting
/// it, which deletes nothing, is offered beside destroying it. Destroying takes the project's own
/// name typed back, and nothing else is accepted: not a "y", not Enter. It is reached through the
/// command it runs, which is the only writer of the manifest and the only thing that deletes a
/// store.
/// </para>
/// <para>
/// <b>That command is no longer reachable from a shell</b>, as of 2026-09-25, so this door is the
/// only one there is. The description and the typed name were always the better arrangement — a
/// command line can offer neither — and the removal is what makes them unavoidable rather than
/// merely available.
/// </para>
/// <para>
/// <b>The word stays in this file.</b> <c>SurfaceTests</c> holds it here, so a second path to the
/// flag would fail the build rather than be noticed — which is the whole of what makes "behind a
/// description" a property of the surface rather than a habit.
/// </para>
/// </remarks>
internal static class Removing
{
    /// <summary>What is offered, in the order it is offered.</summary>
    /// <remarks>
    /// Keeping first, so that Enter on a dialog somebody reached by accident does nothing. Built
    /// here and read by the test that drives it, for the reason every other list here is.
    /// </remarks>
    internal static readonly IReadOnlyList<string> Choices = [Keep, Forget, Destroy];

    /// <summary>Keeping it, which is what Enter does.</summary>
    internal const string Keep = "Keep it";

    /// <summary>Forgetting it, which deletes nothing.</summary>
    internal const string Forget = "Forget it (stops its broker; deletes nothing)";

    /// <summary>Destroying it, which does not come back.</summary>
    internal const string Destroy = "Destroy it (deletes its store)";

    /// <summary>What the first dialog is called.</summary>
    internal const string Title = "Remove this project from this machine";

    /// <summary>What is said when nothing was done.</summary>
    internal const string Kept = "Kept.";

    /// <summary>What is said when the name typed back was not the project's.</summary>
    internal const string WrongName = "That is not this project's name. Nothing changed.";

    /// <summary>What the second dialog asks for, after saying what destroying does.</summary>
    /// <remarks>
    /// A constant because a test reads it, and a test that restated the sentence would pass while
    /// the screen said something else. <c>Destroying</c> next door has always had one.
    /// </remarks>
    internal const string TypeItBack = "Type the project's name to go ahead:";

    /// <summary>What forgetting does, said before it is done.</summary>
    internal const string Forgetting =
        "Forgetting deletes nothing. The store and catalogue stay where they are, and the broker "
        + "serving the manifest stops this project's within a few seconds.";

    /// <summary>What destroying does, said before it is done.</summary>
    internal const string Destroying =
        "Destroying stops this project's broker, deletes its store and catalogue, and forgets it. "
        + "Every conversation in it goes with the store. Repositories that were agents in it keep "
        + "their launch configuration and will find no broker.";

    /// <summary>The project, as the first dialog describes it.</summary>
    /// <param name="project">The project.</param>
    /// <returns>What it is and what would go with it.</returns>
    internal static string Describe(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);

        // **Every one of these is a manifest's, and a manifest is a file this person may not have
        // written.** `ProjectsRegion` has always neutralised the same values on its way to a row;
        // this dialog did not, and it is the more dangerous of the two — it is the description
        // somebody reads before typing a project's name to destroy it, and an escape sequence in
        // an endpoint clears it.
        // **The catalogue line went with 019, and its absence is the honest answer.** A project
        // recorded a catalogue of its own and destroying the project deleted it, so this named it.
        // There is one catalogue per machine now, shared by every project and untouched by this —
        // so naming it here would say a thing was about to go that is not, in the one dialog whose
        // whole job is to say what destroying costs.
        return $"{Safe.Text(project.Name.Value)}{Environment.NewLine}"
            + $"  broker     {Safe.Text(project.Endpoint)}{Environment.NewLine}"
            + $"  store      {Safe.Text(project.Store)}";
    }

    /// <summary>Why a typed name would not be accepted, or null when it would.</summary>
    /// <param name="project">The project being removed.</param>
    /// <param name="typed">What is in the field.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// Ordinal and exact. Trimming would accept a name with a space on the end, which is a
    /// different string from the one somebody was asked to reproduce, and case-insensitivity would
    /// accept a project that does not exist on a machine whose names are case-sensitive.
    /// </remarks>
    internal static string? TypedNameProblem(ProjectSettings project, string typed)
    {
        ArgumentNullException.ThrowIfNull(project);

        return string.Equals(typed, project.Name.Value, StringComparison.Ordinal) ? null : WrongName;
    }

    /// <summary>
    /// Runs the whole removal, dialog by dialog, and reports what happened.
    /// </summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="manifestPath">The manifest the project is named in.</param>
    /// <param name="project">The project.</param>
    /// <param name="queue">
    /// Runs the command, so the caller can wait for it. A dialog's answer arrives on an event and
    /// the work it starts is asynchronous; started with a discarded task, nothing can tell when
    /// the manifest has actually changed, and a test that asked would read the screen before the
    /// command had run.
    /// </param>
    /// <param name="done">
    /// Told what to say, and whether the project is no longer named on this machine.
    /// </param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        string manifestPath,
        ProjectSettings project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, bool, Task> done)
    {
        ArgumentNullException.ThrowIfNull(queue);

        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(done);

        var choosing = Prompting.Choose(Title, Describe(project), Choices);
        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            switch (chosen)
            {
                case Forget:
                    queue(_ => Apply(manifestPath, project, destroy: false, done));
                    break;

                case Destroy:
                    Confirm(app, manifestPath, project, queue, done);
                    break;

                default:
                    _ = done(Kept, false);
                    break;
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>Asks for the project's name, and destroys it only if that is what arrives.</summary>
    private static void Confirm(
        IApplication app,
        string manifestPath,
        ProjectSettings project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, bool, Task> done)
    {
        var asking = Prompting.Text(
            Destroy,
            Destroying + Environment.NewLine + TypeItBack,
            string.Empty,
            typed => TypedNameProblem(project, typed));

        var token = Prompting.Open(app, asking);

        asking.Done += (_, typed) =>
        {
            Prompting.Close(app, token);

            if (typed is null || TypedNameProblem(project, typed) is not null)
            {
                _ = done(Kept, false);

                return;
            }

            queue(_ => Apply(manifestPath, project, destroy: true, done));
        };
    }

    /// <summary>Runs the command, and says in one line what came of it.</summary>
    /// <remarks>
    /// <b>One sentence on success, the command's own words on failure</b> -- the owner's call of 2026-09-27,
    /// made about creating a project and applied to removing one for the same reason. This reprinted what the
    /// command writes for a shell: where the store remains, the flag that would delete it, and which
    /// repositories still name the project. The list the person returns to already says the project is gone.
    /// </remarks>
    private static async Task Apply(
        string manifestPath,
        ProjectSettings project,
        bool destroy,
        Func<string, bool, Task> done)
    {
        var said = new StringWriter();
        var complained = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["remove", project.Name.Value],
            manifestPath,
            new ProjectFields(null),
            said,
            complained,
            destroy,
            CancellationToken.None).ConfigureAwait(false);

        // The command's exit code is the answer. Destroying that removed the entry and could not
        // delete a file reports a failure, and the project is nonetheless gone from the list --
        // so a failure says why in the command's own words, whatever became of the entry.
        var gone = code == 0 || !StillNamed(manifestPath, project.Name.Value);

        if (code != 0)
        {
            var why = complained.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd());

            await done(Reports.Sentences(why), gone).ConfigureAwait(false);

            return;
        }

        await done(
            destroy ? $"Destroyed '{project.Name.Value}'." : $"Forgot '{project.Name.Value}'. Nothing was deleted.",
            gone).ConfigureAwait(false);
    }

    private static bool StillNamed(string manifestPath, string name) =>
        Configuration.ProjectManifest.TryRead(manifestPath, out var projects, out _)
        && projects.Any(p => string.Equals(p.Name.Value, name, StringComparison.Ordinal));
}
