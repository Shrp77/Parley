using System.Text.Json;
using System.Text.Json.Nodes;
using Parley.Core;

namespace Parley.Terminal;

/// <summary>
/// Who the line in each conversation goes to, remembered across restarts of this terminal.
/// </summary>
/// <param name="path">The file it is kept in, or <see langword="null"/> to keep it for this run only.</param>
/// <remarks>
/// <para>
/// <b>A narrowing is the person's decision and survives a restart.</b> If it lived only in memory,
/// every agent would be ticked again after the terminal restarted and the next line would go to all
/// of them.
/// </para>
/// <para>
/// <b>Kept on this machine, in the deployment's root, and nowhere else.</b> Who a person is writing to is a fact about their
/// screen rather than about the deployment: the broker records what was sent, never what was about to be. A file of the
/// terminal's own keeps it out of the protocol and off every other surface, which is the size this deserves.
/// </para>
/// <para>
/// <b>Only a narrowed line is recorded.</b> Everyone is what a conversation starts at, so an entry for it would be a
/// second spelling of the default. Clearing back to everyone removes the entry.
/// </para>
/// <para>
/// <b>A file that cannot be read or written costs the memory and nothing else.</b> Losing the ticks is an
/// inconvenience; a terminal that refused to open over a damaged convenience file would be a worse
/// one. The write is atomic so a crash part-way leaves the previous file, never half of one.
/// </para>
/// </remarks>
internal sealed class AddressBook(string? path)
{
    /// <summary>What the file is called, in the deployment's root folder.</summary>
    internal const string FileName = "terminal-addressing.json";

    private readonly Lock _gate = new();

    private Dictionary<string, Dictionary<string, List<string>>>? _entries;

    /// <summary>The file for a deployment, or <see langword="null"/> when there is no deployment folder to keep it in.</summary>
    /// <param name="root">The deployment's root folder, where its manifest lives.</param>
    /// <returns>The path.</returns>
    /// <remarks>
    /// In the root itself, never its parent: the parent is whatever folder holds the deployment, a person's own folder
    /// among others, and no removal of the deployment would take the file away (review, lens 3 run 66).
    /// </remarks>
    internal static string? In(string? root) =>
        root is { Length: > 0 } folder ? Path.Combine(Path.GetFullPath(folder), FileName) : null;

    /// <summary>Who the line in a conversation was narrowed to, or <see langword="null"/> when it goes to everyone.</summary>
    /// <param name="project">The project.</param>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The recipients last chosen, in the order they were chosen.</returns>
    internal IReadOnlyList<AgentName>? Recall(string project, string conversation)
    {
        lock (_gate)
        {
            if (!Loaded().TryGetValue(project, out var conversations)
                || !conversations.TryGetValue(conversation, out var names))
            {
                return null;
            }

            // Read back through the name rule, because the file is text anybody can edit. A name that no longer parses is
            // dropped rather than trusted.
            var recalled = new List<AgentName>();

            foreach (var name in names)
            {
                if (AgentName.TryCreate(name, out var agent))
                {
                    recalled.Add(agent);
                }
            }

            return recalled.Count > 0 ? recalled : null;
        }
    }

    /// <summary>Records who the line in a conversation goes to.</summary>
    /// <param name="project">The project.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="narrowed">The recipients, or <see langword="null"/> when the line goes to everyone.</param>
    internal void Remember(string project, string conversation, IReadOnlyList<AgentName>? narrowed)
    {
        lock (_gate)
        {
            var entries = Loaded();

            if (narrowed is { Count: > 0 })
            {
                if (!entries.TryGetValue(project, out var conversations))
                {
                    conversations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    entries[project] = conversations;
                }

                var names = narrowed.Select(agent => agent.Value).ToList();

                if (conversations.TryGetValue(conversation, out var was) && was.SequenceEqual(names, StringComparer.Ordinal))
                {
                    return;
                }

                conversations[conversation] = names;
            }
            else if (entries.TryGetValue(project, out var conversations) && conversations.Remove(conversation))
            {
                if (conversations.Count == 0)
                {
                    entries.Remove(project);
                }
            }
            else
            {
                return;
            }

            Save(entries);
        }
    }

    /// <summary>The entries, read from the file the first time they are asked for.</summary>
    /// <returns>The entries.</returns>
    private Dictionary<string, Dictionary<string, List<string>>> Loaded()
    {
        if (_entries is { } entries)
        {
            return entries;
        }

        _entries = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        if (path is null || !File.Exists(path))
        {
            return _entries;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject projects)
            {
                foreach (var (project, value) in projects)
                {
                    if (value is not JsonObject conversations)
                    {
                        continue;
                    }

                    var kept = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                    foreach (var (conversation, names) in conversations)
                    {
                        if (names is JsonArray list)
                        {
                            kept[conversation] = [.. list.Select(name => name?.GetValue<string>()).OfType<string>()];
                        }
                    }

                    _entries[project] = kept;
                }
            }
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            // Starts empty; see the remarks on the type.
        }

        return _entries;
    }

    /// <summary>Writes the entries, replacing the file in one step.</summary>
    /// <param name="entries">The entries.</param>
    private void Save(Dictionary<string, Dictionary<string, List<string>>> entries)
    {
        if (path is null)
        {
            return;
        }

        var written = new JsonObject();

        foreach (var (project, conversations) in entries)
        {
            var inner = new JsonObject();

            foreach (var (conversation, names) in conversations)
            {
                inner[conversation] = new JsonArray([.. names.Select(name => (JsonNode?)JsonValue.Create(name))]);
            }

            written[project] = inner;
        }

        var temporary = path + ".tmp";

        try
        {
            File.WriteAllText(temporary, written.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception unwritable) when (unwritable is IOException or UnauthorizedAccessException)
        {
            // Kept for this run; see the remarks on the type.
        }
    }
}
