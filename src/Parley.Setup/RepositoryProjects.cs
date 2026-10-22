using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// The projects the repository a command is run in answers in, read from the launch configuration at its root.
/// </summary>
/// <remarks>
/// <para>
/// <b>A command's project comes from its context</b>, and the repository somebody is standing in is part of it: enrolment
/// writes one launch entry per project a repository answers in — <c>parley-&lt;project&gt;</c>, carrying
/// <c>PARLEY_PROJECT</c> — so a repository names its own projects already.
/// </para>
/// <para>
/// <b>The repository is found by its <c>.git</c>, and only its own <c>.mcp.json</c> is read.</b> Walking upward to the
/// first launch configuration anywhere would let one in a home directory, or in a checkout the repository happens to sit
/// inside, choose a project for a folder that is not a Parley repository at all. Outside any repository nothing is read.
/// </para>
/// <para>
/// <b>An entry that connects by address names no project.</b> A <c>PARLEY_BROKER</c> says where to connect and nothing
/// about which project that is, so neither its key nor anything else is taken as one. A value that is not a project name —
/// a <c>${VARIABLE}</c> reference, say — is not one either. A launch configuration that cannot be read names nothing:
/// a broken file in the working directory must not stop every command from running.
/// </para>
/// </remarks>
public static class RepositoryProjects
{
    /// <summary>Reads the projects named by the repository containing a directory.</summary>
    /// <param name="workingDirectory">Where the command is being run, or null.</param>
    /// <returns>The distinct project names, in the order the launch configuration gives them; empty when there are none.</returns>
    public static IReadOnlyList<string> Read(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return [];
        }

        DirectoryInfo? directory;

        try
        {
            directory = new DirectoryInfo(workingDirectory);
        }
        catch (Exception invalid) when (invalid is ArgumentException or PathTooLongException or NotSupportedException
                                            or System.Security.SecurityException)
        {
            return [];
        }

        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
               && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return [];
        }

        var launch = Path.Combine(directory.FullName, ".mcp.json");

        if (!File.Exists(launch))
        {
            return [];
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(
                File.ReadAllText(launch),
                documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }

        IReadOnlyList<LaunchEntry> entries;

        try
        {
            // Through the one reader of launch entries, so both spellings of a key and every hand-edited shape are read
            // exactly as enrolment reads them -- KeyRuleTests holds every reader of `mcpServers` to it.
            entries = LaunchEntries.Read(root?["mcpServers"] as JsonObject);
        }
        catch (InvalidDataException)
        {
            return [];
        }

        var names = new List<string>();

        foreach (var entry in entries)
        {
            // A pinned PARLEY_PROJECT names the project; a key alone names it only when the entry does not connect by
            // address, since an address says where to connect and nothing about which project that is.
            var named = entry.Pinned?.Trim() ?? (entry.Broker is null ? entry.Project : null);

            if (named is not null && ProjectName.IsValid(named) && !names.Contains(named, StringComparer.Ordinal))
            {
                names.Add(named);
            }
        }

        return names;
    }
}
