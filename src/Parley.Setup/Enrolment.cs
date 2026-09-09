using System.Text;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// Making one repository an agent of a project, and unmaking it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One writer, because two would disagree.</b> The files that make a repository an agent are
/// its launch configuration and its line in a project's catalogue, and until this existed only
/// <c>parley enroll apply</c> wrote them — driven by a manifest, in bulk. The terminal adds one
/// repository at a time and has no manifest, and reimplementing the writes there would have been
/// the second writer FR-008 forbids. So the writing lives here and both callers use it.
/// </para>
/// <para>
/// <b><see cref="WithdrawAsync"/> is written beside <see cref="AddAsync"/> so the two cannot drift.</b>
/// A withdrawal that leaves a launch configuration behind produces a repository that still
/// announces itself as an agent of a project it was removed from, which is worse than either
/// state on its own. Keeping the inverse next to the original is the cheapest way to notice when
/// one of them grows a step.
/// </para>
/// <para>
/// <b>The instructions file is never rewritten, and withdrawing never touches it at all.</b>
/// Adding appends the peers frame when the file has no <c>## Peers</c> section, and does nothing
/// to it when it has one — the frame belongs to the project, the roster block inside it is written
/// by <c>/parley:write-peers</c>, and nothing here can tell a line the project added from a line a
/// tool did, so a section that exists is left exactly as it is. Withdrawing declines even the
/// append, and says so rather than leaving somebody to wonder.
/// </para>
/// <para>
/// <b>That paragraph used to say the file was never touched by either half, which was not true of
/// adding.</b> Recorded rather than quietly corrected, because a comment claiming a program does
/// not write a file in somebody's repository is the kind of wrong that is believed: it is read by
/// whoever is deciding whether this surface is safe to point at a checkout.
/// </para>
/// </remarks>
public static class Enrolment
{
    /// <summary>The launch configuration's name, in every repository.</summary>
    public const string LaunchFile = ".mcp.json";

    /// <summary>Text without a byte-order mark.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Text with one, for a file that already had one.</summary>
    private static readonly UTF8Encoding Utf8WithMark = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The launch configuration of a repository.</summary>
    /// <param name="workspace">The repository directory.</param>
    /// <returns>The path, whether or not anything is there.</returns>
    public static string LaunchPath(string workspace) => Path.Combine(workspace, LaunchFile);

    /// <summary>
    /// Writes what a plan decided, and says which files changed.
    /// </summary>
    /// <param name="decide">
    /// Given the launch configuration and the standing instructions as they stand — each null when
    /// there is none — says what to write. Called while both are held, so that reading one and
    /// replacing it are one act. **The instructions used to be read by the caller and passed in**,
    /// which put the read and the write at opposite ends of the call with the launch configuration's
    /// two-second acquisition between them.
    /// </param>
    /// <param name="workspace">The repository the plan is for.</param>
    /// <param name="instructionsPath">Where its standing instructions live.</param>
    /// <param name="cancellationToken">Stops the writes.</param>
    /// <returns>
    /// What was decided, every path written in the order written, and anything the writing itself
    /// had to say — which is only ever a file it declined to touch, since everything else was decided
    /// before the writing began.
    /// </returns>
    /// <remarks>
    /// The byte-order mark a file already had is kept. <c>ReadAllText</c> strips one and
    /// <c>WriteAllText</c> does not put it back, so appending a block to a file that had one
    /// rewrites every byte of it — a whole-file diff for a twelve-line addition, in the file a
    /// project reads on every turn.
    /// </remarks>
    public static async Task<(RepositoryPlan Plan, IReadOnlyList<string> Written, IReadOnlyList<string> Said)> WriteAsync(
        Func<string?, string?, RepositoryPlan> decide,
        string workspace,
        string instructionsPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decide);

        var written = new List<string>();
        var said = new List<string>();
        RepositoryPlan plan;

        // The launch configuration is held across the decision and the write, for the reason
        // AddAsync holds it: reading it, deciding, and writing it back without exclusion is a lost
        // update, and this is the path `enroll apply` takes. **It was missed when AddAsync was
        // fixed** -- six bulk runs sharing one catalogue told an agent its enrolment had succeeded
        // and left it out of the file that makes it startable.
        using (var held = await HoldAsync(LaunchPath(workspace), cancellationToken).ConfigureAwait(false))
        {
            // Launch then instructions, the order AddAsync takes them in after the catalogue. Two
            // runs acquiring in opposite orders is how each comes to hold the half the other wants.
            var (notes, declined) = await HoldInstructionsAsync(instructionsPath, cancellationToken)
                .ConfigureAwait(false);

            using (notes)
            {
                var mark = notes is not null && HeldHasByteOrderMark(notes);

                plan = decide(
                    held is null ? null : await ReadHeldAsync(held, cancellationToken).ConfigureAwait(false),
                    notes is null
                        ? await DeclinedTextAsync(declined, instructionsPath, cancellationToken)
                            .ConfigureAwait(false)
                        : await ReadHeldAsync(notes, cancellationToken).ConfigureAwait(false));

                if (plan.LaunchConfiguration is { } launch)
                {
                    var path = LaunchPath(workspace);

                    await OverwriteHeldAsync(held, path, launch, cancellationToken).ConfigureAwait(false);
                    written.Add(path);
                }

                if (plan.Instructions is { } instructions)
                {
                    if (declined is not null)
                    {
                        said.Add(declined);
                    }
                    else
                    {
                        await OverwriteHeldAsync(
                            notes,
                            instructionsPath,
                            instructions,
                            cancellationToken,
                            mark ? Utf8WithMark : Utf8).ConfigureAwait(false);

                        written.Add(instructionsPath);
                    }
                }
            }
        }

        return (plan, written, said);
    }

    /// <summary>What a declined instructions file currently holds, for the plan's sake.</summary>
    /// <param name="declined">Why it was declined, or null when it simply is not there.</param>
    /// <param name="path">Where it lives.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Its text, or null.</returns>
    /// <remarks>
    /// Read unheld, and only ever to decide. A declined file is one nothing is going to write, so
    /// the read is safe where the write is not — and without it the plan would report a *create* for
    /// a file that plainly exists, which reads as a fault in the tool rather than as a link refused.
    /// </remarks>
    private static async Task<string?> DeclinedTextAsync(
        string? declined,
        string path,
        CancellationToken cancellationToken)
    {
        if (declined is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts an agent in a catalogue, so a node may start it.</summary>
    /// <param name="catalogue">The catalogue's <c>agents</c> object.</param>
    /// <param name="agent">The role the node would start.</param>
    /// <param name="workspace">Where it would be started.</param>
    public static void List(JsonObject catalogue, string agent, string workspace)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        catalogue[agent] = new JsonObject
        {
            ["command"] = "claude",
            ["workspace"] = workspace,
        };
    }

    /// <summary>Which agent a launch configuration's parley entry says it starts, or null.</summary>
    /// <param name="entry">The <c>mcpServers.parley</c> node, whatever shape it is in.</param>
    /// <returns>The name, or null when the entry names nobody.</returns>
    /// <remarks>
    /// Null and "somebody else" are deliberately different answers. An entry that names nobody
    /// cannot be another agent's, because nothing identifies it as anybody's — every entry this
    /// program writes carries the variable — so a withdrawal proceeds on one and declines on the
    /// other. Reading the name back is the only thing here that can tell them apart: SPEC.md §16
    /// makes the launch environment the whole of who an agent is.
    /// </remarks>
    public static string? NamedBy(JsonNode? entry) =>
        entry?["env"]?[EnrollmentPlan.AgentVariable] is JsonValue value
        && value.TryGetValue<string>(out var name)
        && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    /// <summary>What a path is a link to, or null when it is an ordinary file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its target, or null.</returns>
    /// <remarks>
    /// <b>Every file this tooling writes inside a checkout asks this first.</b> Both of them are
    /// tracked, so a repository can ship either as a link — and this tooling is pointed at directories
    /// of clones somebody else wrote. Following one turns a clone into a write wherever the link says.
    /// </remarks>
    private static string? Linked(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Takes the launch configuration for the length of a read-modify-write.</summary>
    /// <param name="path">The launch configuration.</param>
    /// <param name="cancellationToken">Stops the waiting.</param>
    /// <returns>The handle, or null when there is no file to hold yet.</returns>
    /// <exception cref="IOException">Somebody else held it throughout.</exception>
    /// <remarks>
    /// <para>
    /// <b>An exclusive handle rather than a lock file or a named mutex.</b> A lock file would leave
    /// something behind in somebody's repository the first time a process died holding it, and a
    /// named mutex is a second mechanism to reason about on two platforms. <c>FileShare.None</c> is
    /// what the operating system already offers, and .NET honours it between processes on both.
    /// </para>
    /// <para>
    /// <b>It waits rather than failing at once.</b> A collision here is two ordinary runs passing
    /// each other, not contention worth reporting — but it waits a bounded time and then throws,
    /// because a wait with no end is a tool that hangs, and this one is often run from a script.
    /// </para>
    /// <para>
    /// <b>Null when the file does not exist, and it is not created here.</b> Opening
    /// <c>OpenOrCreate</c> would leave an empty <c>.mcp.json</c> in a repository the run then
    /// decided to change nothing in — a file written into somebody's checkout by a command that
    /// reported writing nothing.
    /// </para>
    /// </remarks>
    internal static async Task<FileStream?> HoldAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // **A launch configuration that is a link is refused, and this is the one place it can be.**
        // `.mcp.json` is a tracked file, so a repository can ship one as a symlink — and this tooling
        // is pointed at directories of clones somebody else wrote (`parley enroll scan /src`) and then
        // writes into each. Following the link makes a hostile checkout into a write-anywhere-the-user
        // -can primitive: a link to a shell profile, and the next enrolment overwrites it with JSON.
        //
        // Refused rather than replaced. Replacing it would silently destroy a link somebody may have
        // meant, and this cannot tell the two apart; the person can.
        if (Linked(path) is { } target)
        {
            throw new InvalidDataException(
                $"{path} is a link to {Quoting.Flat(target)}, so writing it would write somewhere "
                + "outside this repository. Replace the link with a file, or remove it");
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(
                    path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true);
            }
            catch (IOException) when (attempt < 40 && File.Exists(path))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads a held file from the start.</summary>
    /// <param name="stream">The handle.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Its text.</returns>
    internal static async Task<string> ReadHeldAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;

        using var reader = new StreamReader(stream, leaveOpen: true);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the standing instructions for the length of a read-modify-write.</summary>
    /// <param name="path">Where they live.</param>
    /// <param name="cancellationToken">Stops the waiting.</param>
    /// <returns>The handle — null when the file is absent or declined — and why it was declined.</returns>
    /// <remarks>
    /// <para>
    /// <b>The third file, held the way the other two are.</b> It was read at the top of a call and
    /// written at the bottom, with the launch configuration's acquisition in between — which retries
    /// for two seconds. What lands in that window is somebody saving in an editor, and their work was
    /// overwritten by the copy read before they typed it. The create arm was worse than a lost
    /// append: with no file the plan is <i>create</i>, so a file that appeared in the window was
    /// replaced wholesale by the frame rather than having the frame added to it.
    /// </para>
    /// <para>
    /// <b>A link is declined rather than thrown, and that is why this exists as a helper.</b>
    /// <c>HoldAsync</c> refuses one with an exception, which is right for the launch configuration —
    /// a repository that cannot be merged into stops the run. The frame is the least of the three
    /// writes and this tooling has always said it does not own this file, so here the same refusal
    /// becomes a sentence and the enrolment carries on. One helper because both writers need both
    /// halves, and the last time only one of them got them the other was still following links.
    /// </para>
    /// </remarks>
    private static async Task<(FileStream? Held, string? Declined)> HoldInstructionsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await HoldAsync(path, cancellationToken).ConfigureAwait(false), null);
        }
        catch (InvalidDataException linked)
        {
            return (null, $"{path}: left alone, {linked.Message}");
        }
    }

    /// <summary>Whether a held file starts with a byte-order mark.</summary>
    /// <param name="stream">The handle.</param>
    /// <returns>Whether the first three bytes are one.</returns>
    /// <remarks>
    /// Asked of the handle rather than of the path, because the handle is exclusive: the by-path
    /// version opens the file itself, which under this hold fails and is caught as "no mark". A
    /// silent false there rewrites every byte of a file that had one, which is the whole-file diff
    /// keeping the mark exists to avoid.
    /// </remarks>
    internal static bool HeldHasByteOrderMark(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;

        Span<byte> first = stackalloc byte[3];

        return stream.ReadAtLeast(first, 3, throwOnEndOfStream: false) == 3
            && first[0] == 0xEF
            && first[1] == 0xBB
            && first[2] == 0xBF;
    }

    /// <summary>Replaces what a held file holds, or writes one that was not there.</summary>
    /// <param name="stream">The handle, or null when the file did not exist.</param>
    /// <param name="path">Where it goes when it did not exist.</param>
    /// <param name="text">What it should hold.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <param name="encoding">How to encode it, or null for UTF-8 with no mark.</param>
    /// <remarks>
    /// <b>The create arm is exclusive too, and has to be.</b> Two runs both finding no file and both
    /// creating one is the same lost update at a different moment; <c>CreateNew</c> means the second
    /// is refused rather than silently overwriting the first — and refused loudly, since a caller
    /// that meant to add a second entry has just been told the file appeared underneath it.
    /// </remarks>
    internal static Task OverwriteHeldAsync(
        FileStream? stream,
        string path,
        string text,
        CancellationToken cancellationToken,
        Encoding? encoding = null)
    {
        // **Cancellation is honoured before the write and never during it.** Passing the token into
        // the write left a cancelled run holding an empty `.mcp.json` -- created, truncated, and
        // then abandoned -- which every reader afterwards reports as "not readable JSON". A few
        // kilobytes is not worth being able to interrupt half way, and the file being destroyed by
        // a Ctrl+C is a worse outcome than the write finishing.
        cancellationToken.ThrowIfCancellationRequested();

        // **The preamble is written here rather than left to the encoding.** `Encoding.GetBytes`
        // never emits one — only the `File.WriteAllText` family does — so an encoding chosen to keep
        // a byte-order mark would have quietly dropped it on the way through this.
        var chosen = encoding ?? Utf8;
        var body = chosen.GetBytes(text);
        var preamble = chosen.GetPreamble();
        var bytes = preamble.Length == 0 ? body : [.. preamble, .. body];

        if (stream is null)
        {
            // CreateNew refuses an existing path, so a link cannot be followed here: a link whose
            // target exists is itself an existing path, and one whose target does not is still a
            // directory entry this refuses to write over.
            var created = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: false);

            try
            {
                created.Write(bytes);
            }
            catch
            {
                // Only ever a file this call created a line ago: CreateNew refuses an existing one.
                created.Dispose();

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                throw;
            }

            created.Dispose();

            return Task.CompletedTask;
        }

        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush();

        return Task.CompletedTask;
    }

    /// <summary>How a note names the project it is talking about.</summary>
    /// <param name="project">The project, or null when none was named.</param>
    /// <returns>Words for it.</returns>
    /// <remarks>
    /// "this repository" rather than "no project", because a withdrawal that names none is asking
    /// about the repository rather than about a deployment, and a note reading <c>no entry for no
    /// project to remove</c> reads as a fault in the tool.
    /// </remarks>
    private static string Where(string? project) =>
        project is null ? "this repository" : $"{Quoting.Quoted(project)}";

    /// <summary>Takes an agent out of a catalogue.</summary>
    /// <param name="catalogue">The catalogue's <c>agents</c> object.</param>
    /// <param name="agent">The role to remove.</param>
    /// <returns>Whether it was there to remove.</returns>
    public static bool Unlist(JsonObject catalogue, string agent)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        return catalogue.Remove(agent);
    }

    /// <summary>
    /// Makes one repository an agent of a project.
    /// </summary>
    /// <param name="entry">The repository, its role, and whether it may run unattended.</param>
    /// <param name="project">The project it becomes an agent of.</param>
    /// <param name="catalogueFile">The project's catalogue.</param>
    /// <param name="instructionsPath">Its standing instructions, read but never written by this.</param>
    /// <param name="cancellationToken">Stops the reads and writes.</param>
    /// <param name="deployment">
    /// Every project the machine's manifest names, so an address-pinned entry can be resolved and an
    /// unattended start already granted elsewhere can be found. Null where there is no manifest,
    /// which refuses the first exactly as it was refused before and says nothing about the second.
    /// </param>
    /// <param name="manifestPath">
    /// Where that manifest lives, held for the length of the call so that checking whether another
    /// project starts this workspace and recording that this one does are one act. Null where there
    /// is no manifest, which is also where there is nothing to check.
    /// </param>
    /// <returns>What was decided and what was written.</returns>
    /// <exception cref="InvalidDataException">
    /// A file is in a shape this cannot merge into, or the repository already belongs somewhere.
    /// </exception>
    /// <remarks>
    /// The catalogue is read again here rather than taken from the caller, because the surface
    /// draws from it continuously and somebody may have edited it by hand between the draw and
    /// the confirmation. Reading it at the moment of writing is what makes the write true.
    /// </remarks>
    public static async Task<Outcome> AddAsync(
        EnrollmentEntry entry,
        string project,
        string catalogueFile,
        string instructionsPath,
        CancellationToken cancellationToken,
        IReadOnlyList<ProjectSettings>? deployment = null,
        string? manifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueFile);

        // The machine-wide gate first, then this project's catalogue, then the repository's launch
        // configuration. Three files, one order, everywhere -- the gate is the only thing that can
        // make "nobody else starts this workspace" and "now I do" one act, because those touch
        // different files and no hold on either covers the other.
        using var gate = await Catalogues.GateAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        // **The catalogue is held first, and both halves acquire in this order.** It has the same
        // read-modify-write shape as the launch configuration and a commoner collision: six
        // repositories joining one project is what setting a deployment up looks like, and six
        // unguarded writers left agents told their enrolment had succeeded and missing from the file
        // that makes them startable. Catalogue before launch in AddAsync and in WithdrawAsync, so
        // two runs cannot each hold the half the other is waiting for.
        using var catalogueHeld = await HoldAsync(catalogueFile, cancellationToken).ConfigureAwait(false);

        var catalogue = catalogueHeld is null
            ? await ReadCatalogueAsync(catalogueFile, cancellationToken).ConfigureAwait(false)
            : ParseCatalogue(
                await ReadHeldAsync(catalogueHeld, cancellationToken).ConfigureAwait(false), catalogueFile);

        var agents = AgentsOf(catalogue, catalogueFile);
        var launchPath = LaunchPath(entry.Workspace);

        if (entry.Unattended)
        {
            var already = Catalogues.Elsewhere(
                deployment, entry.Workspace, catalogueFile, out var unreadable);

            // Refused before anything is written, and refused even though the launch entry would
            // have been fine. Enrolling the repository in this project is admitted; starting it here
            // as well is what cannot be.
            if (already is not null)
            {
                throw new InvalidDataException(Catalogues.Refusal(already));
            }

            // And refused on the same terms when the machine could not be checked at all, which is
            // not the same fact and used to reach here as the same silence.
            if (unreadable is not null)
            {
                throw new InvalidDataException(unreadable);
            }
        }

        // **Read and written under one exclusive handle, because a repository now has more than one
        // writer.** Before this feature a second enrolment of one repository was refused outright, so
        // there was never a legitimate reason for two writers to hold this file at once; setting a
        // machine up for two projects is the ordinary case now. Read-modify-write over the whole file
        // without exclusion loses the earlier writer's entry *and reports success* -- eight projects
        // racing left one entry in the file and told two of them it had worked. That is the same
        // class as the withdrawal that removed another agent's entry and reported success: the damage
        // is not the failure, it is the report.
        RepositoryPlan plan;
        var written = new List<string>();
        var said = new List<string>();

        using (var launch = await HoldAsync(launchPath, cancellationToken).ConfigureAwait(false))
        {
            // **The third file, held from here rather than read at the top of the call.** The frame
            // replaces this file with its own contents plus the frame, so the read and the write have
            // to be one act as the other two are — and the acquisition above retries for two seconds,
            // which is a wide window to lose somebody's editor save in.
            var (notes, declined) = await HoldInstructionsAsync(instructionsPath, cancellationToken)
                .ConfigureAwait(false);

            using (notes)
            {
                var mark = notes is not null && HeldHasByteOrderMark(notes);

                plan = EnrollmentPlan.Describe(
                    entry,
                    launch is null ? null : await ReadHeldAsync(launch, cancellationToken).ConfigureAwait(false),
                    notes is null
                        ? await DeclinedTextAsync(declined, instructionsPath, cancellationToken)
                            .ConfigureAwait(false)
                        : await ReadHeldAsync(notes, cancellationToken).ConfigureAwait(false),
                    agents.ContainsKey(entry.Agent),
                    project,
                    deployment?.ToDictionary(
                        settings => settings.Name.Value,
                        settings => settings.Endpoint,
                        StringComparer.Ordinal),
                    agents[entry.Agent]?["workspace"]?.GetValue<string>());

                if (plan.LaunchConfiguration is { } text)
                {
                    await OverwriteHeldAsync(launch, launchPath, text, cancellationToken).ConfigureAwait(false);
                    written.Add(launchPath);
                }

                if (plan.Instructions is { } appended)
                {
                    // Skipped with a note rather than refusing the enrolment. The frame is the least
                    // of the three writes: a repository is an agent without it, and it is the one
                    // file here this tooling has always said it does not own.
                    if (declined is not null)
                    {
                        said.Add(declined);
                    }
                    else
                    {
                        await OverwriteHeldAsync(
                            notes,
                            instructionsPath,
                            appended,
                            cancellationToken,
                            mark ? Utf8WithMark : Utf8).ConfigureAwait(false);

                        written.Add(instructionsPath);
                    }
                }
            }
        }

        if (plan.AddsNodeEntry)
        {
            List(agents, entry.Agent, entry.Workspace);

            await SaveHeldCatalogueAsync(catalogueHeld, catalogueFile, catalogue, cancellationToken)
                .ConfigureAwait(false);

            written.Add(catalogueFile);
        }

        return new Outcome([.. plan.Notes, .. said], written);
    }

    /// <summary>
    /// Unmakes it: the launch configuration's parley entry and the catalogue line, and nothing else.
    /// </summary>
    /// <param name="workspace">The repository directory.</param>
    /// <param name="agent">The role being withdrawn.</param>
    /// <param name="project">The project being withdrawn from, or null when none is named.</param>
    /// <param name="catalogueFile">The project's catalogue, or null when the project has none.</param>
    /// <param name="cancellationToken">Stops the reads and writes.</param>
    /// <returns>What was removed, and what was already gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>The launch configuration is edited rather than deleted.</b> A repository may run other
    /// MCP servers, and a withdrawal that removed the file would take them with it. A file left
    /// holding an empty <c>mcpServers</c> is also the smaller diff and is what a person reverting
    /// this by hand would produce.
    /// </para>
    /// <para>
    /// Everything it cannot parse it leaves alone and reports. A withdrawal is somebody deciding
    /// an agent should stop being reachable; rewriting a file it does not understand in the
    /// course of that is a second, unasked-for change.
    /// </para>
    /// <para>
    /// <b>A repository may now answer in several projects, so a withdrawal that named none would have
    /// to pick.</b> It does not: one entry is taken when there is exactly one, and several are refused
    /// with every project named. Guessing here removes a working agent from a roster nobody was
    /// talking about, and the person finds out when a peer stops being answered.
    /// </para>
    /// </remarks>
    public static async Task<Outcome> WithdrawAsync(
        string workspace,
        string agent,
        string? project,
        string? catalogueFile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        var notes = new List<string>();
        var written = new List<string>();
        var launchPath = LaunchPath(workspace);

        // Catalogue before launch, the same order AddAsync takes them in. Two runs acquiring in
        // opposite orders is how each comes to hold the half the other is waiting for.
        using var catalogueHeld = catalogueFile is null
            ? null
            : await HoldAsync(catalogueFile, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(launchPath))
        {
            notes.Add($"{launchPath}: already absent");
        }
        else
        {
            // Held for the whole read-modify-write, for the reason AddAsync is: a repository may now
            // have more than one writer, and this is the one that deletes.
            //
            // **A link is a note here rather than an exception**, unlike the adding half. Everything
            // this cannot act on it leaves alone and reports — an unreadable file, servers of the
            // wrong shape, an entry belonging to another agent — and a withdrawal that threw for one
            // of those and reported the rest would be one rule with two spellings. The target is
            // untouched either way; what changes is whether the person gets a sentence or a trace.
            FileStream? held;

            try
            {
                held = await HoldAsync(launchPath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException linked)
            {
                notes.Add($"{launchPath}: left alone, {linked.Message}");

                return new Outcome(notes, written);
            }

            using var holding = held;

            var text = held is null
                ? await File.ReadAllTextAsync(launchPath, cancellationToken).ConfigureAwait(false)
                : await ReadHeldAsync(held, cancellationToken).ConfigureAwait(false);

            JsonNode? parsed;

            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = null;
                notes.Add($"{launchPath}: left alone, it is not readable JSON");
            }

            if (parsed is JsonObject root)
            {
                if (root["mcpServers"] is JsonObject servers)
                {
                    IReadOnlyList<LaunchEntry> entries;

                    try
                    {
                        entries = LaunchEntries.Read(servers);
                    }
                    catch (InvalidDataException duplicated)
                    {
                        // A file naming one server twice. Reported and left exactly as it is, like
                        // every other shape this does not understand -- a withdrawal is somebody
                        // deciding an agent should stop being reachable, and rewriting a file it
                        // cannot read in the course of that is a second, unasked-for change.
                        notes.Add($"{launchPath}: left alone, {duplicated.Message}");

                        return new Outcome(notes, written);
                    }

                    // The same precedence enrolling uses, run backwards: a withdrawal removes what
                    // an enrolment into this project would have written at, and nothing else. Two
                    // rules would drift, and the direction they drift in leaves a repository still
                    // announcing itself as an agent of somewhere it was removed from.
                    var candidates = project is null
                        ? entries
                        : LaunchEntries.Landing(servers, project).Existing is { } landed
                            ? (IReadOnlyList<LaunchEntry>)[landed]
                            : [];

                    var named = candidates.Count == 1 ? candidates[0].Agent : null;

                    if (candidates.Count > 1)
                    {
                        // FR-011. Never a guess: taking either would remove a working agent from a
                        // roster nobody was talking about, and the person finds out when a peer
                        // stops being answered.
                        notes.Add(
                            $"{launchPath}: left alone, it answers in "
                            + $"{string.Join(", ", candidates.Select(found => $"{Quoting.Quoted(found.Project ?? found.Key)}"))}"
                            + " — name which one to withdraw from");
                    }
                    else if (candidates.Count == 0)
                    {
                        notes.Add($"{launchPath}: no entry for {Where(project)} to remove");
                    }
                    else if (candidates[0] is { Broker: not null, Pinned: null } addressed
                             && project is not null)
                    {
                        // Pinned to an address and naming no project. Enrolling resolves that
                        // through the manifest; this does not have one, and removing an entry that
                        // may be another project's agent is the more expensive way to be wrong.
                        notes.Add(
                            $"{launchPath}: left alone, its {Quoting.Quoted(addressed.Key)} entry is "
                            + $"pinned to the broker at {Quoting.Quoted(addressed.Broker)}, which this "
                            + $"cannot match to {Where(project)}");
                    }
                    else if (candidates[0] is { Ours: false } stranger)
                    {
                        // **The rule below used to cover this and stopped being able to.** It read:
                        // an entry that names nobody cannot be another agent's, because nothing
                        // identifies it as anybody's and every entry this program writes carries the
                        // variable. That was sound while the key was fixed — an entry at `parley`
                        // naming nobody was ours by the only convention there was. A key derived
                        // from a project name is one somebody may reasonably have used first, so
                        // naming nobody no longer implies being ours.
                        notes.Add(
                            $"{launchPath}: left alone, {Quoting.Quoted(stranger.Key)} is not an entry this wrote");
                    }
                    else if (named is not null && !string.Equals(named, agent, StringComparison.Ordinal))
                    {
                        // Somebody else's. The workspace comes from the registry, which is what the
                        // agent said about itself and may be some time old, so a repository
                        // re-enrolled under a second name has two rows pointing at one file. Taking
                        // this one would unhook an agent nobody named, and would report success.
                        notes.Add(
                            $"{launchPath}: left alone, its {Quoting.Quoted(candidates[0].Key)} entry is "
                            + $"{Quoting.Quoted(named)} rather than {Quoting.Quoted(agent)}");
                    }
                    else if (servers.Remove(candidates[0].Key))
                    {
                        await OverwriteHeldAsync(
                            held,
                            launchPath,
                            root.ToJsonString(SerialisationOptions.Indented) + "\n",
                            cancellationToken).ConfigureAwait(false);

                        var left = LaunchEntries.Read(servers).Count;

                        written.Add(launchPath);
                        notes.Add(
                            $"{launchPath}: {Quoting.Flat(candidates[0].Key)} entry removed, {left} parley "
                            + $"entry(s) and {servers.Count - left} other server(s) left");
                    }
                    else
                    {
                        notes.Add($"{launchPath}: no entry for {Where(project)} to remove");
                    }
                }
                else
                {
                    notes.Add($"{launchPath}: left alone, it names no servers this understands");
                }
            }
            else if (parsed is not null)
            {
                notes.Add($"{launchPath}: left alone, it is not a JSON object");
            }
        }

        if (catalogueFile is null)
        {
            notes.Add("no catalogue: this project has none");
        }
        else if (!File.Exists(catalogueFile))
        {
            notes.Add($"{catalogueFile}: already absent");
        }
        else
        {
            var catalogue = catalogueHeld is null
                ? await ReadCatalogueAsync(catalogueFile, cancellationToken).ConfigureAwait(false)
                : ParseCatalogue(
                    await ReadHeldAsync(catalogueHeld, cancellationToken).ConfigureAwait(false), catalogueFile);

            if (catalogue["agents"] is JsonObject agents)
            {
                if (Unlist(agents, agent))
                {
                    await SaveHeldCatalogueAsync(catalogueHeld, catalogueFile, catalogue, cancellationToken)
                        .ConfigureAwait(false);

                    written.Add(catalogueFile);
                    notes.Add($"{catalogueFile}: entry for '{agent}' removed");
                }
                else
                {
                    notes.Add($"{catalogueFile}: no entry for '{agent}'");
                }
            }
            else
            {
                notes.Add($"{catalogueFile}: left alone, its agents are not an object");
            }
        }

        // Named rather than silently skipped. Somebody withdrawing an agent is entitled to know
        // that the file describing it to its peers keeps whatever it says about them, because
        // that is the one thing here that outlives the withdrawal.
        notes.Add("standing instructions: left untouched");

        return new Outcome(notes, written);
    }

    /// <summary>Reads a catalogue, refusing shapes that writing over would destroy.</summary>
    /// <param name="path">The catalogue file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The catalogue, or an empty one when the file is not there.</returns>
    /// <exception cref="InvalidDataException">It exists and is not readable JSON.</exception>
    public static async Task<JsonObject> ReadCatalogueAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return ParseCatalogue(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), path);
    }

    /// <summary>Reads a catalogue's text, refusing shapes that writing over would destroy.</summary>
    /// <param name="text">What the file holds.</param>
    /// <param name="path">Its path, for the message.</param>
    /// <returns>The catalogue.</returns>
    /// <exception cref="InvalidDataException">It is not a readable JSON object.</exception>
    /// <remarks>
    /// Split out so that a read through an exclusive handle refuses exactly what a read by path
    /// refuses. Two parsers would agree until one of them was edited.
    /// </remarks>
    private static JsonObject ParseCatalogue(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject catalogue)
            {
                return catalogue;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"{path} is not readable JSON: {ex.Message}", ex);
        }

        throw new InvalidDataException($"{path} is not a JSON object");
    }

    /// <summary>The catalogue's agents, refusing a shape that writing over would destroy.</summary>
    /// <param name="catalogue">The catalogue.</param>
    /// <param name="path">Where it came from, for the refusal.</param>
    /// <returns>The agents object, created and attached when absent.</returns>
    /// <exception cref="InvalidDataException"><c>agents</c> is present and is not an object.</exception>
    /// <remarks>
    /// Absent is the only shape that means "empty". An <c>agents</c> that is an array or a string
    /// is somebody's file in a state this does not understand, and casting it away and writing an
    /// object over it loses what it held as completely as an unparseable file would. That exact
    /// bug was found three times in this feature's first form, which is why it is refused here
    /// rather than tidied up.
    /// </remarks>
    public static JsonObject AgentsOf(JsonObject catalogue, string path)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        if (catalogue["agents"] is not null and not JsonObject)
        {
            throw new InvalidDataException(
                $"{path} has an \"agents\" entry that is not an object. Fix or move it first: " +
                "writing here would replace it.");
        }

        if (catalogue["agents"] is not JsonObject agents)
        {
            agents = [];
            catalogue["agents"] = agents;
        }

        return agents;
    }

    /// <summary>Writes a catalogue back.</summary>
    /// <param name="path">Where it goes.</param>
    /// <param name="catalogue">What to write.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The write.</returns>
    public static async Task SaveCatalogueAsync(
        string path,
        JsonObject catalogue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            path,
            catalogue.ToJsonString(SerialisationOptions.Indented) + "\n",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a catalogue back through the handle it was read under.</summary>
    /// <param name="held">The handle, or null when the file did not exist.</param>
    /// <param name="path">The catalogue file.</param>
    /// <param name="catalogue">What it should hold.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The write.</returns>
    /// <remarks>
    /// The public <see cref="SaveCatalogueAsync"/> stays for callers holding nothing — the terminal
    /// creates a catalogue for a project that had none, which is a write with no read before it and
    /// so nothing to be lost between them.
    /// </remarks>
    private static async Task SaveHeldCatalogueAsync(
        FileStream? held,
        string path,
        JsonObject catalogue,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await OverwriteHeldAsync(
            held,
            path,
            catalogue.ToJsonString(SerialisationOptions.Indented) + "\n",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What an add or a withdrawal decided, and what it wrote.</summary>
    /// <param name="Notes">One human-readable line per decision, in the order decided.</param>
    /// <param name="Written">Every file whose contents changed.</param>
    /// <remarks>
    /// The two are separate because a note is not evidence. Every step says what it did, including
    /// the ones that did nothing, and only the paths in <paramref name="Written"/> actually
    /// changed on disk — which is what a surface reporting "exactly what was removed" needs.
    /// </remarks>
    public sealed record Outcome(IReadOnlyList<string> Notes, IReadOnlyList<string> Written);
}
