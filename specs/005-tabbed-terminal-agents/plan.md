# Implementation Plan: A tabbed terminal, and agents added or removed from it

**Branch**: `005-tabbed-terminal-agents` | **Date**: 2026-09-04 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/005-tabbed-terminal-agents/spec.md`

## Summary

`parley top` is rewritten from a sequence of prompts into a windowed surface — projects, agents,
conversations and a transcript on one screen, focus moving between them with Tab or the mouse —
and gains the two things 004 could not do to a project: make a repository one of its agents, and
withdraw one, with retire and purge each behind a description and the agent's name typed back.

The toolkit is Terminal.Gui 2.4.17, the owner's choice, and `Parley.Client` does not move: the
rewrite is confined to `src/Parley.Cli/Top` and its tests. The plan's substance is in three
findings the specification could not have made without reading the code:

- **An added agent cannot be `startable` within ten seconds, on two counts** (research R3). The
  node announces its roles once at join, so a role added to a running node's catalogue would never
  be started — that is fixed here, by the node watching its catalogue and rejoining. And the
  registry lists only agents that have registered themselves, which nothing on an operator's
  surface may do for them. So the specification is amended: the agents region shows the catalogue
  beside the registry, and the added agent appears as *catalogued, not yet registered*.
- **"The launch configuration names the project" means `PARLEY_PROJECT`** (R4), which `Parley.Mcp`
  already resolves and the enroll command does not yet write. It learns to.
- **One writer for the two files** (R5): the per-repository body of `parley enroll apply` is
  extracted so the surface and the command write and withdraw through the same code.

## Q1 settled — the specification asked for something the protocol does not provide

The constitution says a plan that contradicts the specification must say so in the specification
rather than in a commit message. It does, in two places, and this section is the argument.

**SC-003 and User Story 3's fourth scenario asked for the new agent to be listed as `startable`
within ten seconds.** SPEC.md §14 defines `startable` for an agent the registry holds, and §12.3
makes registration the adapter's own act on attach. An operator surface that registered an agent
on a repository's behalf would be a caller-supplied identity — constitution principle III, which
no feature may reintroduce by another name — and starting the agent once to make it register
would spend somebody's tokens with nobody watching, which is the consent the catalogue's
`unattended` flag exists to record. Neither is acceptable, and the ten-second promise cannot be
kept honestly.

**What is promised instead**: the agent appears in the agents region as *catalogued, not yet
registered* within ten seconds; it reads `startable` once its server has attached once. The
surface therefore reads the project's catalogue as well as the broker's log, and FR-006 is
amended to say so. The argument for allowing the second file: the catalogue is the one fact about
a deployment the broker never sees (§17 question 13 keeps what a machine can launch local on
purpose), so reading it is not a second answer to a question the broker answers. It is the only
answer to one the broker cannot.

The amendments are marked in `spec.md` with the date and a pointer here, so the owner can reverse
them by reverting one paragraph.

## Technical Context

**Language/Version**: C# / .NET 10, as the rest of the repository
**Primary Dependencies**: `Terminal.Gui` 2.4.17 (new, on `Parley.Cli`; twenty-four packages on its
graph, no advisories on 2026-09-04 — research R1); `Spectre.Console` and `Spectre.Console.Testing`
removed
**Storage**: none. This is a client; SPEC.md §4 gives the store to one broker process
**Testing**: xUnit v3 on Microsoft.Testing.Platform. Screens are driven through an `IApplication`
the test builds, with injected input and the rendered screen read back as text (R2)
**Target Platform**: Windows, Linux and macOS terminals — the three CI runs, verified locally on
Windows and under podman while CI is down
**Project Type**: terminal application inside the existing CLI tool
**Performance Goals**: an unreachable project reported within five seconds (SC-008); an added agent
visible within ten (SC-003 as amended); the toolkit's own frame rate is not a goal
**Constraints**: nothing by colour alone (SC-006); refuses a non-interactive terminal (FR-018);
opens no database (FR-006); every operator operation behind a description and a typed name,
enumerable from the source (FR-017)
**Scale/Scope**: one machine's projects; the deployment this was built on has nineteen agents
across one project and now several projects each with a few

## Constitution Check

*GATE: passed before Phase 0; re-checked after Phase 1 below.*

| Principle | Verdict | Why |
|---|---|---|
| I. The specification is the product | **Pass, with an amendment carried in the spec** | No protocol change: every read and write maps to an operation §10.2 already defines (contracts/operations.md). The one thing the spec asked for that the protocol cannot provide is amended in `spec.md` with the argument, not silently planned around. |
| II. No agent has authority over another | **Pass** | Retire, reinstate and purge are the operator's, taken by a person, on the Operators client; no agent reaches them. The node rejoining with new roles announces what a machine can launch, which is what it does today at start. |
| III. Identity is fixed by the launch | **Pass, and it decided Q1** | Adding writes `PARLEY_AGENT` and `PARLEY_PROJECT` into the launch environment, which is where identity comes from. Nothing registers on a repository's behalf. |
| IV. Terseness is a security control | **Pass** | Nothing new crosses between agents. The one new text an agent's launch carries, `PARLEY_PROJECT`, is a name the manifest already validates. |
| V. Invariants live in storage | **Not engaged** | Nothing new is stored. |
| Only question and answer text crosses | **Pass** | The reads are the operator's grant under §10.2, through the Operators client only. |
| Operator and agent operations are different surfaces | **Pass** | FR-019; the two clients stay two, and `SurfaceTests` confines the three admitted operations to one file. |
| `Parley.Core` has no I/O | **Not engaged** | Nothing changes in Core. |

No violation. The one entry in Complexity Tracking is a dependency, not a principle.

## Project Structure

### Documentation (this feature)

```text
specs/005-tabbed-terminal-agents/
├── plan.md              # this file
├── research.md          # Phase 0 — ten decisions and what they rest on
├── data-model.md        # Phase 1 — what the surface holds, and the two files it now touches
├── quickstart.md        # Phase 1 — the journey, end to end
├── contracts/
│   ├── operations.md    # which operations, on which client, behind which door
│   └── surface.md       # regions, keys, and the words every state uses
└── tasks.md             # /speckit-tasks, not this command
```

### Source Code (repository root)

```text
src/Parley.Cli/
├── Top/                          REWRITTEN on Terminal.Gui. One IApplication, passed in.
│   ├── TopCommand.cs             entry: interactivity check, directory, seat book, the app
│   ├── Shell.cs                  the four regions, the status line, side-by-side or stacked (R10)
│   ├── ProjectsRegion.cs         004's ProjectList + ProjectMenu + Naming + Removing, as a region
│   ├── AgentsRegion.cs           004's AgentsView + Seating, joined to the catalogue (data-model)
│   ├── ConversationsRegion.cs    004's ConversationList
│   ├── TranscriptRegion.cs       004's TranscriptView + Waiting + Answering + Composer
│   ├── Adding.cs                 add an agent: prompt, validation, what will be written, confirm
│   ├── Withdrawing.cs            THE ONE FILE that may call RetireAsync / ReinstateAsync / PurgeAsync
│   ├── Removing.cs               destroy a project — unchanged in rule, redrawn as a dialog
│   ├── LogTail.cs                follow logic unchanged; drawn into a view
│   ├── Onboarding.cs             the enrolment manifest dialog; still calls EnrollCommand
│   └── Launch.cs, Safe.cs        as today (Safe now escapes for the toolkit, not for markup)
├── Enrolment.cs                  NEW: Add and Withdraw for one repository, extracted from
│                                 EnrollCommand.ApplyAsync and called by it (R5)
├── EnrollmentPlan.cs             Describe gains an optional project name → PARLEY_PROJECT (R4)
├── EnrollCommand.cs              calls Enrolment; passes the resolved project when it has one
└── NodeCommand.cs                watches its catalogue and rejoins with new roles (R3)

src/Parley.Client/                UNCHANGED. Nothing here moves or grows.

tests/Parley.Cli.Tests/
├── Top/                          REWRITTEN alongside. Every 004 journey replayed (SC-002).
│   ├── Driving.cs                builds the IApplication, injects keys and mouse, reads the screen
│   ├── SurfaceTests.cs           three operations confined to Withdrawing.cs; three still forbidden
│   ├── AddingTests.cs, WithdrawingTests.cs, CatalogueJoinTests.cs, LayoutTests.cs    new
│   └── ...                       004's test files, each re-driven through Driving
├── EnrolmentTests.cs             Add and Withdraw are inverses; PARLEY_PROJECT written and refused
└── NodeCatalogueWatchTests.cs    a role added to the file is announced without a restart

Directory.Packages.props          Terminal.Gui 2.4.17 in; both Spectre packages out; comment says
                                  what the graph now carries (R1)
Directory.Build.props             VersionPrefix → 0.17.0
```

**Structure Decision**: no new project. The surface stays a folder in the tool that carries every
other subcommand (004's R5), and the one new type outside that folder, `Enrolment`, sits beside
the command it was extracted from. `Parley.Client` is untouched, which is what the split in 004
was for and is the check SC-005 rests on.

**Versioning**: the minor moves. `parley top` changes wholesale, a package the tool shipped is
gone, and `parley node` behaves differently when its catalogue changes. 0.17.0, in the first
commit that alters `src`, per CLAUDE.md.

## Phases

**Phase 0 — research.** Done: `research.md`. The two spec amendments it forced are in `spec.md`.

**Phase 1 — design.** Done: `data-model.md`, `contracts/`, `quickstart.md`, and the agent context
in `CLAUDE.md` pointing here.

**Phase 2 — tasks.** `/speckit-tasks`. Three constraints on their order, from the research:

1. **The spike is first and gates everything.** R2 leaves one thing open — which component
   factory runs the toolkit without a console — and a rewrite built before that is known is a
   rewrite that might not be testable. The spike injects a key, sees a view take it, reads the
   screen back as text, on Windows and under podman. Nothing else starts until it passes.
2. **Parity before addition.** Every 004 journey runs on the new surface (SC-002) before
   `Adding.cs` or `Withdrawing.cs` exists. The rewrite must not cost a thing 004 delivered, and the
   only way to know is to replay 004's tests before the surface grows.
3. **The node change and the enrolment extraction are independent of the toolkit** and can land
   first, each with its own tests: `NodeCommand` rejoining on a catalogue change, `Enrolment.Add`
   and `Withdraw` as inverses, `Describe` writing `PARLEY_PROJECT`. They are the half of this
   feature that ships behaviour outside the surface, and they are the half most worth probing
   adversarially — a catalogue edited to point one role at another workspace, a `.mcp.json` whose
   `parley` entry names a project that no longer exists.

The review gate applies as CLAUDE.md states it: three consecutive clean runs per lens after the
last edit, logged in `REVIEW-LEDGER.md`, with `SurfaceTests` as the floor for FR-017.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| A dependency with a 24-package graph, where the one it replaces had none | The owner's decision, made after using the prompt-driven surface on a real deployment: a windowing toolkit is what makes four regions, focus and a mouse possible at all | Spectre.Console's `Live`/`Layout` can draw panes but has no focus model, no widgets and no mouse; building those on it is writing a toolkit. `System.Console` directly is the same, worse. The cost is recorded in `Directory.Packages.props` beside the pin, and `NuGetAudit` prices it on every build. |
