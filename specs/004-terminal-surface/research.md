# Phase 0 research — a terminal surface a person takes part from

Five questions the plan could not settle by asserting. Each is answered from this repository or
marked as still open, and each names what would change if the answer turned out wrong.

## R1 — Does every requirement map to an operation that already exists?

**Decision: yes, with one caveat.** No protocol change is needed, which keeps this inside the
constitution's first principle.

| Requirement | Operation | Service |
|---|---|---|
| FR-003 agents and presence | `DescribeRegistry` | Operators |
| FR-004 conversations in a project | `ListConversations` | Operators |
| FR-004 transcript | `Export` | Operators |
| FR-006/FR-011 hold and release a seat | `Attach` (a held stream) | Agents |
| FR-008 what is waiting; answering | `Waiting`, `DrainInbox`, `Answer`, `ResumeOwedBy`, `GetSubQuestionIds` | Agents |
| FR-008 send into a conversation | `Clarify`, `Notify` | Agents |
| FR-009 open a conversation | `Ask`, `AskRole` | Agents |
| FR-013 the broker's log | not an operation — a file (see R4) | — |
| FR-015 name a project, onboard an agent | `parley project add` / enrolment, in-process | none |

`ListConversations` and `Export` are the two operator reads that `specs/003-parley-web-console`
gated itself behind and that were specified before being built. They exist, and the terminal is
their second reader rather than a reason to add a third.

**The caveat.** `Export` was specified to serve a console transcript, not a terminal one. If the
terminal wants anything it does not carry, that is a SPEC.md §10.2 change and leaves this scope —
the same gate `003` accepted. The plan assumes it carries enough because the web console renders
its transcript from it today.

**If wrong**: any missing read becomes a specification change first, and this feature waits.

## R2 — What does a second seat surface do to the guard on a name?

**Decision: both surfaces present the same adapter, `parley-console`, and `Seat.Adapter` stays a
single constant.**

`SeatBook` refuses a name the registry holds for anything whose adapter is not `Seat.Adapter`,
and refuses outright when the registry could not be read. That guard is not about *which*
surface a person is sitting at. It is about the one power seating grants that no operator
operation does — speaking as somebody — and the thing it must not let a person do is take the
name of an **agent**.

Giving the terminal its own marker (`parley-top`) would turn one constant into a set, and would
mean a person seated in a browser could not seat themselves in a terminal under their own name.
SPEC.md §14 makes two sessions for one agent legitimate, so that would be a new restriction
invented by a naming choice.

**Consequence to carry into design**: a person seated in both is two attachments for one name,
which is exactly the case `Broker.StartAsync` reads when it decides whether to release abandoned
work — and where `connected = 1` was wrong until 2026-09-02. The terminal makes that case
ordinary rather than rare, so `tests/Parley.Client.Tests` must cover a person seated twice.

**If wrong**: if a future guard needs to tell the surfaces apart, `Seat.Adapter` becomes a set and
the registry gains a distinction it does not have today. Nothing here forecloses that.

## R3 — Spectre.Console, and what taking a dependency costs

**Decision: add it to `Parley.Cli` under central package management, pinned exactly.**

Constraints this repository already imposes, which the choice has to live inside:

- Central Package Management: the version goes in `Directory.Packages.props`, and a `Version=`
  attribute in the csproj is a restore error.
- `NuGetAudit` fails the build on high or critical advisories. A new dependency is a new source of
  those, and this is the first one on the tool that is not Microsoft's or gRPC's.
- Warnings are errors and documentation is enforced; a package that emits either fails the build.

**Open until implementation**: the exact version, and whether it needs a transitive pin with the
justification `Directory.Packages.props` requires of every entry in that group.

**If wrong**: the alternative is drawing with `System.Console` directly, which is what the
startup banner does today. That is viable for a status view and not for a scrolling transcript
with panes, so the fallback is a smaller feature rather than a different library.

## R4 — How is the broker's log read while the broker is writing it?

**Decision: open it with `FileShare.ReadWrite | FileShare.Delete`, and never with the default.**

Not a guess — this was measured in this repository on 2026-09-02, on the manifest rather than the
log. A reader holding a file with the default share mode does not merely risk a stale read: it
**stops the writer**. `File.ReadAllText` opens with `FileShare.Read`, and a rename onto a file
held without `FileShare.Delete` is refused outright:

```
Move,    readers FileShare.Read        287 / 300 denied
Replace, readers ReadWrite | Delete      0 / 300 denied
```

A log tail is a reader that is open *continuously*, which is the worst version of that. If the
broker ever rotates or replaces its log, a tail holding it the wrong way would block the rotation
rather than follow it.

**If wrong**: the log is a convenience view. It can be dropped without touching any other story.

## R5 — Does a terminal application belong in `Parley.Cli` at all?

**Decision: yes, as a subcommand.**

The alternative is a third tool, and CLAUDE.md records that argument being had and settled when
the web console was added: a second tool would have kept ASP.NET Core off machines that only run
an agent, *and one command is worth more than that saving*. The same reasoning applies with less
cost here, because Spectre.Console is smaller than Kestrel and because the terminal is for the
person who already has the tool installed.

**If wrong**: `parley top` is a thin entry point over `Parley.Client`, so moving it into its own
tool later is a project file and a `Main`.

## What is deliberately not researched

**Whether the web console should survive.** It should; FR-014 opens it, and it remains the
better reader on a large screen. Nothing here is a migration.

**How the terminal renders.** Panes, keys and layout are design, not research, and they are the
part a person will ask to change after using it once.
