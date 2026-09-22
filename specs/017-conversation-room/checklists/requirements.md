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
with a special case (FR-017 to FR-028): one form, `/` and an identifier; each command declares its identifier and
what parameters it takes, of what kind; and the line parses, completes, refuses and lists every command from that
declaration. A command added later is a declaration, not another branch in the input line.

Three things fell out of designing it that way rather than one command at a time, and each is recorded as a
requirement:

- **Commands divide into two kinds** (FR-024). `/add` changes the draft; a future `/close` would act. "Cannot be
  sent alone" is true of the first kind and false of the second, so an abstraction that assumed either would be
  wrong the moment the second arrived.
- **Parameters are typed, not text** (FR-018). `/add` takes a resolved agent, through the same picker `@` uses —
  so the command receives an agent rather than a spelling, for the same reason recipients are chips.
- **A command table must not become a second route to a guarded operation** (FR-026). `SurfaceTests` holds each
  destructive operation to one file carrying its own door, a description and the subject's name typed back. A
  generic dispatcher is exactly the shape that erodes that, and it would erode it invisibly.

**Both open decisions were settled by the owner on 2026-09-21, and each changed a requirement:**

**1. An unrecognised `/something` is text, and a command is `/` plus an identifier with no second `/`**
(FR-020, FR-021). Better than the refusal the first draft specified, and for a reason worth keeping: the two rules
together decide `/etc/hosts` without reference to what any command is called, so the set of paths a person may ask
about never has to be reconciled with the set of commands that exist. Nothing is refused for not being a command,
because the identifiers somebody might type are not a closed set they can be expected to know.

**What it costs, and what pays for it.** The failure mode inverts: it is no longer "my command was refused" but
"my command was sent". `/ad @billing` becomes an ask that every participant must answer — real turns spent on a
typo. FR-022 is the mitigation and it is the design's own idiom rather than a new mechanism: the line already says
what Enter will do, so it says *sending as text* when a draft starts with `/` and did not resolve. Visible before
Enter beats refused after it, and it keeps prose sendable.

**2. Commands may be destructive, and more operations are expected to become commands** (FR-026 to FR-028,
FR-039). So the requirement is not that commands stay away from guarded operations but that they arrive at the
same door: a description of what will go, and the subject's own name typed back. A destructive command declares
its door beside its identifier, so the door is part of what the command *is* rather than something a handler
remembers.

**The completion list was the owner's answer to the cost of that, added the same afternoon** (FR-029 to FR-036):
typing `/` opens every command, the list narrows by prefix, up and down select, Tab completes, and the list closes
when nothing matches and returns if enough is deleted that something matches again. Their reasoning, in their
words: *"this way accidental typos in commands are less likely."*

That is why these are requirements rather than polish. FR-021 sends an unrecognised identifier as text, so a
command typed blind and wrong costs somebody an ask they must answer — and the remedy is to make typing one blind
the unusual path rather than to add a refusal back. Two details in it are load-bearing:

- **The list is derived, never remembered** (FR-033). There is no flag saying somebody was completing a command;
  there is the text and what it matches, which is the only way deleting back into a match can bring the list
  back. The same discipline as the waiting count, which is asked rather than accumulated for the same reason.
- **Prefix decides the list; exact match decides the send** (FR-034). Those are two different comparisons and
  conflating them is a bug waiting: `/ad` is text even while the list is offering `add`, because nothing completed
  it. The list is a suggestion; the identifier is the fact.

**That question is settled, and the reasoning I offered for it was wrong.** I claimed fingers arriving from
Claude Code would expect Enter to accept the highlighted item; the owner, who uses it daily, corrected that —
Claude Code sends on Enter and does not complete on it. So Enter always sends and Tab always completes
(FR-031), which is also the only version consistent with the rest of this line: a key that sent most of the time
and accepted a suggestion the rest is the one that loses a message somebody meant to send.

One thing follows that is worth having, and it is FR-032: where the identifier has not resolved but *is* a prefix
of one that would — the list is open, Enter would send as text — the line names the command Tab would complete.
That is the near miss the list exists to prevent, arriving at the one keystroke where preventing it still costs
nothing.

**The consequence the owner should know about.** `SurfaceTests` proves the door exists today by holding each
operation's name to exactly one file which must contain its prompt. As operations move into commands, that check
keeps passing while meaning less — the name now lives in a declaration and the dispatch is generic. FR-028 says
the guarantee must stay mechanically checkable, which means that test is rewritten to read the command
declarations rather than deleted or left to pass vacuously. It is the same failure this repository has recorded
twice: a guard that cannot see the thing it was written about (`SetupSurfaceTests` and a nested public type; the
terminal's store-freedom before it had a project of its own).

**One thing the plan will have to settle that the spec deliberately does not**: what the input line does with a
person who holds no seat but is reading as an operator. FR-016 forbids sending under a seat nobody holds, which
covers the safety of it; whether the line is hidden, disabled, or offers to take a seat first is a surface
decision, and 0.28.2 already chose "offer, and open the composer once seated" for the conversations menu.
