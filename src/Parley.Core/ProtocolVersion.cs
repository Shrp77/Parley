namespace Parley.Core;

/// <summary>
/// Which version of the Parley protocol this implementation speaks.
/// </summary>
/// <remarks>
/// <para>
/// Not the version of these packages, and the distinction is the reason this exists. SPEC.md is
/// what another implementation builds against, and alternative implementations are expected — so
/// a fix to the operator's command-line tool ships a new package and must not suggest that the
/// wire contract moved. Three numbers, three lifetimes: the package version in
/// <c>Directory.Build.props</c>, this, and the storage schema's own in <c>user_version</c>.
/// </para>
/// <para>
/// Major and minor only, because a protocol has no patch level: either a change is visible to
/// something on the other side of the boundary or it is not a protocol change at all. While the
/// major is zero the minor carries breaking changes, which is what SPEC.md's own header says
/// about its state.
/// </para>
/// <para>
/// Kept here rather than read from the document at run time, so an implementation that ships
/// without SPEC.md beside it can still say what it speaks. <c>SpecConsistencyTests</c> binds the
/// two, so the constant and the document cannot drift apart in a build that passes.
/// </para>
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>The protocol version, as SPEC.md declares it.</summary>
    public const string Current = "0.1";
}
