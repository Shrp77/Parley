# Implementation Plan: A terminal surface a person takes part from

**Branch**: `004-terminal-surface` | **Date**: 2026-09-02 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/004-terminal-surface/spec.md`

## Summary

A terminal application, `parley top`, that lets a person navigate a deployment, read its
conversations, and take part in them under their own name — without a browser and without an
editor session that nothing can wake.

The plan's substance is the answer to the spec's one open question, and the answer is smaller
than it looked: **almost everything the two surfaces would share is already presentation-free.**
Of the twelve types the web console holds outside its Razor pages, exactly one touches HTTP. So
the sharing is a move, not a rewrite, and the terminal is a second reader of the same code
rather than a second implementation of the same behaviour.

## Q1 settled — what is shared, and what stays

**Decision: extract the transport-facing half of `Parley.Web` into `src/Parley.Client`, and have
both surfaces reference it.**

The evidence, checked rather than assumed:

| Type | Touches HTTP? | Goes to `Parley.Client` |
|---|---|---|
| `BrokerDirectory`, `BrokerClient`, `ProjectHealth` | no | yes |
| `Seating/Seat`, `SeatBook`, `SeatHold`, `SeatVoice`, `Delivered` | no | yes |
| `Rendering/Transcript`, `Visibility`, `Roster`, `RosterView`, `Payload`, `Palette` | no | yes |
| `Seating/SeatCookie` | **yes** (`HttpRequest`) | no — stays in `Parley.Web` |
| `ConsoleHost`, `Program`, `Pages/`, `Brand/` | yes | no — stays in `Parley.Web` |

`Parley.Client` references `Parley.Transport` and `Parley.Configuration` — which is exactly what
`Parley.Web` references today, so **nothing lands on anyone's graph that was not already there**.
The constraint the spec was protecting holds by construction: there is no SQLite on the console's
graph, and `Parley.Client` cannot put one there because it has the same two references.

**Why a project rather than reuse in place.** `Parley.Cli` already references `Parley.Web`, so
the terminal could use `Parley.Web.Seating` today with no move at all. That is rejected for the
reason CLAUDE.md already records about `Parley.Configuration`, in almost these words: *a project
of its own because two programs read it and cannot otherwise share one reader*. Seating a person
and deriving a transcript are not web concerns; leaving them in an assembly called `Web` means
the next reader has to know that "Web" means "and also everything a client does", and a terminal
application taking a dependency on an ASP.NET Core assembly to find out who is in a conversation
is a boundary nobody would defend if it were proposed fresh.

**Why not the other two shapes.** Making the terminal thin and leaving the web console as the
interaction surface answers none of the spec's driver: the point is participation without a
browser. Letting the two implement independently is how they come to disagree about who was in a
conversation, which SC-003 forbids and which this repository has now shipped twice in one week —
a frame disagreeing with the page beneath it about a waiting count, and again about a seat.

**Consequence worth stating.** After the move, `Parley.Cli` still references `Parley.Web`,
because `parley console` starts Kestrel and that was decided deliberately. But the terminal's own
code path does not: it reaches `Parley.Client`, and a future decision to split the tool would find
the seam already cut.

## Technical Context

**Language/Version**: C# / .NET 10, as the rest of the repository
**Primary Dependencies**: `Spectre.Console` (new, on `Parley.Cli`); `Grpc.Net.Client` and
`Parley.Transport` via `Parley.Client`
**Storage**: none. This is a client; SPEC.md §4 gives the store to one broker process
**Testing**: xUnit v3 on Microsoft.Testing.Platform, as everywhere else. A new
`tests/Parley.Client.Tests` inherits the moved tests from `Parley.Web.Tests`
**Target Platform**: Windows, Linux and macOS terminals — the three CI already runs
**Project Type**: terminal application inside the existing CLI tool
**Performance Goals**: an unreachable project reported within five seconds (SC-004), matching the
console's existing two-second connect timeout
**Constraints**: renders nothing by colour alone (SC-007, FR-005); refuses a non-interactive
terminal (FR-018); opens no database (FR-016)
**Scale/Scope**: one machine's projects — the deployment this was built for has nineteen agents
across one project

## Constitution Check

*GATE: must pass before Phase 0, re-checked after Phase 1.*

| Principle | Verdict | Why |
|---|---|---|
| I. The specification is the product | **Pass, with a gate** | No protocol change is assumed. Research R1 must confirm every read and write maps to an operation SPEC.md already defines; anything that does not is a specification change and leaves this scope. |
| II. No agent has authority over another | **Pass** | The person is a participant, not an orchestrator. FR-019 keeps operator operations on the `Operators` client and the seat on `Agents`, as the web console already does. |
| III. Identity is fixed by the launch | **Pass, and it needs the same guard** | A person types a name, which is caller-supplied. SPEC.md §16 already concedes that anything reaching a broker can claim any agent, so the new power is *speaking as* somebody — and `SeatBook` already refuses a name the registry holds for anything that is not a seat, and refuses outright when the registry cannot be read. Moving that code moves the guard. See R2 for the one decision it forces. |
| IV. Terseness is a security control | **Pass** | Nothing new crosses the boundary. The composer must refuse rule 8.5's cap before sending (FR-010) rather than let the store refuse it after a round trip. |
| V. Invariants live in storage | **Not engaged** | Nothing new is stored. |
| Only question and answer text crosses | **Pass** | Reading conversations is the operator's grant under §10.2, which is what the web console reads them through, and which is why FR-019 keeps the two clients apart. |

No violations. Complexity tracking below is empty.

## Project Structure

### Documentation (this feature)

```text
specs/004-terminal-surface/
├── plan.md              # this file
├── research.md          # Phase 0 — the decisions and what they rest on
├── data-model.md        # Phase 1 — what the surface holds while it runs
├── quickstart.md        # Phase 1 — the journey, end to end
├── contracts/
│   └── operations.md    # Phase 1 — which existing operation serves each requirement
└── tasks.md             # /speckit-tasks, not this command
```

### Source Code (repository root)

```text
src/Parley.Client/               NEW. What a client of a broker needs in order to show a
                                 deployment to a person and to seat one. References
                                 Parley.Transport and Parley.Configuration and nothing else --
                                 the same two Parley.Web references today, so no graph grows.
├── BrokerDirectory.cs           moved from Parley.Web
├── BrokerClient.cs              moved
├── ProjectHealth.cs             moved
├── Seating/                     moved, less SeatCookie
└── Rendering/                   moved: Transcript, Visibility, Roster, RosterView,
                                 Payload, Palette

src/Parley.Web/                  Unchanged in role. Keeps ConsoleHost, Program, Pages, Brand
                                 and SeatCookie -- the only type in it that knows what HTTP is.
                                 References Parley.Client.

src/Parley.Cli/                  Gains the terminal application and Spectre.Console.
└── Top/                         The screens, the key handling, and the loop.

tests/Parley.Client.Tests/       NEW. Takes the tests that moved with the code.
tests/Parley.Web.Tests/          Keeps the ones about pages, cookies and rendering raw.
tests/Parley.Cli.Tests/          Gains the terminal's own tests.
```

**Structure Decision**: one new library and one new folder in an existing tool. The library exists
because two programs read it; the folder exists because `parley top` is a subcommand of the tool
that already carries every other one.

## Phases

**Phase 0 — research.** Settle what cannot be settled by reading this repository's code: which
existing operations serve each requirement, what a second seat surface does to `SeatBook`'s
guard, how Spectre.Console behaves on the three platforms CI runs, and how a log is tailed
without holding it open against the writer. Output: `research.md`.

**Phase 1 — design.** The move, then the surface. Output: `data-model.md`, `contracts/`,
`quickstart.md`, and an updated agent context.

The move lands first and on its own, because it must change no behaviour: the web console's tests
pass unchanged against relocated code, or the move is wrong. Only then is there a second reader.

## Complexity Tracking

No constitution violations. Nothing to justify.
