using System.Text.Json.Serialization;
using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// One repository in an enrollment manifest.
/// </summary>
/// <param name="Workspace">Absolute path to the repository.</param>
/// <param name="Agent">The name peers will address it by.</param>
/// <param name="Owns">What it owns and what it does not, or empty when nobody has said yet.</param>
/// <param name="Unattended">Whether a peer's question may start it with nobody watching.</param>
/// <remarks>
/// <para>
/// <b><paramref name="Owns"/> arrives empty and is meant to.</b> The manifest exists so that a
/// person writes these lines once, in one file, rather than being asked eleven times in eleven
/// sessions — not so that anything can compose them. Nothing in this program writes one, and an
/// empty value omits the key rather than leaving a placeholder that reads as a claim somebody
/// made.
/// </para>
/// <para>
/// <b><paramref name="Unattended"/> defaults to false</b> because listing a repository is the
/// consent, not a description of it: it says a node may start that repository when a peer asks
/// it something, which spends somebody's tokens with nobody watching. Withdrawing it later means
/// removing the entry, nothing else and nowhere else.
/// </para>
/// </remarks>
public sealed record EnrollmentEntry(
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("owns")] string? Owns,
    [property: JsonPropertyName("unattended")] bool Unattended)
{
    /// <summary>
    /// Whether this repository is to be enrolled at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>False by default, and that is the whole of the field.</b> Scanning a directory finds
    /// every checkout under it, and most are usually not meant to be agents. Enrolling what was
    /// merely found would write a launch configuration and a peers block into repositories
    /// nobody considered.
    /// </para>
    /// <para>
    /// It cannot be inferred from <see cref="Owns"/> being filled in, which is the tempting
    /// shortcut: an empty <c>owns</c> is a legitimate state — SPEC.md §17 question 10 allows an
    /// agent with no line, listed with none, and askers fall back to its name — so a blank field
    /// would otherwise mean both "this is a peer whose boundary nobody has agreed" and "I never
    /// looked at this row".
    /// </para>
    /// </remarks>
    [JsonPropertyName("enroll")]
    public bool Enroll { get; init; }

    /// <summary>
    /// Whether a person has read <see cref="Owns"/> and said yes to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPEC.md §17 question 10, as amended, lets a tool put a suggested line in front of
    /// somebody — on condition that <b>approval is a recorded positive act, not a default</b>.
    /// This is that record.
    /// </para>
    /// <para>
    /// <b>It said "and enrolling refuses a non-empty <see cref="Owns"/> that does not carry it",
    /// which stopped being true of every reachable path.</b> That refusal belonged to the worksheet:
    /// a person typed a line into a manifest and ticked a box beside it, so a recorded yes was what
    /// separated a draft nobody read from one somebody weighed. FR-040 removed the file, and with it
    /// the only place the box could be ticked — so <c>EnrolAsync</c> asks
    /// <c>EnrollmentPlan.Validate</c> not to enforce it, and every repository whose launch
    /// configuration already carries an owns line would otherwise be unenrollable.
    /// </para>
    /// <para>
    /// The field is kept because the promise is still the right one and 020 is what makes it true
    /// again: an agent is <i>asked</i> what it is responsible for and the answer is discussed to
    /// agreement, which is a recorded positive act by the only party that actually knows.
    /// </para>
    /// <para>
    /// It is what keeps the field's claim true after the amendment. What the registry now
    /// promises is not that a person composed the line but that a person read it and assented,
    /// and a default of <see langword="true"/> would make that promise vacuous — a draft nobody
    /// looked at would be indistinguishable from one somebody weighed.
    /// </para>
    /// </remarks>
    [JsonPropertyName("owns_approved")]
    public bool OwnsApproved { get; init; }

    /// <summary>
    /// What the suggestion was drawn from, each piece quoted with where it came from.
    /// </summary>
    /// <remarks>
    /// Condition 1 of the amendment: a suggestion travels with its evidence so that what is
    /// being approved is checkable against the material behind it rather than against nothing.
    /// Gathered mechanically — a README line, the top-level directories, an earlier
    /// registration in the repository's own history — and never summarised, because a summary
    /// is one more thing to take on trust.
    /// </remarks>
    [JsonPropertyName("_evidence")]
    public IReadOnlyList<string>? Evidence { get; init; }
}

// **`EnrollmentManifest` was here: the file `enroll scan` wrote and `enroll apply` read.** It
// carried the rows, a block of guidance for whoever filled it in, and a `node_config` naming where
// consent should land.
//
// FR-040 removed it. Scanning built the rows in memory and applying deserialised them straight
// back, so the file was a round trip through the disk between two halves of one operation, and the
// one thing it bought was somewhere for a person to type what each repository owns — which 020 asks
// the agent instead of having somebody answer from memory.
//
// Its `node_config` was the only other thing lost, and that is a simplification rather than a
// reduction: a catalogue is still named explicitly with `--node-config`. What the field added was a
// *second* explicit source, and with one source there is no precedence question left to get wrong.

/// <summary>
/// What enrolling one repository would do, and the file contents it would write.
/// </summary>
/// <param name="Notes">One human-readable line per decision.</param>
/// <param name="LaunchConfiguration">New <c>.mcp.json</c> contents, or null when it is already correct.</param>
/// <param name="Instructions">New standing-instructions contents, or null when the file is left alone.</param>
/// <param name="AddsNodeEntry">Whether this repository would gain a <c>node.json</c> entry.</param>
/// <param name="Identity">
/// Which agent this repository is: the one it already declared, or the one this enrolment mints for it.
/// </param>
/// <remarks>
/// <b><see cref="Identity"/> is reported rather than passed in, because the repository is where it lives</b>
/// (018 R2). A checkout that already carries one keeps it — that is what makes a move or a rename cost
/// nothing — and only a checkout carrying none gets a new one. The caller needs it because the catalogue is
/// keyed by it, and deriving it a second time from the same file is how two readers come to disagree.
/// </remarks>
public sealed record RepositoryPlan(
    IReadOnlyList<string> Notes,
    string? LaunchConfiguration,
    string? Instructions,
    bool AddsNodeEntry,
    AgentIdentity Identity = default);
