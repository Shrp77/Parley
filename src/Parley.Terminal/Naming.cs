using Parley.Setup;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Naming a project from inside the surface, through the code the command runs.
/// </summary>
/// <remarks>
/// <para>
/// Convenience rather than capability: <c>parley project add</c> has always done this, and it is
/// done once per project. What it buys is not having to leave a full-screen application to run a
/// command whose arguments are on the screen you left.
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
    /// <summary>What the form naming the running deployment is called.</summary>
    internal const string NameRunningTitle = "Name the running deployment";

    /// <summary>What the form creating a project is called.</summary>
    internal const string CreateTitle = "Create a project";

    /// <summary>What is said when the command refused.</summary>
    internal const string NothingWritten = "Nothing was written.";

    /// <summary>What is said when it did not.</summary>
    internal const string Named = "Named. It is on the project list now.";

    /// <summary>Opens the form that names the deployment this machine already runs.</summary>
    /// <param name="app">The application the form opens in.</param>
    /// <param name="manifestPath">The manifest to write into.</param>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="entry">The installed entry, whose paths pre-fill the form.</param>
    /// <param name="queue">Runs the command, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when the form has been dealt with.</param>
    /// <returns>When the form is open.</returns>
    internal static Task OpenNameRunningAsync(
        IApplication app,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry entry,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return OpenAsync(
            app,
            NameRunningTitle,
            manifestPath,
            projects,
            entry,
            new Proposed(
                Store: entry.Store!,
                Endpoint: entry.Listen ?? DefaultEndpoint,
                NodeConfig: entry.NodeConfig,
                StoreWhy: "the database the running broker owns; every project has its own file",
                EndpointWhy: "where the running broker listens; change it only if this project's "
                             + "broker is on another machine"),
            queue,
            done);
    }

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

        var taken = projects.Select(p => p.Endpoint).ToList();

        if (entry?.Listen is { } listening)
        {
            taken.Add(listening);
        }
        else if (entry is { ServesManifest: false })
        {
            taken.Add(DefaultEndpoint);
        }

        return OpenAsync(
            app,
            CreateTitle,
            manifestPath,
            projects,
            entry,
            new Proposed(
                Store: null,
                Endpoint: FreeEndpoint(taken),
                NodeConfig: null,
                StoreWhy: "a new database file; every project has its own, and the broker creates "
                          + "it on first start",
                EndpointWhy: "where this project's broker will listen; a port nothing here holds "
                             + "was chosen, so change it only if it must be reachable from elsewhere"),
            queue,
            done);
    }

    /// <summary>Whether there is a running deployment that nothing in the manifest names yet.</summary>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="installed">The installed entry, if any.</param>
    /// <returns>Whether the form has anything to pre-fill from.</returns>
    /// <remarks>
    /// An entry that serves the manifest is already serving whatever is named there, so there is
    /// no unnamed deployment to name — and an entry whose store some project already claims has
    /// been named once. Offering it either way would invite somebody to name the same broker
    /// twice, which is two projects sharing a store and is what §4.1 forbids.
    /// </remarks>
    internal static bool CanNameRunning(
        IReadOnlyList<ProjectSettings> projects, InstalledEntry? installed)
    {
        ArgumentNullException.ThrowIfNull(projects);

        return installed is { ServesManifest: false, Store: { Length: > 0 } store }
            && !projects.Any(p => ProjectManifest.PathComparer.Equals(p.Store, store));
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

    /// <summary>Why a typed store would be refused, or null.</summary>
    internal static string? StoreProblem(string typed, IReadOnlyList<ProjectSettings> projects)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return "a project needs a database file";
        }

        var held = projects.FirstOrDefault(p => ProjectManifest.PathComparer.Equals(p.Store, typed));

        return held is null
            ? null
            : $"'{held.Name}' already owns that database; two projects cannot share one (SPEC.md §4.1)";
    }

    /// <summary>Why a typed endpoint would be refused, or null.</summary>
    internal static string? EndpointProblem(string typed, IReadOnlyList<ProjectSettings> projects)
    {
        if (!Uri.TryCreate(typed, UriKind.Absolute, out _))
        {
            return $"not an absolute address; one looks like {DefaultEndpoint}";
        }

        var held = projects.FirstOrDefault(p =>
            string.Equals(p.Endpoint.TrimEnd('/'), typed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        return held is null
            ? null
            : $"'{held.Name}' already listens there; each project's broker needs its own port";
    }

    /// <summary>Where a new project's store should go, next to the ones this machine has.</summary>
    /// <param name="name">The project being created.</param>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="entry">The installed entry, if any.</param>
    /// <returns>A path beside the existing stores.</returns>
    /// <remarks>
    /// Beside the existing stores rather than in a fresh default, because a machine that keeps
    /// its stores somewhere has already decided where, and a second convention is a second place
    /// to look. Named after the project, as SPEC.md §4.1 draws it: <c>alpha.db</c>, <c>beta.db</c>.
    /// </remarks>
    internal static string ProposedStore(
        string name, IReadOnlyList<ProjectSettings> projects, InstalledEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var beside = projects.Select(p => Path.GetDirectoryName(p.Store))
            .FirstOrDefault(d => !string.IsNullOrEmpty(d))
            ?? (entry?.Store is { } running ? Path.GetDirectoryName(running) : null)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".parley");

        return Path.Combine(beside, name + ".db");
    }

    /// <summary>The default endpoint if nothing holds it, otherwise a free loopback port.</summary>
    /// <param name="taken">Endpoints already spoken for on this machine.</param>
    /// <returns>An endpoint nothing in <paramref name="taken"/> uses.</returns>
    internal static string FreeEndpoint(IReadOnlyList<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        if (!taken.Any(t => Same(t, DefaultEndpoint)))
        {
            return DefaultEndpoint;
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);

            listener.Start();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            listener.Stop();

            var candidate = string.Create(
                CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");

            if (!taken.Any(t => Same(t, candidate)))
            {
                return candidate;
            }
        }

        // Sixteen ports handed out by the system and every one already in the manifest is not a
        // state a machine reaches; the proposal is left blank and the field asks.
        return string.Empty;
    }

    /// <summary>The endpoint an MCP server looks for when told nothing.</summary>
    internal static string DefaultEndpoint { get; } = string.Create(
        CultureInfo.InvariantCulture, $"http://127.0.0.1:{BrokerEndpoint.DefaultPort}");

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the form, checks every field as it is typed, and writes on confirmation.</summary>
    private static Task OpenAsync(
        IApplication app,
        string title,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry? entry,
        Proposed proposed,
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
                new FormField(
                    "Database path — " + proposed.StoreWhy + ":",
                    proposed.Store ?? ProposedStore("project", projects, entry),
                    typed => StoreProblem(typed, projects)),
                new FormField(
                    "Broker address — " + proposed.EndpointWhy + ":",
                    proposed.Endpoint,
                    typed => EndpointProblem(typed, projects)),
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

    /// <summary>Runs the command and reports what it said, in its own words.</summary>
    /// <remarks>
    /// <para>
    /// A refusal comes back from the command's own validation and is shown as it was written. A
    /// surface that summarised it to "invalid" would take away the one thing that says which rule
    /// was broken.
    /// </para>
    /// <para>
    /// What happens next is said every time, because naming a project writes a file and starts
    /// nothing, and the first person to use this form assumed the opposite. Which sentence is said
    /// depends on the entry: none, one serving a single store, or one serving the manifest.
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
            new ProjectFields(
                values[1],
                values[2],
                values[3].Length == 0 ? null : values[3]),
            said,
            complained,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var lines = (said.ToString() + complained)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .ToList();

        lines.Add(code != 0 ? NothingWritten : Named);

        if (code == 0)
        {
            lines.Add(entry switch
            {
                null =>
                    "Nothing starts it yet. `parley autostart install` registers an entry that "
                    + "serves every project named here, and starts it now.",

                { ServesManifest: false } =>
                    "The autostart entry serves one store by explicit paths and will not start "
                    + "this one. Re-run `parley autostart install` with no --db to have it serve "
                    + "every project in the manifest.",

                _ =>
                    "The broker serving the manifest starts it within a few seconds; the list "
                    + "will show it answering.",
            });
        }

        await done(Reports.Sentences(lines)).ConfigureAwait(false);
    }

    /// <summary>What a form proposes for each field, and the one line saying why.</summary>
    private sealed record Proposed(
        string? Store,
        string Endpoint,
        string? NodeConfig,
        string StoreWhy,
        string EndpointWhy);
}
