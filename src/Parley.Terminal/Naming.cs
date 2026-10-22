using System.Globalization;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Naming a project from inside the surface, through the code the command runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only way to create a project</b>: the command line does not dispatch to
/// <see cref="ProjectCommand"/> for it.
/// </para>
/// <para>
/// <b>It calls <see cref="ProjectCommand"/> rather than writing the manifest itself.</b>
/// The manifest has exactly one writer; a second path that assembled a <c>ProjectSettings</c> and wrote
/// it would be a second writer with its own idea of the rules, and it would sidestep the lock that stops
/// two concurrent writers discarding one another's work.
/// </para>
/// <para>
/// The form asks for a name and an optional description. The store is derived from the name and the port
/// is claimed under a hold, so nothing else is asked. Every answer is checked as it is typed, which a
/// refusal at the end could not do.
/// </para>
/// </remarks>
internal static class Naming
{
    /// <summary>What the form creating a project is called.</summary>
    internal const string CreateTitle = "Create a project";

    /// <summary>What is said when the command refused.</summary>
    internal const string NothingWritten = "Nothing was written.";

    /// <summary>What is said when the command succeeded.</summary>
    /// <param name="name">The project's name.</param>
    /// <returns>One short sentence.</returns>
    internal static string Created(string name) => $"Created '{name}'.";

    /// <summary>Opens the form that creates a project that does not exist yet.</summary>
    /// <param name="app">The application the form opens in.</param>
    /// <param name="manifestPath">The manifest to write into.</param>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="entry">The installed entry, if any, for what to say after.</param>
    /// <param name="queue">Runs the command, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when the form has been dealt with.</param>
    /// <returns>When the form is open.</returns>
    /// <remarks>
    /// SPEC.md §4.1: each project gets its own store and its own transport endpoint, both derived or
    /// claimed by the command rather than asked for.
    /// </remarks>
    internal static Task OpenCreateAsync(
        IApplication app,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry? entry,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(projects);

        // Nothing is proposed, because nothing is asked for beyond a name; the port is claimed under a hold.
        return OpenAsync(app, CreateTitle, manifestPath, projects, entry, queue, done);
    }

    /// <summary>Why a typed name would be refused, or null.</summary>
    /// <remarks>
    /// The same rule <see cref="ProjectCommand"/> applies, said here so the refusal arrives at
    /// the field rather than after four more answers. Kept in one place there and quoted from
    /// here: <see cref="ProjectName.TryCreate"/> is the rule, and this only names it.
    /// </remarks>
    internal static string? NameProblem(string typed, IReadOnlyList<ProjectSettings> projects)
    {
        if (!ProjectName.TryCreate(typed, out _))
        {
            return "lowercase letters, digits and hyphens, starting with a letter, at most "
                   + ProjectName.MaxLength.ToString(CultureInfo.InvariantCulture) + " characters";
        }

        return projects.Any(p => string.Equals(p.Name.Value, typed, StringComparison.Ordinal))
            ? $"'{typed}' is already named on this machine"
            : null;
    }

    /// <summary>What is said when a name had to be corrected as it was typed.</summary>
    internal const string NameWasCorrected = Parley.Core.NameCorrection.WasCorrected;

    /// <summary>
    /// The name rule applied to what is being typed, rather than reported after the fact.
    /// </summary>
    /// <param name="typed">What is in the field.</param>
    /// <returns>The same name, made acceptable.</returns>
    /// <remarks>
    /// <para>
    /// <b>Reporting a refusal is not enough.</b> A field that will not take a capital in the first
    /// place cannot fail the way a sentence under it can, whatever the layout does.
    /// </para>
    /// <para>
    /// <b>It corrects and never completes.</b> Case is folded and disallowed characters are
    /// dropped, because both are unambiguous — there is exactly one lowercase form of a letter.
    /// What it does not do is invent: an empty name, or one starting with a digit, is left alone
    /// for <see cref="NameProblem"/> to refuse, because guessing what somebody meant is how a
    /// field ends up holding a name they did not choose.
    /// </para>
    /// <para>
    /// <b>A space becomes a hyphen</b>: <c>Order Ledger</c> becomes <c>order-ledger</c>, a name that can
    /// be read back. The rule is <see cref="Parley.Core.NameCorrection"/>'s, which is also what an enrolled
    /// agent's name and a seat are held to: one grammar, one correction.
    /// </para>
    /// </remarks>
    internal static string NameCorrection(string? typed) =>
        Parley.Core.NameCorrection.Correct(typed);

    /// <summary>Builds the form, checks every field as it is typed, and writes on confirmation.</summary>
    private static Task OpenAsync(
        IApplication app,
        string title,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry? entry,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(done);

        var form = new FormDialog(
            title,
            [
                new FormField(
                    "Project name — a label for this machine only, never sent anywhere:",
                    string.Empty,
                    typed => NameProblem(typed, projects),
                    Correct: NameCorrection,
                    Corrected: NameWasCorrected),
                // No path or port field: the store is derived from the name and the port is claimed.
                new FormField(
                    "Description (optional):",
                    string.Empty),
            ]);

        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null)
            {
                _ = done(string.Empty);

                return;
            }

            queue(_ => WriteAsync(manifestPath, values, entry, done));
        };

        return Task.CompletedTask;
    }

    /// <summary>Runs the command and says, in one line, what came of it.</summary>
    /// <remarks>
    /// <para>
    /// <b>A success is one sentence, and the facts are on the projects list</b>: the one fact worth having
    /// is where the project answers, which the list shows on its row.
    /// </para>
    /// <para>
    /// A refusal still comes back in the command's own words. A surface that summarised it to "invalid"
    /// would take away the one thing that says which rule was broken.
    /// </para>
    /// <para>
    /// What happens next is said only when it is not the ordinary thing. With an entry serving the
    /// manifest the project simply starts, and the list shows it answering; with no entry, or one
    /// narrowed to a single project, nothing will, and that is worth one more short sentence.
    /// </para>
    /// </remarks>
    private static async Task WriteAsync(
        string manifestPath,
        IReadOnlyList<string> values,
        InstalledEntry? entry,
        Func<string, Task> done)
    {
        var said = new StringWriter();
        var complained = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["add", values[0]],
            manifestPath,
            new ProjectFields(values[1].Length == 0 ? null : values[1]),
            said,
            complained,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        if (code != 0)
        {
            var refused = complained.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd())
                .Append(NothingWritten)
                .ToList();

            await done(Reports.Sentences(refused)).ConfigureAwait(false);

            return;
        }

        var outcome = entry switch
        {
            null => $"{Created(values[0])} Nothing starts it yet: run `parley autostart install`.",

            // What narrows an entry is `--project`; leaving it out makes the entry serve everything.
            { ServesManifest: false } =>
                $"{Created(values[0])} The autostart entry serves one project only; re-run "
                + "`parley autostart install` without --project to start this one.",

            _ => Created(values[0]),
        };

        await done(outcome).ConfigureAwait(false);
    }

}
