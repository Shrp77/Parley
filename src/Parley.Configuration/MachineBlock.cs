using Parley.Core;

namespace Parley.Configuration;

/// <summary>What one machine says differently for one of a repository's entries (SPEC.md §4.1.2).</summary>
/// <param name="Owns">Its ownership line, or null when it gives none.</param>
/// <param name="Broker">The address it reaches its broker at, or null when it gives none.</param>
/// <remarks>
/// <b>Two settings, and an empty block means something.</b> A block's presence says this machine is enrolled in that
/// project, which is how a withdrawal elsewhere knows to leave the entry in place. It holds no role, project, machine or
/// identifier, so which block applies can never depend on what a block says, and no workspace, because a reader has to
/// have found the file before it can read one.
/// </remarks>
public sealed record MachineBlock(string? Owns, string? Broker);

/// <summary>What one machine ends up with for one entry, with its block laid over the rest.</summary>
/// <param name="Owns">The ownership line, or null.</param>
/// <param name="Broker">The broker address, or null when nothing names one.</param>
/// <param name="BrokerFromBlock">Whether a block gave the address, which is the file saying so on purpose.</param>
public sealed record LaunchSettings(string? Owns, string? Broker, bool BrokerFromBlock);

/// <summary>The launch configuration a launch belongs to, found by looking for it.</summary>
/// <param name="Folder">The folder it was found in.</param>
/// <param name="Configuration">What it declares.</param>
/// <param name="Membership">The entry this launch is, or null when its machine section is unreadable.</param>
/// <param name="Problem">Why its machine section cannot be used, or null.</param>
public sealed record LocatedLaunch(
    string Folder,
    LaunchConfiguration Configuration,
    LaunchMembership? Membership,
    string? Problem)
{
    /// <summary>Finds the launch configuration for a launch.</summary>
    /// <param name="workspace">What <c>PARLEY_WORKSPACE</c> holds, if anything.</param>
    /// <param name="agent">What <c>PARLEY_AGENT</c> holds.</param>
    /// <param name="project">What <c>PARLEY_PROJECT</c> holds, if anything.</param>
    /// <param name="startFolder">The folder the launch started in.</param>
    /// <returns>The file, or null when no file has an entry for this launch.</returns>
    /// <remarks>
    /// <para>
    /// <b>An entry must name this launch, or the file is not this launch's.</b> That is what lets an old entry's path from
    /// another machine fail quietly here: nothing is there, or what is there is not this agent.
    /// </para>
    /// <para>
    /// <b>The walk stops at the first folder holding <c>.git</c> or a launch configuration</b>, because a client may
    /// start a server in a subfolder, and a repository's parents are none of this launch's business. A folder leading off
    /// this machine is never looked in: the lookup alone would offer the person's sign-in to that host.
    /// </para>
    /// </remarks>
    public static LocatedLaunch? Find(string? workspace, string? agent, string? project, string startFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startFolder);

        if (string.IsNullOrWhiteSpace(agent))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(workspace)
            && Directory.Exists(workspace)
            && !OffMachineLinks.LeadsOffThisMachine(workspace)
            && Consider(workspace, agent, project) is { } named)
        {
            return named;
        }

        DirectoryInfo? folder;

        try
        {
            folder = new DirectoryInfo(startFolder);
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (; folder is not null; folder = folder.Parent)
        {
            if (OffMachineLinks.LeadsOffThisMachine(folder.FullName))
            {
                return null;
            }

            if (File.Exists(Path.Combine(folder.FullName, LaunchConfiguration.FileName))
                || OffMachineLinks.LeadsOffThisMachine(Path.Combine(folder.FullName, LaunchConfiguration.FileName)))
            {
                return Consider(folder.FullName, agent, project);
            }

            var git = Path.Combine(folder.FullName, ".git");

            if (Directory.Exists(git) || File.Exists(git))
            {
                return null;
            }
        }

        return null;
    }

    private static LocatedLaunch? Consider(string folder, string agent, string? project)
    {
        var read = LaunchConfiguration.TryRead(folder, out var configuration, out var problem, out var machinesBroken);

        if (machinesBroken)
        {
            return new LocatedLaunch(folder, LaunchConfiguration.None, null, problem);
        }

        return read && configuration.EntryFor(agent, project) is { } membership
            ? new LocatedLaunch(folder, configuration, membership, null)
            : null;
    }

    /// <summary>What this machine ends up with.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="read">The launch environment.</param>
    /// <returns>The block laid over the environment.</returns>
    public LaunchSettings SettingsFor(string machine, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var block = Membership is null ? null : Configuration.BlockFor(machine, Membership.Key);

        return new LaunchSettings(
            block?.Owns ?? read(LaunchConfiguration.OwnsSetting),
            block?.Broker ?? read(BrokerEndpoint.Variable),
            block?.Broker is not null);
    }
}
