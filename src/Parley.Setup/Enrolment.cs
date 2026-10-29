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
/// a second writer. So the writing lives here and both callers use it.
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
    /// replacing it are one act.
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
        // update, and this is the path `parley enroll` takes.
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

    /// <summary>Turns a failure arriving after files have changed into one that says which.</summary>
    /// <param name="cause">What went wrong.</param>
    /// <param name="written">Everything already changed on disk.</param>
    /// <returns>The failure to raise instead.</returns>
    /// <remarks>
    /// <para>
    /// <b>Three files are written in order and the exception carried only the last one's reason.</b>
    /// A catalogue that cannot be written arrives after the launch configuration has been replaced —
    /// so the run reports a failure, the person's reasonable reading is that nothing happened, and
    /// what has actually happened is a repository announcing itself as an agent that no node will
    /// ever start. *The damage is not the failure, it is the report*, which this repository has now
    /// written down in three features.
    /// </para>
    /// <para>
    /// <b>Reported rather than rolled back.</b> Undoing a write is another write that can fail the
    /// same way, on a file whose previous contents this would have to have kept — and the previous
    /// contents include other people's servers. The state left behind is a working attended agent
    /// that is not startable unattended, which is an ordinary configuration; being told is what was
    /// missing.
    /// </para>
    /// <para>
    /// Cancellation is deliberately not wrapped: it is somebody pressing Ctrl+C, and callers and
    /// tests both distinguish it by type. The files it leaves are the same, and
    /// <see cref="OverwriteHeldAsync"/> is what keeps each of them whole.
    /// </para>
    /// </remarks>
    private static InterruptedEnrolment Interrupted(Exception cause, IReadOnlyList<string> written) =>
        new(
            $"{cause.Message} This did not finish, and had already changed "
            + $"{string.Join(", ", written.Select(Quoting.Flat))}. Nothing is undone automatically, "
            + "because undoing a write is another write that can fail the same way: re-run once the "
            + "fault is fixed, or withdraw.",
            cause,
            [.. written]);

    /// <summary>Each project on this machine and the endpoint it holds.</summary>
    /// <param name="root">The machine's root, or null when none is known.</param>
    /// <returns>The projects by name, or null when there is no root or it cannot be read.</returns>
    /// <remarks>
    /// What lets an entry pinned to an address be matched to a project instead of guessed at.
    /// </remarks>
    private static IReadOnlyDictionary<string, string>? EndpointsOf(string? root) =>
        root is not null && ProjectManifest.TryRead(root, out var projects, out _)
            ? projects.ToDictionary(project => project.Name.Value, project => project.Endpoint, StringComparer.Ordinal)
            : null;

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
    /// <param name="agent">Which agent, which is what the catalogue is keyed by.</param>
    /// <param name="workspace">Where it would be started.</param>
    /// <remarks>
    /// <b>Keyed by identity, not by role.</b> A role is what one project calls a checkout. One checkout is
    /// one entry, whatever it is called in however many projects, and enrolling into a further project
    /// writes nothing here at all.
    /// </remarks>
    public static void List(JsonObject catalogue, AgentIdentity agent, string workspace)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        catalogue[agent.ToString()] = new JsonObject
        {
            ["command"] = "claude",
            ["workspace"] = workspace,
        };
    }

    /// <summary>What a path is a link to, or null when it is an ordinary file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its target, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every file this tooling writes inside a checkout asks this first.</b> Both of them are
    /// tracked, so a repository can ship either as a link — and this tooling is pointed at directories
    /// of clones somebody else wrote. Following one turns a clone into a write wherever the link says.
    /// </para>
    /// <para>
    /// <b>And every file it reads out of one.</b> Enrolment reads a clone's <c>.mcp.json</c> to suggest a
    /// name and owns line, so a link pointing out of the checkout would copy a line of somebody else's file
    /// into what the person reads. Internal rather than private because the readers are in
    /// <c>EnrollCommand</c>, and a second implementation is how two rules come to differ.
    /// </para>
    /// <para>
    /// <b>A link whose target cannot be read is still a link.</b> Its own permissions can hide the target, and then reading
    /// it gives nothing rather than failing; taking that as "no link" let a write go through it (review, lens 2 run 72).
    /// So a reparse point is a link whatever its target reads as, and so is a path whose attributes cannot be read.
    /// </para>
    /// </remarks>
    internal static string? Linked(string path)
    {
        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return UnreadableTarget;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }

        try
        {
            return new FileInfo(path).LinkTarget ?? UnreadableTarget;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return UnreadableTarget;
        }
    }

    private const string UnreadableTarget = "a target that cannot be read";

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
    /// <b>The third file, held the way the other two are.</b> The launch configuration's acquisition
    /// retries for two seconds, and somebody saving in an editor during that window would otherwise be
    /// overwritten by the copy read before they typed; with no file the plan is <i>create</i>, so a file
    /// that appeared meanwhile would be replaced wholesale by the frame.
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
    /// <param name="agent">Which agent to remove.</param>
    /// <returns>Whether it was there to remove.</returns>
    /// <remarks>
    /// Keyed by identity, as <see cref="List"/> is. The identity comes from the repository being withdrawn,
    /// so a withdrawal removes the entry for the checkout it was pointed at and never one that happens to
    /// answer to the same name.
    /// </remarks>
    public static bool Unlist(JsonObject catalogue, AgentIdentity agent)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        return catalogue.Remove(agent.ToString());
    }

    /// <summary>Says which other catalogued checkouts already answer to a name in a project.</summary>
    /// <param name="agents">The catalogue's <c>agents</c> object.</param>
    /// <param name="identity">The checkout being enrolled, which is never its own claimant.</param>
    /// <param name="project">The project the name is being taken in, or null.</param>
    /// <param name="role">The name being taken.</param>
    /// <returns>The workspaces that already claim it, in the order the catalogue holds them.</returns>
    /// <remarks>
    /// <para>
    /// <b>Keyed by identity, two repositories claiming one name are separate entries, so the collision
    /// has to be looked for.</b> An agent's name is the one thing this protocol makes unforgeable, and
    /// two repositories claiming one means either may collect the other's mail.
    /// </para>
    /// <para>
    /// <b>Said rather than refused</b>, because the node is what refuses: it leaves a contested
    /// name out of what it announces, so neither is started under it. Refusing here would refuse the
    /// repair as well as the mistake — renaming one of the two is an enrolment.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> Claimants(
        JsonObject agents, AgentIdentity identity, string? project, string role)
    {
        var found = new List<string>();

        foreach (var entry in agents)
        {
            if (string.Equals(entry.Key, identity.ToString(), StringComparison.Ordinal)
                || entry.Value?["workspace"] is not JsonValue value
                || !value.TryGetValue<string>(out var workspace)
                || !LaunchConfiguration.TryRead(workspace, out var declared, out _))
            {
                continue;
            }

            if (declared.Memberships.Any(membership =>
                string.Equals(membership.Name, role, StringComparison.Ordinal)
                && (project is null
                    || membership.Project is null
                    || string.Equals(membership.Project, project, StringComparison.Ordinal))))
            {
                found.Add(workspace);
            }
        }

        return found;
    }

    /// <summary>Refuses an enrolment of a second checkout of a repository this machine already starts.</summary>
    /// <param name="catalogued">Where the catalogue says that identity lives, or null when it says nothing.</param>
    /// <param name="offered">Where this enrolment says it lives.</param>
    /// <exception cref="InvalidDataException">Both are there, so they are two checkouts of one agent.</exception>
    /// <remarks>
    /// <para>
    /// <b>A move and a copy are the same two facts from the files, and the filesystem is what tells them
    /// apart.</b> An identity catalogued at one path and offered at another is either a checkout that moved
    /// or a checkout that was copied — and they want opposite repairs. What distinguishes them is whether
    /// anything is still at the old path: if there is not, it moved, and the entry is repointed with a note.
    /// If there is, both exist, and cataloguing the second would point one identity at two working
    /// directories.
    /// </para>
    /// <para>
    /// <b>Asked here rather than in <see cref="EnrollmentPlan"/>, which decides everything else about an
    /// enrolment.</b> That type takes file contents as strings and returns file contents as strings, and
    /// reaching a filesystem from inside it would hand every one of its tests one.
    /// </para>
    /// </remarks>
    private static void RefuseACopy(string? catalogued, string offered)
    {
        if (Refused(catalogued, offered) is { } refusal)
        {
            throw new InvalidDataException(refusal);
        }
    }

    /// <summary>Why a repository cannot be enrolled against this catalogue, if it cannot.</summary>
    /// <param name="agents">The catalogue's <c>agents</c> object.</param>
    /// <param name="workspace">The repository being enrolled.</param>
    /// <returns>The refusal, or <see langword="null"/> when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agents"/> is null.</exception>
    /// <remarks>
    /// <b>The bulk command asks this and <see cref="AddAsync"/> applies it, which is one rule and two
    /// callers rather than two rules.</b> They differ only in when: a bulk run refuses the whole manifest
    /// before writing any of it, because a half-enrolled set is worse than none, and the single-repository
    /// path has nothing to be half of. The answer is the same either way, and a rule enforced on
    /// one writer and not the other is the mistake this guards against.
    /// </remarks>
    public static string? CopyRefusal(JsonObject agents, string workspace)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var identity = EnrollmentPlan.IdentityIn(ReadLaunch(workspace));

        if (!identity.Known)
        {
            // Nothing declared, so nothing is already catalogued under it: the enrolment is about to mint
            // one, and a minted identity is in no catalogue by construction.
            return null;
        }

        return Refused(agents[identity.ToString()]?["workspace"]?.GetValue<string>(), workspace);
    }

    /// <summary>Reads a repository's launch configuration, or null when there is none to read.</summary>
    /// <param name="workspace">The repository.</param>
    /// <returns>The file's contents, or null.</returns>
    private static string? ReadLaunch(string workspace)
    {
        var path = LaunchPath(workspace);

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The one comparison behind both callers.</summary>
    /// <param name="catalogued">Where the catalogue says that identity lives, or null.</param>
    /// <param name="offered">Where this enrolment says it lives.</param>
    /// <returns>The refusal, or null.</returns>
    private static string? Refused(string? catalogued, string offered) =>
        catalogued is { Length: > 0 }
        && !WorkspacePath.SameRepository(catalogued, offered)
        && Directory.Exists(catalogued)
            ? Catalogues.Copied(catalogued, offered)
            : null;

    /// <summary>
    /// Makes one repository an agent of a project.
    /// </summary>
    /// <param name="entry">The repository, its role, and whether it may run unattended.</param>
    /// <param name="project">The project it becomes an agent of.</param>
    /// <param name="catalogueFile">The project's catalogue.</param>
    /// <param name="instructionsPath">Its standing instructions; the peers frame is appended when it has none.</param>
    /// <param name="cancellationToken">Stops the reads and writes.</param>
    /// <param name="manifestPath">
    /// The machine's root, whose lock is held for the length of the call so that deciding from the
    /// catalogue and writing to it are one act. Null where there is none.
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
        string? manifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueFile);

        // `EnrollmentPlan.Validate` holds every rule: a name an `AgentName` can parse, and an owns line
        // inside the cap the broker's schema enforces and free of anything that could break its own
        // roster row. Both ways in apply it, because neither failure is visible where it is caused: a
        // bad name announces a repository that appears on no roster, and an over-long owns line is
        // refused by a CHECK constraint at registration, far from the person who typed it.
        if (EnrollmentPlan.Validate([entry], Directory.Exists, _ => "enrolment") is { Count: > 0 } wrong)
        {
            throw new InvalidDataException(string.Join("; ", wrong));
        }

        // The machine-wide gate first, then this project's catalogue, then the repository's launch
        // configuration. Three files, one order, everywhere -- the gate is the only thing that can
        // make "nobody else starts this workspace" and "now I do" one act, because those touch
        // different files and no hold on either covers the other.
        using var gate = await Catalogues.GateAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        // The catalogue is held first, and both halves acquire in this order. It has the same
        // read-modify-write shape as the launch configuration and a commoner collision: six
        // repositories joining one project is what setting a deployment up looks like. Catalogue before
        // launch in AddAsync and in WithdrawAsync, so two runs cannot each hold the half the other
        // is waiting for.
        using var catalogueHeld = await HoldAsync(catalogueFile, cancellationToken).ConfigureAwait(false);

        var catalogue = catalogueHeld is null
            ? await ReadCatalogueAsync(catalogueFile, cancellationToken).ConfigureAwait(false)
            : ParseCatalogue(
                await ReadHeldAsync(catalogueHeld, cancellationToken).ConfigureAwait(false), catalogueFile);

        var agents = AgentsOf(catalogue, catalogueFile);
        var launchPath = LaunchPath(entry.Workspace);

        var written = new List<string>();
        var said = new List<string>();

        // The catalogue is one file keyed by the agent's identity, so one checkout has one entry whatever
        // it is called in however many projects; enrolling into a further project adds a name and
        // touches nothing else.

        // Read and written under one exclusive handle, because a repository may have several writers
        // (one per project it joins). Read-modify-write without exclusion loses the earlier writer's
        // entry and reports success; the damage is not the failure, it is the report.
        RepositoryPlan plan = null!;

        try
        {
            using (var launch = await HoldAsync(launchPath, cancellationToken).ConfigureAwait(false))
            {
                // The third file, held from here. The frame replaces this file with its own contents plus
                // the frame, so the read and the write have to be one act as the other two are.
                var (notes, declined) = await HoldInstructionsAsync(instructionsPath, cancellationToken)
                    .ConfigureAwait(false);

                using (notes)
                {
                    var mark = notes is not null && HeldHasByteOrderMark(notes);

                    var declared = launch is null
                        ? null
                        : await ReadHeldAsync(launch, cancellationToken).ConfigureAwait(false);

                    // **The identity is read before the catalogue is asked anything, because it is the
                    // key.** Whether this machine already starts this checkout is not a question about a
                    // name — a name is what one project calls it — so it cannot be asked until the
                    // repository has said which agent it is.
                    var identity = EnrollmentPlan.IdentityIn(declared);

                    if (!identity.Known)
                    {
                        identity = AgentIdentity.Mint();
                    }

                    var key = identity.ToString();
                    var listed = agents[key]?["workspace"]?.GetValue<string>();

                    RefuseACopy(listed, entry.Workspace);

                    // **An attended enrolment beside a catalogued checkout still widens what a start
                    // starts, and this is the only moment anybody could be told.** The entry about to be
                    // written here is loaded by the client the node launches — one working directory has
                    // one launch configuration and a client brings up every server in it — so this
                    // project gets an agent that answers unattended without having asked for one. Consent
                    // is the machine's, because there is one catalogue.
                    if (!entry.Unattended && agents.ContainsKey(key))
                    {
                        said.Add(
                            $"this machine already starts {Quoting.Flat(entry.Workspace)}, so its node "
                            + $"will bring this entry up as well and {Quoting.Quoted(project)} will have "
                            + "an agent here with nobody watching");
                    }

                    foreach (var claimant in Claimants(agents, identity, project, entry.Agent))
                    {
                        said.Add(
                            $"{Quoting.Flat(claimant)} already answers to {Quoting.Quoted(entry.Agent)} in "
                            + $"{Quoting.Quoted(project)}. Two repositories claiming one name means either "
                            + "may collect the other's mail, and this machine will start neither under it "
                            + "until one is renamed");
                    }


                    plan = EnrollmentPlan.Describe(
                        entry,
                        declared,
                        notes is null
                            ? await DeclinedTextAsync(declined, instructionsPath, cancellationToken)
                                .ConfigureAwait(false)
                            : await ReadHeldAsync(notes, cancellationToken).ConfigureAwait(false),
                        agents.ContainsKey(key),
                        project,
                        EndpointsOf(manifestPath),
                        listed,
                        identity);

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
                List(agents, plan.Identity, entry.Workspace);

                await SaveHeldCatalogueAsync(catalogueHeld, catalogueFile, catalogue, cancellationToken)
                    .ConfigureAwait(false);

                written.Add(catalogueFile);
            }
        }
        catch (Exception ex) when (written.Count > 0 && ex is not OperationCanceledException)
        {
            throw Interrupted(ex, written);
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
    /// <b>A repository may answer in several projects, so a withdrawal that named none would have
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

        // **Which agent this repository is, and whether it is still one when this is done.** The catalogue
        // is keyed by identity and one entry serves every project the checkout answers in, so withdrawing
        // from one of two projects must leave it there: taking it would stop the other project being able
        // to start the repository, which nobody asked for. Both are filled in from the file below.
        var withdrawn = default(AgentIdentity);
        var stillAnAgent = false;

        // Catalogue before launch, the same order AddAsync takes them in. Two runs acquiring in
        // opposite orders is how each comes to hold the half the other is waiting for.
        using var catalogueHeld = catalogueFile is null
            ? null
            : await HoldAsync(catalogueFile, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(launchPath))
            {
                notes.Add($"{launchPath}: already absent");
            }
            else
            {
                // Held for the whole read-modify-write, for the reason AddAsync is: a repository may
                // have several writers, and this is the one that deletes.
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
                    withdrawn = EnrollmentPlan.IdentityIn(text);

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
                            // Never a guess: taking either would remove a working agent from a
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
                            // A key derived from a project name is one somebody may reasonably have used
                            // first, so an entry naming nobody is not necessarily ours.
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
                            var left = LaunchEntries.Read(servers).Count;

                            // **The identity goes with the last entry and not before it.** It belongs to
                            // the repository rather than to any one project, so a withdrawal from one of
                            // two must leave it — that is what lets the other project go on addressing
                            // this checkout as the same agent. Once nothing answers from here the
                            // repository is an agent nowhere, and a key this program wrote into somebody
                            // else's file has to come back out: withdrawing undoes exactly what adding
                            // did, which is the property this half exists to keep.
                            if (left == 0)
                            {
                                _ = root.Remove(LaunchConfiguration.IdentityMember);
                            }

                            await OverwriteHeldAsync(
                                held,
                                launchPath,
                                root.ToJsonString(SerialisationOptions.Indented) + "\n",
                                cancellationToken).ConfigureAwait(false);


                            written.Add(launchPath);
                            notes.Add(
                                $"{launchPath}: {Quoting.Flat(candidates[0].Key)} entry removed, {left} parley "
                                + $"entry(s) and {servers.Count - left} other server(s) left");
                        }
                        else
                        {
                            notes.Add($"{launchPath}: no entry for {Where(project)} to remove");
                        }

                        // Asked of the file as it stands, on every path through the block above. Three of
                        // those paths leave the repository answering somewhere — the entry was removed and
                        // others remain, the entry belongs to another agent and was left, the file was left
                        // alone for a reason — and all three must keep the catalogue line.
                        stillAnAgent = LaunchEntries.Read(servers).Count > 0;
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
                    // Left where the repository still answers somewhere. One entry says this machine may
                    // start this checkout, and every project it answers in is started through that one
                    // entry — so removing it because one project let it go would take the others' ability
                    // to start it.
                    if (stillAnAgent)
                    {
                        notes.Add(
                            $"{catalogueFile}: entry kept, the repository still answers in another project");
                    }
                    else if (!withdrawn.Known)
                    {
                        // A launch file that declares no identity, or one this could not read. The entry
                        // cannot be found by name, and guessing which one is meant is how a withdrawal
                        // comes to remove somebody else's.
                        notes.Add(
                            $"{catalogueFile}: left alone, {launchPath} says which agent this is and it "
                            + "does not — re-enrol it, or remove the entry by hand");
                    }
                    else if (Unlist(agents, withdrawn))
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
        }
        catch (Exception ex) when (written.Count > 0 && ex is not OperationCanceledException)
        {
            throw Interrupted(ex, written);
        }

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
