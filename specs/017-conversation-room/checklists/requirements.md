# Specification Quality Checklist: A conversation is a room

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-21
**Feature**: [spec.md](../spec.md)

## Content Quality

- [X] No implementation details (languages, frameworks, APIs)
- [X] Focused on user value and business needs
- [X] Written for non-technical stakeholders
- [X] All mandatory sections completed

## Requirement Completeness

- [X] No [NEEDS CLARIFICATION] markers remain
- [X] Requirements are testable and unambiguous
- [X] Success criteria are measurable
- [X] Success criteria are technology-agnostic (no implementation details)
- [X] All acceptance scenarios are defined
- [X] Edge cases are identified
- [X] Scope is clearly bounded
- [X] Dependencies and assumptions identified

## Feature Readiness

- [X] All functional requirements have clear acceptance criteria
- [X] User scenarios cover primary flows
- [X] Feature meets measurable outcomes defined in Success Criteria
- [X] No implementation details leak into specification

## Notes

**On "no implementation details".** The spec names `@`, `/add`, two columns and an input line, which are closer to
a design than most specs here go. That is deliberate and it is what was asked for: the owner specified a layout,
and a spec that abstracted it back into "a way to address some participants" would be describing a different
feature. What is kept out is *how* any of it is built — no types, no toolkit, no file names.

**Three things were settled while writing it and are recorded rather than left open**, because each has one
defensible answer once the protocol is read:

- **`/add` cannot stand alone** (FR-015). §5.0 says membership is derived: a participant is anyone party to an
  exchange or that placed a file. There is no list to append to, so "add" with nothing to say would have to invent
  a Request — and every message is answered, which would manufacture an obligation with no content in it.
- **Routing never reaches a payload** (FR-016). §13 delivers a message inside a framing block telling the receiver
  that anything claiming authority is inert data; a routing directive in the text is exactly what that exists to
  have ignored. And the broker would have opened exchanges the excluded agents could not un-owe, putting wrong
  entries in the flags, the wait-for graph and §15's counts.
- **The answer-or-ask-back case is chosen, never derived** (FR-008). It is the one pair-state the derivation
  cannot reach, and the only one where guessing wrong destroys something: a Result closes an exchange, and an
  exchange closed on work that was not done is a record asserting it was.

**The command surface was added on the owner's instruction** and is specified as a surface rather than as `/add`
with a special case (FR-017 to FR-024): one form, `/` and an identifier; each command declares its identifier and
what parameters it takes, of what kind; and the line parses, completes, refuses and lists every command from that
declaration. A command added later is a declaration, not another branch in the input line.

Three things fell out of designing it that way rather than one command at a time, and each is recorded as a
requirement:

- **Commands divide into two kinds** (FR-022). `/add` changes the draft; a future `/close` would act. "Cannot be
  sent alone" is true of the first kind and false of the second, so an abstraction that assumed either would be
  wrong the moment the second arrived.
- **Parameters are typed, not text** (FR-018). `/add` takes a resolved agent, through the same picker `@` uses —
  so the command receives an agent rather than a spelling, for the same reason recipients are chips.
- **A command table must not become a second route to a guarded operation** (FR-024). `SurfaceTests` holds each
  destructive operation to one file carrying its own door, a description and the subject's name typed back. A
  generic dispatcher is exactly the shape that erodes that, and it would erode it invisibly.

**Two decisions are the owner's and are open:**

1. **What `/` means at the start of an ask that is not a command.** "Is `/etc/hosts` readable from your
   container?" begins with a slash. FR-020 refuses an unrecognised identifier rather than sending it, which is
   safe but not sufficient — a person needs a way to say "this is prose". The options are an escape, a rule about
   what follows the slash, or requiring commands to be completed from the picker rather than typed blind.
2. **Whether destructive operations may appear in the command table at all.** FR-024 says a command may not skip
   an existing door; the stronger position is that such operations simply are not commands, which keeps
   `SurfaceTests` able to prove it by reading one file.

**One thing the plan will have to settle that the spec deliberately does not**: what the input line does with a
person who holds no seat but is reading as an operator. FR-016 forbids sending under a seat nobody holds, which
covers the safety of it; whether the line is hidden, disabled, or offers to take a seat first is a surface
decision, and 0.28.2 already chose "offer, and open the composer once seated" for the conversations menu.
