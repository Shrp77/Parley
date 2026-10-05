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
/// <b>0.8 made a person's line a Request, and lets the agent say what it settled.</b> A surface no longer guesses
/// which of an agent's open questions a person's words answer; the agent replying names, in <c>settles</c>, its own
/// questions to the person that the words settled, and the broker closes them as <c>settled</c>.
/// </para>
/// <para>
/// <b>0.7 made a Request the ask.</b> The intent, the items with their kinds, identifiers and gates, and the
/// <c>done_when</c> are gone: a Request carries one ask and its conversation, and a Result answers the exchange
/// whole under the item <c>request</c>. A receiver that cannot tell how far an ask goes sends a Clarify, which
/// the protocol has always had. Messages stored under earlier versions keep their own fields and still read.
/// </para>
/// <para>
/// <b>0.6 lets files cross between repositories.</b> An agent places a <c>.md</c> or <c>.txt</c> file in
/// the file location beside the store and another fetches it onto disk in its own repository; the broker
/// computes each file's SHA-256, and an agent that fetched a file finds a change notice in its inbox when
/// the file changes or goes. What crosses about a file is a bounded name and a hash, never its contents
/// in a model's context.
/// </para>
/// <para>
/// <b>0.5 made Parley a protocol for work across repositories.</b> Question, Answer and Notify became
/// Request, Clarify and Result; every message is answered; the broker assigns the conversation; nothing
/// is orphaned or ghosted; a cycle is recorded rather than refused; and the envelope says a participant
/// may request work. §18 lists what a second implementation will observe.
/// </para>
/// <para>
/// <b>0.4 made a notification wake its recipient. 0.3 raised the message cap to 8,000. 0.2 introduced
/// the transport.</b> §18 carries the history.
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
