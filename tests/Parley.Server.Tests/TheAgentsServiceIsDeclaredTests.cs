using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the <c>Agents</c> service, which every adapter binds, offers exactly the §10.1 operations written down here.
/// </summary>
/// <remarks>
/// <para>
/// In process, <c>SurfaceBoundaryTests</c> makes a new public method on the broker a decision. On the wire nothing did: the
/// per-operation guards (<c>ArchiveBoundaryTests</c> for archive, destroy, start and force-conclude; <c>FreezingAnAgentTests</c>
/// for freeze) name the operator operations that existed when each was written, and none names the ones SPEC.md §10.2 gained
/// since: <c>stop</c>, <c>restart</c>, <c>kill</c>, <c>locate_exchange</c> and <c>read_shared_file</c>. <c>Kill</c> added to
/// <c>service Agents</c> and served by <c>AgentsService</c> passed every boundary test, though it lets an agent end another
/// agent's process.
/// </para>
/// <para>
/// Listed rather than matched by word, as the broker's surface is: the point is that an addition is a decision about which
/// side of §10 it belongs on.
/// </para>
/// </remarks>
[Unit]
public sealed class TheAgentsServiceIsDeclaredTests
{
    private static readonly HashSet<string> Declared = new(StringComparer.Ordinal)
    {
        // The stream an adapter attaches on (§4.0).
        "Attach",

        // §10.1: the messages of §5.
        "Request", "Clarify", "Result", "Conclude", "DrainInbox", "Waiting",

        // §10.1: who may be addressed, without workspaces.
        "ListAgents",

        // §10.1 and §5.8: reading the past, recorded for the person and changing nothing.
        "ReadExchange", "SearchHistory",

        // §8.4: the adapter's count of what it refused before sending.
        "RecordRefusal",

        // §10.1 and §9.5: an agent's own files.
        "PlaceFile", "FetchFile", "FetchedFile", "ListFiles", "RemoveOwnFiles",

        // §10.1 and §5.7: the one roadmap operation an agent has, which stores a proposal and nothing else.
        "ProposeRoadmap",
    };

    [Fact]
    public void The_agents_service_offers_only_what_is_declared()
    {
        var offered = Wire.Agents.Descriptor.Methods.Select(method => method.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            offered.SetEquals(Declared),
            "Undeclared on Agents: " + string.Join(", ", offered.Except(Declared))
            + "; declared and gone: " + string.Join(", ", Declared.Except(offered)));
    }

    [Fact]
    public void The_operator_operations_this_feature_added_are_on_the_operators_service()
    {
        // Not vacuous: the operations the list keeps off Agents exist, on the other service.
        var operators = Wire.Operators.Descriptor.Methods.Select(method => method.Name).ToList();

        Assert.Contains("Stop", operators);
        Assert.Contains("Restart", operators);
        Assert.Contains("Kill", operators);
        Assert.Contains("LocateExchange", operators);
        Assert.Contains("ReadSharedFile", operators);
        Assert.Contains("Freeze", operators);
        Assert.Contains("Unfreeze", operators);
    }
}
