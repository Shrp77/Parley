namespace Parley.Core;

/// <summary>
/// Which version of the Parley protocol this implementation speaks.
/// </summary>
/// <remarks>
/// <para>
/// Not the version of these packages. SPEC.md is what another implementation builds against, so a fix
/// to the operator's command-line tool ships a new package and must not suggest that the wire contract
/// moved. Three numbers, three lifetimes: the package version in <c>Directory.Build.props</c>, this, and
/// the storage schema's own in <c>user_version</c>.
/// </para>
/// <para>
/// Major and minor only, because a protocol has no patch level: a change is either visible to something
/// on the other side of the boundary or it is not a protocol change at all. While the major is zero the
/// minor carries breaking changes.
/// </para>
/// <para>
/// A person's line to an agent is a Request, and the agent replying names, in <c>settles</c>, its own
/// questions to the person that the words settled; the broker closes them as <c>settled</c>. A Request
/// carries one ask and its conversation, and a Result answers the exchange whole under the item
/// <c>request</c>. Files cross between repositories through the broker, which computes each file's
/// SHA-256; what crosses about a file is a bounded name and a hash, never its contents in a model's
/// context. SPEC.md §18 carries the history.
/// </para>
/// <para>
/// Kept here rather than read from the document at run time, so an implementation that ships without
/// SPEC.md beside it can still say what it speaks. A test binds the two.
/// </para>
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>The protocol version, as SPEC.md declares it.</summary>
    public const string Current = "0.8";
}
