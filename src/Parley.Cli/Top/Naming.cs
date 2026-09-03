using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Naming a project from inside the surface, through the code the command runs.
/// </summary>
/// <remarks>
/// <para>
/// FR-015. Convenience rather than capability: <c>parley project add</c> has always done this, and
/// it is done once per project. What it buys is not having to leave a full-screen application to
/// run a command whose arguments are on the screen you left.
/// </para>
/// <para>
/// <b>It calls <see cref="ProjectCommand"/> rather than writing the manifest itself</b>, which is
/// the whole of the design. The manifest has exactly one writer, and this is that same program;
/// a second path that assembled a <c>ProjectSettings</c> and wrote it would be a second writer
/// with its own idea of the rules — and it would sidestep the lock that stops two concurrent
/// writers discarding one another's work, which was a real defect fixed in #78 and not a
/// hypothetical one.
/// </para>
/// <para>
/// <b>Two forms, because the two questions are different.</b> The first version had one, with
/// five blank prompts, and the person filling it in answered every one only to be told at the end
/// that the name had to be lowercase — and had to go and read a scheduled task to find the paths
/// it wanted. Naming the deployment this machine already runs is a form whose every answer is
/// already written down in the autostart entry, so it is pre-filled from there. Creating a new
/// project needs values that do <i>not</i> exist yet — a fresh store, a port nothing else holds —
/// so those are proposed rather than asked for blank. Both validate as each answer is typed,
/// which is what the refusal at the end could never do.
/// </para>
/// </remarks>
internal static class Naming
{
    /// <summary>Names the deployment this machine's autostart entry already serves.</summary>
    /// <param name="console">Where to draw, and where the fields are typed.</param>
    /// <param name="manifestPath">The manifest to write into.</param>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="entry">The installed entry, whose paths pre-fill the form.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>When it has been named, or refused.</returns>
    internal static Task NameRunningAsync(
        IAnsiConsole console,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry entry,
        CancellationToken cancellationToken)
    {
        console.MarkupLine(
            "[bold]The deployment this machine already runs.[/] [dim]Every path below was read "
            + "from the autostart entry; Enter keeps it.[/]");

        return CollectAndWriteAsync(
            console,
            manifestPath,
            projects,
            entry,
            new Proposed(
                Store: entry.Store!,
                Endpoint: entry.Listen ?? DefaultEndpoint,
                NodeConfig: entry.NodeConfig,
                StoreWhy: "The database the running broker owns. Every project has its own file.",
                EndpointWhy: "Where the running broker listens. Change it only if this project's "
                             + "broker is on another machine."),
            cancellationToken);
    }

    /// <summary>Creates a project that does not exist yet.</summary>
    /// <param name="console">Where to draw, and where the fields are typed.</param>
    /// <param name="manifestPath">The manifest to write into.</param>
    /// <param name="projects">What the manifest already names.</param>
    /// <param name="entry">The installed entry, if any, for where stores live and what to say after.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>When it has been named, or refused.</returns>
    /// <remarks>
    /// The endpoint proposed is the default port when nothing on this machine holds it — an MCP
    /// server told nothing about a broker looks there, which is what lets a repository's launch
    /// configuration say only who it is — and a free loopback port otherwise, because a second
    /// deployment cannot share the first one's. SPEC.md §4.1: each project gets its own store and
    /// its own transport endpoint.
    /// </remarks>
    internal static Task CreateAsync(
        IAnsiConsole console,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry? entry,
        CancellationToken cancellationToken)
    {
        console.MarkupLine(
            "[bold]A new project.[/] [dim]Its own database and its own port, proposed below; "
            + "Enter accepts a proposal.[/]");

        var taken = projects.Select(p => p.Endpoint).ToList();

        if (entry?.Listen is { } listening)
        {
            taken.Add(listening);
        }
        else if (entry is { ServesManifest: false })
        {
            taken.Add(DefaultEndpoint);
        }

        return CollectAndWriteAsync(
            console,
            manifestPath,
            projects,
            entry,
            new Proposed(
                Store: null,
                Endpoint: FreeEndpoint(taken),
                NodeConfig: null,
                StoreWhy: "A new database file. Every project has its own, and the broker creates "
                          + "it on first start.",
                EndpointWhy: "Where this project's broker will listen. A port nothing here holds "
                             + "was chosen; change it only if it must be reachable from elsewhere."),
            cancellationToken);
    }

    /// <summary>Asks for each field, validating as it is typed, then writes through the command.</summary>
    private static async Task CollectAndWriteAsync(
        IAnsiConsole console,
        string manifestPath,
        IReadOnlyList<ProjectSettings> projects,
        InstalledEntry? entry,
        Proposed proposed,
        CancellationToken cancellationToken)
    {
        Guidance(console,
            "A label for this machine only, never sent anywhere. Lowercase letters, digits and "
            + "hyphens, starting with a letter — the same rule as an agent's role.");

        var name = console.Prompt(
            new TextPrompt<string>("Project name:")
                .Validate(typed => NameProblem(typed, projects) is { } why
                    ? ValidationResult.Error($"[red]{Safe.Text(why)}[/]")
                    : ValidationResult.Success()));

        Guidance(console, proposed.StoreWhy);

        var store = console.Prompt(
            Prompt("Database path:", proposed.Store ?? ProposedStore(name, projects, entry))
                .Validate(typed => StoreProblem(typed, projects) is { } why
                    ? ValidationResult.Error($"[red]{Safe.Text(why)}[/]")
                    : ValidationResult.Success()));

        Guidance(console, proposed.EndpointWhy);

        var listen = console.Prompt(
            Prompt("Broker address:", proposed.Endpoint)
                .Validate(typed => EndpointProblem(typed, projects) is { } why
                    ? ValidationResult.Error($"[red]{Safe.Text(why)}[/]")
                    : ValidationResult.Success()));

        Guidance(console,
            "Optional, and it decides whether this project works unattended. With a node.json "
            + "here, this machine starts a listed repository's agent when a question arrives for "
            + "it and nobody has it open, and keeps it running. Without one, a question to an "
            + "agent nobody has open waits, times out and is ghosted; agents answer only while a "
            + "person has their repository open. Blank starts nothing.");

        var nodeConfig = console.Prompt(
            Prompt("Node catalogue [dim](optional)[/]:", proposed.NodeConfig).AllowEmpty());

        Guidance(console,
            "Optional. One line shown beside the name on the project list, here and in the web "
            + "console.");

        var description = console.Prompt(
            new TextPrompt<string>("Description [dim](optional)[/]:").AllowEmpty());

        var said = new StringWriter();
        var complained = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["add", name],
            manifestPath,
            new ProjectFields(
                store,
                listen,
                nodeConfig.Length == 0 ? null : nodeConfig,
                description.Length == 0 ? null : description),
            said,
            complained,
            cancellationToken).ConfigureAwait(false);

        Report(console, code, said.ToString(), complained.ToString(), entry);
    }

    /// <summary>A prompt with a proposal the person can accept by pressing Enter.</summary>
    private static TextPrompt<string> Prompt(string question, string? proposal)
    {
        var prompt = new TextPrompt<string>(question);

        return proposal is { Length: > 0 } ? prompt.DefaultValue(proposal) : prompt;
    }

    /// <summary>One dim line saying what a field is for, before it is asked.</summary>
    private static void Guidance(IAnsiConsole console, string what) =>
        console.MarkupLine($"[dim]{Safe.Text(what)}[/]");

    /// <summary>Why a typed name would be refused, or null.</summary>
    /// <remarks>
    /// The same rule <see cref="ProjectCommand"/> applies, said here so the refusal arrives at
    /// the prompt rather than after four more answers. Kept in one place there and quoted from
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
    /// <remarks>
    /// Beside the existing stores rather than in a fresh default, because a machine that keeps
    /// its stores somewhere has already decided where, and a second convention is a second place
    /// to look. Named after the project, as SPEC.md §4.1 draws it: <c>alpha.db</c>, <c>beta.db</c>.
    /// </remarks>
    internal static string ProposedStore(
        string name, IReadOnlyList<ProjectSettings> projects, InstalledEntry? entry)
    {
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
        // state a machine reaches; the proposal is left blank and the prompt asks.
        return string.Empty;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The endpoint an MCP server looks for when told nothing.</summary>
    internal static string DefaultEndpoint { get; } = string.Create(
        CultureInfo.InvariantCulture, $"http://127.0.0.1:{BrokerEndpoint.DefaultPort}");

    /// <summary>Shows what the command said, in its own words, and what happens next.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="code">What the command returned.</param>
    /// <param name="said">What it wrote to output.</param>
    /// <param name="complained">What it wrote to error.</param>
    /// <param name="entry">The installed entry, which decides whether the project will be served.</param>
    /// <remarks>
    /// The refusal comes back from the command's own validation and is shown as it was written.
    /// A surface that summarised it to "invalid" would take away the one thing that says which
    /// rule was broken.
    /// <para>
    /// What happens next is said every time, because naming a project writes a file and starts
    /// nothing, and the first person to use this form assumed the opposite. Which sentence is
    /// said depends on the entry: none, one serving a single store, or one serving the manifest.
    /// </para>
    /// </remarks>
    private static void Report(
        IAnsiConsole console, int code, string said, string complained, InstalledEntry? entry)
    {
        foreach (var line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[dim]{Safe.Text(line.TrimEnd())}[/]");
        }

        foreach (var line in complained.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[red]{Safe.Text(line.TrimEnd())}[/]");
        }

        if (code != 0)
        {
            console.MarkupLine("[yellow]Nothing was written.[/]");
            return;
        }

        console.MarkupLine("[green]Named.[/] [dim]It is on the project list now.[/]");

        console.MarkupLine(entry switch
        {
            null =>
                "[yellow]Nothing starts it yet.[/] `parley autostart install` registers an entry "
                + "that serves every project named here, and starts it now.",

            { ServesManifest: false } =>
                "[yellow]The autostart entry serves one store by explicit paths and will not "
                + "start this one.[/] Re-run `parley autostart install` with no --db to have it "
                + "serve every project in the manifest.",

            _ =>
                "[dim]The autostart entry serves the manifest, so this starts with the broker: "
                + "at the next logon, or sooner by restarting it.[/]",
        });
    }

    /// <summary>What a form proposes for each field, and the one line saying why.</summary>
    private sealed record Proposed(
        string? Store,
        string Endpoint,
        string? NodeConfig,
        string StoreWhy,
        string EndpointWhy);
}
