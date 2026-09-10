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
/// <b>0.4 made a notification wake its recipient.</b> Before it, a <c>notify</c> reached only an
/// agent that was already attached, so one addressed to a closed session was stored and read
/// whenever somebody next opened that repository — which for a repository nobody opens is never.
/// The bump is caused by the behaviour rather than by the field that reports it: an agent that
/// starts where it previously did not is observable from the other side of the boundary, which is
/// §18's whole test, and a protocol with no patch level has no slot for a compatible change. It is
/// bounded by coalescing rather than by a cap — attention is asked for once per recipient until
/// that recipient drains, because a drain collects everything pending.
/// </para>
/// <para>
/// <b>0.3 raised the message cap to 8,000.</b> A number rather than a mechanism, and it still
/// moves this: what a peer will accept is exactly the kind of thing a second implementation has
/// to agree about, and two builds both calling themselves 0.2 while disagreeing about it is what
/// this number exists to make impossible. It is the smallest change that can qualify, which is
/// why it is worth being explicit that it does.
/// </para>
/// <para>
/// <b>0.2 introduced the transport.</b> 0.1 was MCP over stdio against a shared file: there was
/// no wire, so there was nothing for a second implementation to speak and the number moved for
/// nothing. There is one now — <c>proto/parley.proto</c>, three services, and a held connection
/// that decides what reachable means — and every one of those is visible from the other side of
/// the boundary. See §18 for what changed.
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
    public const string Current = "0.4";
}
