using System.Text;

namespace Parley.Client.Rendering;

/// <summary>How a message names the files the person attached to it (SPEC.md §9.5).</summary>
/// <remarks>
/// Placing tells nobody, so the message is what tells an agent a file is there. It names each as the broker spells it, with
/// its hash, which is what lets the agent fetch exactly what was meant.
/// </remarks>
public static class Attaching
{
    /// <summary>What a conversation's first message says when it has no words and its files have to come second.</summary>
    public const string FilesFollow = "Files follow in the next message.";

    /// <summary>One file as a form carries it between presses.</summary>
    /// <param name="name">The name the broker placed it under.</param>
    /// <param name="sha256">Its hash.</param>
    /// <returns>The entry.</returns>
    public static string Entry(string name, string sha256) => name + "|" + sha256;

    /// <summary>The name in an entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Split('|', 2)[0];
    }

    /// <summary>The message: the person's words, then the files.</summary>
    /// <param name="says">What the person wrote, if anything.</param>
    /// <param name="entries">The files, each as an <see cref="Entry"/>.</param>
    /// <returns>What to send; the words as written when there are no files.</returns>
    public static string Message(string? says, IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var files = entries.Select(entry => entry.Split('|', 2)).Where(parts => parts.Length == 2).ToList();

        if (files.Count == 0)
        {
            return says ?? string.Empty;
        }

        var message = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(says))
        {
            message.Append(says.TrimEnd()).Append("\n\n");
        }

        message.Append("Attached, to fetch with parley_file_fetch:");

        foreach (var file in files)
        {
            message.Append("\n- ").Append(file[0]).Append(" (sha256: ").Append(file[1]).Append(')');
        }

        return message.ToString();
    }
}
