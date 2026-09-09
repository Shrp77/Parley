# Research: A repository may answer in more than one project

**Feature**: `009-multi-project-enrolment` | **Date**: 2026-09-09

Eight decisions. Each was reached by reading the code that exists rather than by reasoning about
what it probably does, and each names what it read.

---

## R1 — The key rule is one type, and nothing outside one project can reach a launch configuration

**Decision**: a new `LaunchKey` in `src/Parley.Setup` owns the whole vocabulary — building a key from
a project, recognising a key as one this tooling writes, and reading a project back out of one. Every
caller is already inside `Parley.Setup`.

**Rationale**: the "six surfaces" of the spec turn out to be two writers and four illustrations, which
is what makes one implementation possible rather than aspirational. Checked by grep, not assumed:

| Reader | What it does with `.mcp.json` |
|---|---|
| `parley enroll scan/plan/apply` | `EnrollCommand`, `EnrollmentPlan`, `Enrolment` — all in `Parley.Setup` |
| `parley-top` adding and withdrawing | `Parley.Top/Adding.cs` and `Withdrawing.cs` call `Enrolment.AddAsync` / `WithdrawAsync`; neither parses JSON |
| The web console | **Never opens one.** `Register.cshtml` prints an example as text; `Withdrawing` there is retire/reinstate/purge, which are registry operations |
| README, `plugins/parley/commands/setup.md`, `Register.cshtml` | Illustrations shown to a person |

So the literal string `"parley"`-as-a-server-key appears today in three files and nine places —
`Enrolment.cs` (the `ServerKey` constant and two uses), `EnrollmentPlan.cs` (four), `EnrollCommand.cs`
(two) — and must appear in one.

**Alternatives considered**: putting it in `Parley.Configuration`, which both tools already reference —
rejected because `Parley.Mcp` references that project, and an agent's own server would then carry the
vocabulary of the file that launches it. The same argument that put `Parley.Setup` where it is (007,
R8).

**Enforced rather than intended**: a test in the `SurfaceTests` shape — the literal appears in exactly
one file, and a helper merely *named* for it fails. That check has already caught a violation once in
this repository, which is the reason for preferring it to a convention.

---

## R2 — Ownership of a key and ownership of an entry are two questions

**Decision**: `LaunchKey.IsOurs(key)` answers *may this tooling write here* — the key is `parley`, or
`parley-` followed by a valid project name. `Enrolment.NamedBy(entry)` answers *is this ours to
remove* — it carries `PARLEY_AGENT`. Writing consults the first, removing consults both.

**Rationale**: they genuinely differ, and conflating them is how the fourth silent overwrite would
happen. A hand-written server called `parley-alpha` occupies a key this would like and is not this
tooling's entry; FR-009 refuses it, which is only expressible if the two questions are separate.
`NamedBy` already carries the argument for the second in its remarks — *"Null and 'somebody else' are
deliberately different answers"* — and this feature does not change it.

**Alternatives considered**: deciding ownership from `command == "parley-mcp"`. Rejected: the command
is what a person edits when they run the adapter from a build directory, and an entry with a changed
command is still their agent. The environment variable is the identity, per SPEC.md §16.

---

## R3 — Finding the entry for a project has a precedence, and it must be written down

**Decision**: `LaunchEntries.For(servers, project)` returns the entry to update, in this order:

1. the key `parley-<project>`;
2. the key `parley` whose `PARLEY_PROJECT` is that project — **the legacy entry, updated in place and
   never renamed**;
3. the key `parley` that pins no project at all, when a project is being named — the re-point of
   FR-007, which says so out loud as it does today;
4. nothing, and a new `parley-<project>` entry is written.

**Rationale**: without an order, case 2 and case 4 both match a repository already in `alpha` and the
choice between them is whether a live agent's tool names change. Case 3 must come after case 2 or an
unpinned entry could swallow an enrolment meant to be a second one.

**Alternatives considered**: migrating case 2 to the new key and leaving a note. Rejected in the spec
and confirmed here: the key is a component of every tool name the client exposes
(`mcp__parley__parley_ask`), so a rename edits the vocabulary of a working agent to change nothing
about what it can do.

---

## R4 — FR-008 resolves an address through the manifest, and `Describe` stays pure

**Decision**: `EnrollmentPlan.Describe` gains a parameter carrying *project name → endpoint* for the
machine's manifest. It does no I/O. Its callers — `Enrolment.AddAsync` and `EnrollCommand` — read the
manifest they already hold and pass it.

**Rationale**: `Describe` takes file contents as strings and returns file contents as strings, which
is why it is the most heavily tested type in the project. Reaching a manifest from inside it would
make every one of those tests need a filesystem. The three outcomes of FR-008 are then a pure function
of an address and a map: same project → refuse as a duplicate; different project → proceed as an
addition; unknown address → refuse and name it.

**Alternatives considered**: keeping today's blanket refusal. Rejected because it is the one shape that
would block the feature entirely for a deployment that pins by address, and the manifest makes the
answer decidable rather than a guess — which is the objection the existing remark actually raises.

---

## R5 — `parley enroll` has no project today, and that is a gap this feature must close

**Decision**: `EnrollmentManifest` gains `project`, and `parley enroll apply` gains `--project <name>`,
resolved with the precedence `node_config` already documents. Without either, the run behaves exactly
as it does now: key `parley`, no `PARLEY_PROJECT`, which is FR-003 rather than a special case.

**Found by reading**: `EnrollCommand.cs:803` calls `EnrollmentPlan.Describe` with **four arguments** —
it never passes a project. Only `Enrolment.AddAsync`, which the terminal calls, passes one. So today
the CLI writes unpinned entries and only `parley-top` can enrol into a named project. Left alone, this
feature would have been terminal-only, and the spec's User Story 1 is written in the shell.

---

## R6 — FR-013 is checked where the catalogue is written, using the comparison §4.1.1 defines

**Decision**: before a catalogue entry is written, the other projects' catalogues named by the manifest
are read and searched for the same workspace, compared with `WorkspacePath.SameRepository`. A match is
refused, naming both projects, the workspace and the other catalogue's file.

**Rationale**: the comparison already exists in `Parley.Core` and is the one SPEC.md §4.1.1 specifies —
textual, and explicitly forbidden from consulting a filesystem, because the broker performing it need
not be on the machine whose paths it compares. Writing a second comparison here would be two answers to
one question.

The check belongs at the write rather than in `ProjectManifest.Validate` because validation is a pure
function over the manifest and a workspace is not in the manifest — it is in each project's catalogue,
which is a separate file per project. `ProjectManifest` already says why the catalogue cannot carry this
itself: *"the one thing SPEC.md §4.1's isolation cannot supply from storage because the catalogue is not
in the store."*

**Why refused rather than warned**: `ResidentAgent.cs:157` sets `WorkingDirectory = workspace`. Two
nodes starting one workspace is two models editing one working tree with no coordination between them.
A launch entry is inert until a person opens the repository; a catalogue entry is standing consent to
start without one, and that is the half that must stay singular.

---

## R7 — The scan answers with a list, because a repository now has several answers

**Decision**: the two `EnrollCommand` readers that ask *what is this repository already* return every
enrolment they find rather than the first. The committed-history reader (`git show <commit>:.mcp.json`)
does the same.

**Rationale**: both read `["mcpServers"]["parley"]["env"]` by literal key today, so on a repository
enrolled by this feature they would report *nothing*, and a scan that reports a multiply-enrolled
repository as unenrolled is how somebody enrols it twice in one project. This is the same failure mode
as the `--console` reader of 007, one layer down.

---

## R8 — SPEC.md does not change, and the check is recorded rather than the conclusion

**Decision**: no change to `SPEC.md`, and no new `Parley.Spec.Tests` case.

**Rationale**: `grep -cin "mcp.json\|mcpServers\|server key" SPEC.md` is **0**. The document mentions
"launch configuration" twice, neither time giving it a shape. The protocol has no concept of a project
(§4.1) and none of a file, so nothing in this feature is protocol.

The three places a person is shown a shape — README, the plugin's `setup.md`, and the console's
`Register` page — are documentation of this implementation and do change, under FR-014.
