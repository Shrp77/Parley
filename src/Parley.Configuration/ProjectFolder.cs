using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// Where one project's files are, and what its own configuration says.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is the address.</b> A project is a directory under the root named by the project, so
/// naming a project is the whole of saying where its files are — there is no path for a person to supply
/// and none for them to get wrong.
/// </para>
/// <para>
/// <b>Two things are derived rather than recorded, which is stronger than checking them.</b>
/// A project's store is <c>parley.db</c> inside its own folder, so two projects cannot share one; and a
/// project's name is its directory entry, so two cannot share a name.
/// </para>
/// <para>
/// <b>There is no name in <c>project.json</c>, and no store.</b> The folder is the name, so the two cannot
/// disagree; the store is derived, so there is nothing to record. There is one node catalogue per machine,
/// not one per project.
/// </para>
/// <para>
/// Path composition and nothing else lives here. <see cref="ProjectSettings"/> stays free of filesystem
/// rules because a broker compares deployments belonging to machines it is not on, so a type that asked a
/// filesystem about a path would be answering the wrong question.
/// </para>
/// </remarks>
public static class ProjectFolder
{
    /// <summary>The directory under the root whose contents are the machine's projects.</summary>
    public const string ProjectsDirectoryName = "projects";

    /// <summary>What a project's own configuration is called, inside its folder.</summary>
    public const string ConfigurationFileName = "project.json";

    /// <summary>What a project's store is called, inside its folder.</summary>
    public const string StoreFileName = "parley.db";

    /// <summary>The directory holding every project on a machine.</summary>
    /// <param name="root">The machine's root.</param>
    /// <returns>The projects directory, which need not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    public static string ProjectsIn(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return Path.Combine(root, ProjectsDirectoryName);
    }

    /// <summary>One project's folder.</summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project.</param>
    /// <returns>The folder, which need not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    public static string For(string root, ProjectName name) =>
        Path.Combine(ProjectsIn(root), name.Value);

    /// <summary>Where a project's own configuration is.</summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project.</param>
    /// <returns>The path, which need not exist.</returns>
    public static string ConfigurationFor(string root, ProjectName name) =>
        Path.Combine(For(root, name), ConfigurationFileName);

    /// <summary>Where a project's store is.</summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project.</param>
    /// <returns>The path, which need not exist.</returns>
    /// <remarks>
    /// Derived rather than read: there is nowhere else for it to be, so there is no option that could
    /// name one and nothing to keep in step.
    /// </remarks>
    public static string StoreFor(string root, ProjectName name) =>
        Path.Combine(For(root, name), StoreFileName);

    /// <summary>
    /// A value that changes when this machine's projects do, and not otherwise.
    /// </summary>
    /// <param name="root">The machine's root.</param>
    /// <returns>An opaque stamp, compared with <see cref="StringComparison.Ordinal"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// <b>One implementation, because there are three callers.</b> The serve watcher, the operator's
    /// console and the terminal each decide whether to re-read by asking whether anything has moved, and
    /// separate spellings of that question drift apart.
    /// </para>
    /// <para>
    /// <b>What it covers is every project's configuration and when each was last written</b> — so a
    /// project appearing, going or being edited moves it, and a file somebody drops beside one does not.
    /// A folder with no configuration is not a project (see <see cref="TryRead"/>), so it is not counted
    /// here either; the two agree by construction rather than by a second rule.
    /// </para>
    /// <para>
    /// <b>The accepted limit.</b> Windows updates a write time on
    /// the system timer tick rather than per write, so two edits to one project inside about fifteen
    /// milliseconds share a timestamp. The file's length is in the stamp as well, at no cost, so such an
    /// edit is missed only when it also leaves the length unchanged, until something else touches the file.
    /// Written down rather than closed, because closing it means hashing every file — which is the read this
    /// exists to avoid.
    /// </para>
    /// <para>
    /// A root that cannot be listed stamps as empty, which is the same as having no projects. That is the
    /// safe direction: a reader that held projects a moment ago holds a stamp this cannot equal, so the
    /// mismatch sends it to a real read, where the failure becomes something an operator is shown rather
    /// than a silence.
    /// </para>
    /// </remarks>
    public static string StampOf(string root)
    {
        var directory = ProjectsIn(root);

        try
        {
            if (!Directory.Exists(directory))
            {
                return string.Empty;
            }

            return string.Join(
                "\n",
                Directory.GetDirectories(directory)
                    .Select(folder => Path.Combine(folder, ConfigurationFileName))
                    .Where(File.Exists)
                    .Order(StringComparer.Ordinal)
                    .Select(file => new FileInfo(file))
                    .Select(info => $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}"));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads one project's configuration out of its folder.
    /// </summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="name">The project, taken from the directory entry.</param>
    /// <param name="project">What it holds, when this returns true.</param>
    /// <param name="problem">Why it could not be read, when this returns false.</param>
    /// <returns><see langword="true"/> when the folder holds a usable project.</returns>
    /// <remarks>
    /// <para>
    /// <b>A folder with no configuration in it is not a project and is not a fault.</b> It is half-created,
    /// or somebody's scratch directory, and reporting it would make an ordinary machine look broken. It
    /// returns false with a null problem, which is the one case a caller passes over in silence.
    /// </para>
    /// <para>
    /// Everything else that is wrong is reported <b>naming the project and its file</b>, never as the
    /// machine having no projects, as <see cref="ProjectManifest.TryRead"/> does.
    /// </para>
    /// </remarks>
    public static bool TryRead(
        string root, ProjectName name, out ProjectSettings? project, out string? problem)
    {
        project = null;
        problem = null;

        var file = ConfigurationFor(root, name);

        if (!File.Exists(file))
        {
            return false;
        }

        string text;

        try
        {
            // Shared, so a reader never stands in the way of the rename that commits a write. See
            // ReadShared: without FileShare.Delete this is not a reader seeing half a file, it is a
            // reader stopping the write from happening at all.
            text = ReadShared(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = $"{name.Value}: {file} could not be read — {exception.Message}";
            return false;
        }

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException exception)
        {
            problem = $"{name.Value}: {file} is not valid JSON — {exception.Message}";
            return false;
        }

        if (parsed is not JsonObject entry)
        {
            problem = $"{name.Value}: {file} does not hold an object";
            return false;
        }

        var settings = new ProjectSettings(
            name,
            StoreFor(root, name),
            Text(entry, "endpoint") ?? string.Empty,
            Text(entry, "description"));

        if (settings.Problems() is { Count: > 0 } faults)
        {
            problem = $"{name.Value}: {file}: {string.Join("; ", faults)}";
            return false;
        }

        project = settings;

        return true;
    }

    /// <summary>
    /// Writes one project's configuration, creating its folder.
    /// </summary>
    /// <param name="root">The machine's root.</param>
    /// <param name="project">What to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is there and cannot be parsed, so writing over it would discard it.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The name and the store are not written: the folder is the name and the store is derived from it, so
    /// writing either would create something for a reader to disagree with.
    /// </para>
    /// <para>
    /// <b>Four rules, each because this is a configuration file with no second copy.</b> Unknown keys
    /// survive, the byte order mark and line endings survive, a file that cannot be parsed is refused
    /// rather than replaced, and the write is committed by rename rather than made in place.
    /// </para>
    /// </remarks>
    public static void Write(string root, ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var folder = For(root, project.Name);
        var file = Path.Combine(folder, ConfigurationFileName);

        // Refused rather than replaced, for the same reason reading refuses it. A project's endpoint is
        // the only record of where its broker listens, so a write that started fresh over a file it could
        // not understand would destroy the one copy of it.
        var document = Existing(file)
            ?? throw new InvalidDataException(
                $"{file} exists but could not be parsed, so writing over it would discard it.");

        document["_readme"] = new JsonArray([.. Guidance.Select(line => JsonValue.Create(line))]);
        document["endpoint"] = project.Endpoint;

        if (project.Description is { Length: > 0 } description)
        {
            document["description"] = description;
        }
        else
        {
            // The other half of preserving unknown keys: the fields this build owns are authoritative, so
            // an optional one that is now absent must go rather than linger. Preserving everything
            // indiscriminately would make an optional field impossible to clear.
            _ = document.Remove("description");
        }

        _ = Directory.CreateDirectory(folder);

        Commit(file, document);
    }

    private static readonly string[] Guidance =
    [
        "One project. The folder this file sits in is the project's name, and `parley.db` beside",
        "it is its store - neither is written here, because a name or a path recorded in two",
        "places is two places that come to disagree.",
        "",
        "Managed from `parley`. Editing by hand is fine; the same rules are",
        "applied on read, so a mistake is refused rather than half-applied - and whatever this",
        "version does not recognise is carried across untouched, so an older Parley editing a",
        "newer one's file does not eat it.",
        "",
        "This block is the exception: it is rewritten on every write, so notes of your own belong",
        "anywhere else in the file rather than here.",
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Reads a string member, or null when it is absent or not a string.</summary>
    private static string? Text(JsonObject entry, string member) =>
        entry.TryGetPropertyValue(member, out var value) && value is JsonValue holding
        && holding.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>Reads the document already at a path, for a write that must not lose it.</summary>
    /// <param name="path">Where to look.</param>
    /// <returns>The document, an empty one when absent, or null when it cannot be parsed.</returns>
    private static JsonObject? Existing(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(ReadShared(path)) as JsonObject;
        }
        catch (JsonException)
        {
            // Null means one thing only: the file is there and is not JSON. Anything else — a sharing
            // violation lasting microseconds — is let through as what it is, because reporting it as
            // "could not be parsed, so writing over it would discard it" is the sentence that tells
            // somebody their configuration is destroyed, printed because a file was briefly open.
            return null;
        }
    }

    /// <summary>
    /// Puts a document at a path by writing beside it and renaming over the top.
    /// </summary>
    /// <param name="path">What to write.</param>
    /// <param name="document">What it should hold.</param>
    /// <remarks>
    /// <para>
    /// <b>The failure worth designing against is not a bad value but a half-written file.</b>
    /// <see cref="File.WriteAllText(string, string)"/> truncates first, so a process that stops between
    /// the truncate and the write leaves nothing — and every reader then refuses it, correctly and
    /// permanently, because refusing to overwrite a configuration file nobody can parse is the rule that
    /// protects it the rest of the time.
    /// </para>
    /// <para>
    /// The pending file is a sibling so the commit stays within one volume, and it is <b>unique per
    /// write</b>: a fixed name is shared by every writer, so two of them do not merely race — one commits
    /// the other's half-written file.
    /// </para>
    /// <para>
    /// <see cref="File.Replace(string, string, string)"/> rather than
    /// <see cref="File.Move(string, string, bool)"/>, with readers opening
    /// <see cref="FileShare.Delete"/>. Measured on Windows, eight readers against 300 writes: Move was
    /// denied 287 times whatever the readers' share mode, Replace 300 times against
    /// <see cref="FileShare.Read"/> and not once against <see cref="FileShare.ReadWrite"/> |
    /// <see cref="FileShare.Delete"/>. Neither half works alone.
    /// </para>
    /// </remarks>
    private static void Commit(string path, JsonObject document)
    {
        var existed = File.Exists(path);
        var mark = existed && HasByteOrderMark(path);
        var crlf = existed && ReadShared(path).Contains("\r\n", StringComparison.Ordinal);
        var json = document.ToJsonString(Indented) + "\n";

        if (crlf)
        {
            json = json.ReplaceLineEndings("\r\n");
        }

        var pending = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.writing";

        File.WriteAllText(
            pending,
            json,
            mark ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(false));

        try
        {
            try
            {
                File.Replace(pending, path, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                // Nothing to replace: this project's first configuration. No handle can be open on a file
                // that does not exist, so the plain rename is safe here and only here.
                File.Move(pending, path, overwrite: true);
            }
        }
        catch
        {
            // Never left behind to be mistaken for configuration, or to make the next write fail. Nested
            // so it is reached from the fallback rename as well.
            try
            {
                File.Delete(pending);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The move already failed; the caller is about to hear about that.
            }

            throw;
        }
    }

    /// <summary>Reads a file without standing in the way of whoever replaces it.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>Its contents.</returns>
    /// <remarks>
    /// <see cref="FileShare.Delete"/> is the part that matters: a rename onto a file somebody holds
    /// without it is refused outright, so a reader does not see half a file — it stops the write from
    /// happening at all, and the command fails with an access error naming a file nothing else is writing.
    /// <see cref="FileShare.ReadWrite"/> beside it for the same reason in the other direction.
    /// </remarks>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static bool HasByteOrderMark(string path)
    {
        try
        {
            // Shared for the same reason as ReadShared, which this cannot use because it wants bytes
            // rather than text.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            Span<byte> head = stackalloc byte[3];

            return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
                && head is [0xEF, 0xBB, 0xBF];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
