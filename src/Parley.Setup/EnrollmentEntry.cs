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
/// <b><paramref name="Unattended"/> says a node may start that repository when a peer asks it
/// something</b>, which spends somebody's tokens with nobody watching. Withdrawing it later means
/// removing the catalogue entry, nothing else and nowhere else.
/// </para>
/// <para>
/// <b>It used to default to false, and the paragraph here explained why: ticking it in the
/// worksheet was the consent.</b> 019 deleted the worksheet, and FR-043 reversed the rule —
/// enrolling *is* the consent, granted once at a door that names the count and says what being
/// startable costs. The default was left behind, so every repository enrolled from a folder
/// arrived with it false and the catalogue write in <c>EnrollmentPlan</c> never ran: enrolled,
/// addressable and unstartable, under a screen that said otherwise.
/// </para>
/// <para>
/// The field survives the reversal because it is still a question with two answers. A workspace
/// has one launch configuration and the catalogue is one per machine, so a checkout catalogued
/// once is startable in every project it answers in — which <c>Enrolment</c> says out loud when
/// an entry arrives beside one already there. What changed is who answers the question: the
/// person at the door, rather than a line in a file.
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
    /// Gathered mechanically — a README line, the top-level directories, and the projects the
    /// repository already answers in — and never summarised, because a summary is one more thing
    /// to take on trust. Read off disk only: the repository's history is not consulted.
    /// </remarks>
    [JsonPropertyName("_evidence")]
    public IReadOnlyList<string>? Evidence { get; init; }

    /// <summary>
    /// What a person needs to know about this repository before ticking it, or empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the row because two surfaces show it and only one of them knew any of this.</b>
    /// `EnrollCommand` worked the note out privately for its own picker — "worktree of another
    /// checkout", "already an agent", "has an .mcp.json" — and the terminal's picker composed its
    /// own from <c>Owns</c> alone, under a remark claiming it was "read off the row the command
    /// produced ... so the note and what the command will do cannot disagree". They disagreed: on
    /// the surface the owner actually enrols from, a worktree was offered with nothing said.
    /// </para>
    /// <para>
    /// <b>A worktree is the one worth the trouble.</b> It is the *same repository* at another path,
    /// and a folder of worktrees looks exactly like a folder of checkouts to a scan — so enrolling
    /// one gives a second agent to one codebase, and because <c>.mcp.json</c> is a tracked file the
    /// two agents' launch configurations are the same path on two branches. Whichever branch is
    /// checked out decides which agent exists.
    /// </para>
    /// <para>
    /// Reported and not refused: a worktree per branch with an agent on each is a reasonable
    /// arrangement somebody may mean. What must not happen is it going unsaid, because the cost
    /// arrives later as a launch configuration that changes under a branch switch.
    /// </para>
    /// <para>
    /// Not serialised: it is computed from the repository every time the directory is read, and a
    /// stored copy would be a fact about a checkout as it was.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public string Note { get; init; } = string.Empty;
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
