using System.Text;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// Writing an accepted ownership line into a repository's launch configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only thing that makes an accepted line reach anybody.</b> The registry's
/// <c>owns</c> is filled from the launch environment when an agent attaches (SPEC.md §12.3) and no
/// operation writes it, so a line a person agreed to is a record and nothing else until it is in the
/// file the agent's server is started with. <c>record_ownership</c> stores the decision; this puts it
/// where the decision takes effect.
/// </para>
/// <para>
/// <b>It reuses enrolment's own hold-and-overwrite rather than writing the file itself</b>, which is
/// what FR-018 means by the one component that already writes those files: the exclusive handle, the
/// refusal to follow a symlink, the byte-order mark surviving a round trip, and cancellation being
/// honoured before the write and never during it are all rules that were paid for once and must not
/// be reimplemented beside them.
/// </para>
/// </remarks>
public static class OwnershipLine
{
    /// <summary>UTF-8 with no byte-order mark, for a file that had none.</summary>
    private static readonly UTF8Encoding Plain = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8 with a byte-order mark, for a file that had one.</summary>
    /// <remarks>
    /// Which of the two is used is decided by reading the file, never by preference: a round trip that
    /// added or dropped a mark would rewrite a file somebody's editor then reports as changed for a
    /// reason nothing explains.
    /// </remarks>
    private static readonly UTF8Encoding Marked = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Records the line in a repository's launch configuration.</summary>
    /// <param name="workspace">The repository holding the agent.</param>
    /// <param name="project">The project the entry belongs to, or null for the unnamed key.</param>
    /// <param name="agent">The agent the entry must name, checked before anything is written.</param>
    /// <param name="statement">The line, or null to take it out again.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>Null when it was written, or why it was not.</returns>
    /// <remarks>
    /// Returns rather than throws because every caller puts the reason in front of a person, and a
    /// refusal here is an ordinary answer: a repository that moved, a machine that is not this one, a
    /// file somebody else is holding.
    /// </remarks>
    public static async Task<string?> WriteAsync(
        string? workspace,
        string? project,
        AgentName agent,
        string? statement,
        CancellationToken cancellationToken = default)
    {
        if (NotHere(agent) is { } elsewhere)
        {
            return elsewhere;
        }

        if (statement is not null && !OwnershipStatement.IsValid(statement, out var wrong))
        {
            return wrong;
        }

        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return $"the registry says '{agent.Value}' runs in " +
                   $"{(string.IsNullOrWhiteSpace(workspace) ? "no recorded directory" : workspace)}, " +
                   "and there is no repository there. Nothing has been written. Re-enrol it if it moved.";
        }

        var path = Path.Combine(workspace, LaunchConfiguration.FileName);

        FileStream? held;

        try
        {
            held = await Enrolment.HoldAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException busy)
        {
            return $"could not take {path}: {busy.Message} Nothing has been written.";
        }

        using (held)
        {
            if (held is null)
            {
                return $"there is no {LaunchConfiguration.FileName} in {workspace}, so this agent is " +
                       "not enrolled from here. Nothing has been written.";
            }

            var text = await Enrolment.ReadHeldAsync(held, cancellationToken).ConfigureAwait(false);
            var mark = Enrolment.HeldHasByteOrderMark(held);

            JsonObject root;

            try
            {
                root = JsonNode.Parse(text) as JsonObject
                       ?? throw new InvalidDataException("the file is not a JSON object.");
            }
            catch (Exception unreadable) when (unreadable is System.Text.Json.JsonException or InvalidDataException)
            {
                return $"{path} is not readable as a launch configuration: {unreadable.Message} " +
                       "Nothing has been written.";
            }

            var servers = root["mcpServers"] as JsonObject;
            var (key, existing) = LaunchEntries.Landing(servers, project);

            if (servers is null || existing is null)
            {
                return $"{path} has no entry for " +
                       (project is null ? "this agent" : $"project '{project}'") +
                       ". Nothing has been written.";
            }

            // **005's review finding, reached from the other side.** A withdrawal once deleted a launch
            // configuration belonging to a *different* agent, because the workspace came from the
            // registry -- which is what the agent said about itself and may be long out of date, and a
            // repository re-enrolled under a second name has one file and two rows pointing at it. So
            // the name is read back out of the file before anything is written to it.
            if (!Names(existing, agent))
            {
                return $"{path} configures " +
                       (existing.Agent is null ? "no agent" : $"'{existing.Agent}'") +
                       $" under '{key}', not '{agent.Value}'. Nothing has been written.";
            }

            if (servers[key] is not JsonObject entry)
            {
                return $"{path} has no entry at '{key}'. Nothing has been written.";
            }

            if (entry["env"] is not JsonObject environment)
            {
                environment = [];
                entry["env"] = environment;
            }

            if (statement is null)
            {
                environment.Remove(EnrollmentPlan.OwnsVariable);
            }
            else
            {
                environment[EnrollmentPlan.OwnsVariable] = statement;
            }

            await Enrolment.OverwriteHeldAsync(
                held,
                path,
                root.ToJsonString(SerialisationOptions.Indented) + "\n",
                cancellationToken,
                mark ? Marked : Plain).ConfigureAwait(false);

            return null;
        }
    }

    /// <summary>Where the launch configuration for an agent would be.</summary>
    /// <param name="workspace">The repository.</param>
    /// <returns>The path.</returns>
    public static string PathIn(string workspace) =>
        Path.Combine(workspace, LaunchConfiguration.FileName);

    /// <summary>Why this machine cannot write for an agent, or null when it can.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The refusal, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>One rule for both surfaces, and not a restriction on the web UI.</b> A launch configuration
    /// is a file in a repository on the machine the agent runs on, and neither surface is guaranteed
    /// to be there: a browser is frequently not on the machine hosting the broker, and
    /// <c>parley terminal</c> reads a deployment that spans machines. Without this, a surface would
    /// compose a path that does not exist locally — or worse, write over an unrelated file that
    /// happens to sit at the same path.
    /// </para>
    /// <para>
    /// Compared against <see cref="MachineName.Current"/> — the machine half of an agent's name is
    /// detected and never configured (§4.1.1), so this is the same comparison <c>AgentFleet</c> makes
    /// and not a second idea of what machine this is. The refusal names the machine, so somebody is
    /// told where to go rather than that it did not work.
    /// </para>
    /// </remarks>
    public static string? NotHere(AgentName agent)
    {
        var here = MachineName.Current;

        if (string.IsNullOrEmpty(here))
        {
            return "this machine's name could not be read, so whether it is the one holding that " +
                   "repository cannot be decided. Nothing has been written.";
        }

        return string.Equals(agent.Machine, here, StringComparison.Ordinal)
            ? null
            : $"'{agent.Value}' runs on {agent.Machine} and its launch configuration is a file there. " +
              $"This machine is {here} and cannot write it. Accept it from a surface running on " +
              $"{agent.Machine}.";
    }

    /// <summary>Whether an entry names the agent being written for.</summary>
    /// <param name="entry">The entry found for the project.</param>
    /// <param name="agent">Who it should be.</param>
    /// <returns><see langword="true"/> when the entry is that agent's.</returns>
    /// <remarks>
    /// The entry records a role, and the registry records <c>role@machine</c> — the adapter appends
    /// the machine on attach. Comparing the whole name against a role would refuse every correct
    /// entry, so the role is what is compared, and the machine half was already settled by
    /// <see cref="NotHere"/>.
    /// </remarks>
    private static bool Names(LaunchEntry entry, AgentName agent) =>
        entry.Agent is { } named
        && (string.Equals(named, agent.Role, StringComparison.Ordinal)
            || string.Equals(named, agent.Value, StringComparison.Ordinal));
}
