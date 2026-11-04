using System.Text.Json.Nodes;
using Parley.Configuration;

namespace Parley.Setup;

/// <summary>A launch configuration as text, keeping the line endings the file already has.</summary>
/// <remarks>
/// The file is shared between machines, and a laptop and a Linux server are this feature's own example: a write in the
/// platform's own line endings would show every line as changed in the other machine's diff.
/// </remarks>
internal static class LaunchText
{
    /// <summary>Renders the file.</summary>
    /// <param name="root">What it holds now.</param>
    /// <param name="original">Its text before, or null when it is new.</param>
    /// <returns>The text to write.</returns>
    public static string Render(JsonObject root, string? original)
    {
        var newline = original?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";

        return root.ToJsonString(SerialisationOptions.Indented).ReplaceLineEndings(newline) + newline;
    }
}

/// <summary>Writes the machine section of a launch configuration (SPEC.md §4.1.2).</summary>
/// <remarks>
/// <b>Everything here changes one machine's block and nothing else</b>, because the file is shared and each machine
/// writes it from its own checkout. Reading and checking the section is <see cref="LaunchConfiguration"/>'s, since the
/// adapter and the node read it too; a writer that also validated would come to disagree with them.
/// </remarks>
internal static class MachineBlocks
{
    /// <summary>Whether a machine has a block for an entry.</summary>
    /// <param name="root">The file.</param>
    /// <param name="machine">The machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns><see langword="true"/> when it has one.</returns>
    public static bool Has(JsonObject root, string machine, string key) => Block(root, machine, key) is not null;

    /// <summary>A machine's block for an entry.</summary>
    /// <param name="root">The file.</param>
    /// <param name="machine">The machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>The block, or null.</returns>
    public static JsonObject? Block(JsonObject root, string machine, string key) =>
        (root[LaunchConfiguration.MachinesMember] as JsonObject)?[machine] is JsonObject blocks
            ? blocks[key] as JsonObject
            : null;

    /// <summary>A machine's block for an entry, made when it has none.</summary>
    /// <param name="root">The file.</param>
    /// <param name="machine">The machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>The block.</returns>
    public static JsonObject Ensure(JsonObject root, string machine, string key)
    {
        if (root[LaunchConfiguration.MachinesMember] is not JsonObject section)
        {
            section = [];
            root[LaunchConfiguration.MachinesMember] = section;
        }

        if (section[machine] is not JsonObject blocks)
        {
            blocks = [];
            section[machine] = blocks;
        }

        if (blocks[key] is not JsonObject block)
        {
            block = [];
            blocks[key] = block;
        }

        return block;
    }

    /// <summary>The machines other than this one holding a block for an entry.</summary>
    /// <param name="root">The file.</param>
    /// <param name="machine">This machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>Their names.</returns>
    public static IReadOnlyList<string> OthersHolding(JsonObject root, string machine, string key) =>
        root[LaunchConfiguration.MachinesMember] is JsonObject section
            ? [.. section
                .Where(pair => !string.Equals(pair.Key, machine, StringComparison.Ordinal)
                    && pair.Value is JsonObject blocks
                    && blocks[key] is not null)
                .Select(pair => pair.Key)]
            : [];

    /// <summary>Removes a machine's block for an entry, and whatever that leaves empty.</summary>
    /// <param name="root">The file.</param>
    /// <param name="machine">The machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns><see langword="true"/> when it had one.</returns>
    public static bool Remove(JsonObject root, string machine, string key)
    {
        if (root[LaunchConfiguration.MachinesMember] is not JsonObject section
            || section[machine] is not JsonObject blocks
            || !blocks.Remove(key))
        {
            return false;
        }

        if (blocks.Count == 0)
        {
            _ = section.Remove(machine);
        }

        if (section.Count == 0)
        {
            _ = root.Remove(LaunchConfiguration.MachinesMember);
        }

        return true;
    }

    /// <summary>The one entry a window onto a broker can mean on this machine, or null when that cannot be told.</summary>
    /// <param name="root">The file.</param>
    /// <param name="entries">Its entries.</param>
    /// <param name="machine">This machine.</param>
    /// <returns>The entry.</returns>
    /// <remarks>
    /// <b>A window's name is no project's</b>, so the only safe reading is a machine that answers through exactly one entry, and
    /// reaches a broker through it. Picking the one with an address from among several would overwrite another project's line.
    /// </remarks>
    public static LaunchEntry? ThroughAWindow(JsonObject root, IReadOnlyList<LaunchEntry> entries, string machine)
    {
        var here = entries.Where(entry => AnswersHere(root, machine, entry.Key)).ToList();

        return here.Count == 1 && Block(root, machine, here[0].Key)?[EnrollmentPlan.BrokerVariable] is not null
            ? here[0]
            : null;
    }

    /// <summary>Whether this machine still answers through an entry.</summary>
    /// <param name="root">The file.</param>
    /// <param name="machine">This machine.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>
    /// <see langword="true"/> when it has a block, or when no machine has one: an entry from before blocks belongs to
    /// whichever machine opens it.
    /// </returns>
    public static bool AnswersHere(JsonObject root, string machine, string key) =>
        Has(root, machine, key) || OthersHolding(root, machine, key).Count == 0;
}
