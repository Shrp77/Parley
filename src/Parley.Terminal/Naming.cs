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
/// <b>This is the only way to create a project now</b>, and for most of its life it was not: the
/// command line could do it too, and this existed so that nobody had to leave a full-screen
/// application to run a command whose arguments were on the screen they left. The owner removed
/// the other half on 2026-09-25 — "if we're doing everything in the terminal app, then we DO
/// everything in the terminal app" — so what was a convenience is the capability.
/// </para>
/// <para>
/// Nothing below changed with it, which is the point of the arrangement: the command still exists
/// and this still calls it. What went is the arm in <c>parley</c> that dispatched to it.
/// </para>
/// <para>
/// <b>It calls <see cref="ProjectCommand"/> rather than writing the manifest itself</b>, which is
/// the whole of the design. The manifest has exactly one writer, and this is that same program; a
/// second path that assembled a <c>ProjectSettings</c> and wrote it would be a second writer with
/// its own idea of the rules — and it would sidestep the lock that stops two concurrent writers
/// discarding one another's work, which was a real defect and not a hypothetical one.
/// </para>
/// <para>
/// <b>Two forms, because the two questions are different.</b> An earlier version had one, with
/// five blank prompts, and the person filling it in answered every one only to be told at the end
/// that the name had to be lowercase — and had to go and read a scheduled task to find the paths
/// it wanted. Naming the deployment this machine already runs is a form whose every answer is
/// already written down in the autostart entry, so it is pre-filled from there. Creating a new
/// project needs values that do <i>not</i> exist yet — a fresh store, a port nothing else holds —
/// so those are proposed rather than asked for blank. Both check every answer as it is typed,
/// which is what a refusal at the end could never do.
/// </para>
/// </remarks>
internal static class Naming
{
    /// <summary>What the form creating a project is called.</summary>
    internal const string CreateTitle = "Create a project";

    /// <summary>What is said when the command refused.</summary>
    internal const string NothingWritten = "Nothing was written.";

    /// <summary>What is said when it did not.</summary>
    /// <param name="name">The project's name.</param>
    /// <returns>One short sentence.</returns>
    internal static string Created(string name) => $"Created '{name}'.";

    /// <summary>Opens the form that creates a project that does not exist yet.</summary>
    /// <param name="app">The application the form opens in.</param>
    /// <param name="manifestPath">The manifest to write into.</param>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="entry">The installed entry, if any, for where stores live and what to say after.</param>
    /// <param name="queue">Runs the command, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when the form has been dealt with.</param>
    /// <returns>When the form is open.</returns>
    /// <remarks>
    /// The endpoint proposed is the default port when nothing on this machine holds it — an MCP
    /// server told nothing about a broker looks there, which is what lets a repository's launch
    /// configuration say only who it is — and a free loopback port otherwise, because a second
    /// deployment cannot share the first one's. SPEC.md §4.1: each project gets its own store and
    /// its own transport endpoint.
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

        // **Nothing is proposed any more**, because nothing is asked for beyond a name. The endpoints
        // already spoken for used to be gathered here so a free one could be offered; the port is
        // claimed under a hold now, which is a stronger answer to the same question than a proposal a
        // person could overwrite.
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
    /// <b>Because reporting it was not enough.</b> The owner typed <c>TimeTracker</c> and met no
    /// resistance: the only thing enforcing lowercase was <see cref="NameProblem"/>'s sentence,
    /// on a clipped row under a label that had itself overflowed. A field that will not take a
    /// capital in the first place cannot fail that way, whatever the layout does.
    /// </para>
    /// <para>
    /// <b>It corrects and never completes.</b> Case is folded and disallowed characters are
    /// dropped, because both are unambiguous — there is exactly one lowercase form of a letter.
    /// What it does not do is invent: an empty name, or one starting with a digit, is left alone
    /// for <see cref="NameProblem"/> to refuse, because guessing what somebody meant is how a
    /// field ends up holding a name they did not choose.
    /// </para>
    /// <para>
    /// <b>A space became nothing and now becomes a hyphen</b>, which is the whole of what changed
    /// here: <c>Order Ledger</c> was corrected to <c>orderledger</c>. That is a name nobody can
    /// read back, and the owner chose the hyphen for the conversation field on exactly those
    /// grounds — so the field beside it does not go on swallowing spaces. The rule is
    /// <see cref="Parley.Core.NameCorrection"/>'s, which is also what an enrolled agent's name and
    /// a seat are held to: one grammar, one correction.
    /// </para>
    /// </remarks>
    internal static string NameCorrection(string? typed) =>
        Parley.Core.NameCorrection.Correct(typed);

    // **Four members stood here and all four went with the fields that used them.** `StoreProblem`
    // refused a store another project owned; `EndpointProblem` refused an address another project
    // listened on; `FreeEndpoint` proposed one nothing held; `DefaultEndpoint` was the port an MCP
    // server looks for when told nothing.
    //
    // A project's store is derived from its name and its port is claimed when it is created, so the
    // form asks for neither and nothing is left to validate or propose. `DeadSurfaceTests` reported
    // each of them as named only in its own file, which is exactly what that guard is for.

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
                // **Two fields went, and that is the feature rather than a tidy-up.** A person naming a
                // project had to invent a filesystem path and pick a free port, on a form that could
                // only tell them afterwards that one of them clashed. The store is derived from the name
                // and the port is claimed, so both questions have gone rather than been given better
                // defaults — which is what "the name is the address" buys, said on a screen.
                new FormField(
                    "Description (optional) — one line shown beside the name:",
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
    /// <b>A success is one sentence, and the facts are on the projects list.</b> This used to print
    /// everything the command wrote for a shell -- the port, the folder, a paragraph about tunnels -- and
    /// then two sentences of its own. The owner's call of 2026-09-27: nobody reads that, and the one fact
    /// worth having is where the project answers, which the list now shows on its row.
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

            // **`--db` is what used to narrow an entry to one store, and it has not existed since 019.**
            // What narrows an entry today is `--project`, and leaving it out is what makes the entry serve
            // everything.
            { ServesManifest: false } =>
                $"{Created(values[0])} The autostart entry serves one project only; re-run "
                + "`parley autostart install` without --project to start this one.",

            _ => Created(values[0]),
        };

        await done(outcome).ConfigureAwait(false);
    }

}
