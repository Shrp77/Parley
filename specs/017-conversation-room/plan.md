# Implementation Plan: A conversation is a room

**Branch**: `015-file-store` (this repository does not branch per spec; 016 was written and built here too)
**Date**: 2026-09-21 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/017-conversation-room/spec.md`

## Summary

Replace the terminal's conversation view with a room: participants, outstanding work and agent status down a
narrow column; the transcript above a single input line in a wide one. The input line derives what it will do per
recipient from the pair state — Result where they asked and this person holds it, Clarify where this person asked
and is waiting, Request where nothing is open — and shows that, with its cost, before Enter. `@` and `/` complete
from one narrowing list; commands are declarations rather than branches.

**Nothing below the surface moves.** No protocol change, no schema change, no wire change: the pair state comes
from joining two operator reads the surface already makes (research R1), and every act used is one the seat can
already perform.

## Technical Context

**Language/Version**: C# 13 on .NET 10
**Primary Dependencies**: Terminal.Gui v2 (the terminal's own; pinned in `Directory.Packages.props`), Grpc.Net.Client via `Parley.Transport`
**Storage**: none reachable from here. `Parley.Terminal` and `Parley.Client` must not reach a store, checked by `GraphTests`' transitive walk
**Testing**: xUnit v3 on Microsoft.Testing.Platform; the terminal is driven headlessly through `Driving`/`Walking`/`Drawn`, and over a real loopback broker where the broker's answer is what matters
**Target Platform**: Windows, Linux and macOS terminals; the suite runs on all three in CI
**Project Type**: desktop/TUI client of a local broker
**Performance Goals**: the transcript already follows every 3s and the seat's mail every 5s; this adds one `DescribeWaiting` per tick and must not stack reads — one in flight at a time, as following already does
**Constraints**: no store on the graph; warnings are errors and every public member documented; the input line must not lose unsent text
**Scale/Scope**: one view replaced (~1,200 lines of terminal code touched), two new types in `Parley.Client`, and the conversation-view tests sorted per research R10

## Constitution Check

*GATE: must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle / boundary | Bearing on this feature | Verdict |
|---|---|---|
| I. The specification is the product | This changes a surface, not the protocol. SPEC.md needs no edit, and the plan asserts that rather than assuming it: every act used already exists in §5.1, §5.2 and §5.3 | **Pass** |
| II. No agent has authority over another | Nothing here lets one participant direct another. A person sends Requests as themselves, from a seat | **Pass** |
| III. Identity is fixed by the launch, never by a call | The seat is unchanged. FR-016 forbids sending under a seat the book no longer holds, which is the existing rule restated at a new call site | **Pass** |
| IV. Terseness is a security control | The room invites shorter, more frequent messages, and that is the one place this feature pushes against a principle. It is answered rather than waved past: the cap is unchanged and enforced in storage, and FR-009 makes the cost of a plural ask visible *before* it is paid, which is the first time this surface has done so | **Pass, argued** |
| V. Invariants live in storage | No invariant moves into the client. The act derivation decides what to *offer*; the broker still refuses anything wrong, and R1 deliberately reads the broker's own wait-for graph rather than reconstructing it | **Pass** |
| What crosses between agents is messages | FR-015: no recipient list, no addressing syntax and no routing reaches a payload. SC-003 checks it | **Pass** |
| Agent and operator operations are different surfaces | The room reads `Export` and `DescribeWaiting` (operator, §10.2) and sends through the seat's agent client (§10.1). R2 keeps the derivation in `Parley.Client`, which reaches both legitimately; `ClientSeparationTests` holds every path to the agents client to a granted seat | **Pass** |
| `Parley.Core` has no I/O | Untouched by this feature | **Pass** |
| A green build is the whole lint gate | Every public member documented; the two new `Parley.Client` types are public and carry `<remarks>` saying why | **Pass** |
| Probe; do not reason | The derivation's table has four states and one of them is not derivable. Each is probed, including the one that must refuse to guess | **Pass** |
| Review is three consecutive clean runs per lens | Owed after the last edit. Recorded honestly in the morning report if not reached | **Owed** |
| The repository is public | Hygiene grep before each commit; placeholders are `orders`, `billing`, `inventory` | **Pass** |

**No violation requires justification.** Principle IV is the one under tension and is argued rather than
dismissed: a room that makes chatter easy is a real risk to a context-poisoning defence, and the mitigation is
that the bill is shown before it is paid rather than found in `parley flags` the next day.

## Project Structure

### Documentation (this feature)

```text
specs/017-conversation-room/
├── plan.md              # This file
├── spec.md              # 40 requirements, 11 success criteria
├── research.md          # Ten decisions, R1 the load-bearing one
├── data-model.md        # The draft, the derivation, the declarations
├── quickstart.md        # The walk
├── contracts/
│   ├── composer.md      # What the input line does, per pair state
│   └── commands.md      # The command surface and its declarations
├── checklists/
│   └── requirements.md  # Spec quality, and the decisions settled while writing it
└── tasks.md             # /speckit-tasks output
```

### Source code

```text
src/Parley.Client/
├── Rendering/
│   ├── Reading.cs            # gains the message identifier per line (R7)
│   └── Palette.cs            # unchanged; already name-derived
└── Seating/
    ├── Composing.cs          # NEW: the act derivation and the draft (R1, R2)
    ├── Commands.cs           # NEW: command declarations and the table (R3)
    └── Completion.cs         # NEW: one narrowing list over a candidate set (R4, R5, R6)

src/Parley.Terminal/
├── Shell.cs                  # the Conversation view becomes three panes; the room's keys
├── RoomRegion.cs             # NEW: participants, outstanding, status
├── LineRegion.cs             # NEW: the input line, the preview and the completion list
└── TranscriptRegion.cs       # gains scroll-to-message (R7)

tests/Parley.Client.Tests/
├── ComposingTests.cs         # NEW: the four pair states, and the one that must not be guessed
├── CommandTableTests.cs      # NEW: declarations, doors, draft-vs-acting
└── CompletionTests.cs        # NEW: narrowing, derived state, prefix vs exact

tests/Parley.Terminal.Tests/
├── RoomTests.cs              # NEW: the layout, the sidebar, scroll-to-context
├── LineTests.cs              # NEW: the preview, Enter by active control, Tab, sending as text
└── (existing)                # sorted per research R10 before anything is changed
```

## Phasing

Built in spec priority order, each phase a commit that leaves the suite green.

1. **The derivation, headless.** `Composing` in `Parley.Client` with its tests: four pair states, the
   non-derivable one, and the payload carrying no routing. No terminal change yet, so the whole thing is testable
   before any layout exists.
2. **The room's layout.** Three panes for the Conversation view, the sidebar filled from the export and the
   waiting edges, the transcript where it was. Existing conversation-view tests sorted per R10 in this phase,
   because that is when they start failing.
3. **The line.** The input line, the preview, Enter by active control, and sending — one act per recipient.
4. **Completion and commands.** The narrowing list over both sigils, the declarations, `/add`, the near-miss
   note, and sending as text.
5. **Outstanding items and scroll-to-context.** The sidebar's list and the transcript anchor.
6. **Delivery.** Version bump, full suites from their own host executables, the hygiene grep, install.

## Risks, and what each costs

- **The test sort (R10) is the largest single risk.** Getting it wrong quietly loses a property that was found by
  probe. Mitigation: sort before changing, and record the pile each test went into.
- **Terminal.Gui layout with an input line at the bottom of one column.** The toolkit has cost this repository
  several cycles: a form taller than the screen clipped at the top, a tab strip that truncates silently, a region
  sized from its own words. Mitigation: the layout tests drive real widths, including forty columns.
- **Keys that must not reach past the list.** Up and down belong to the list while it is open and to whatever
  they did before once it closes, which is 0.20.3's fault in a new place — a key tested only in the negative is
  a key nobody has pressed.
- **The seat can go mid-draft.** FR-016, and the edge case list has it. Mitigation: re-check at the send, which
  is what 0.18.9 established as the rule after finding three call sites that captured the seat when a form opened.
