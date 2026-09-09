# Specification Quality Checklist: An instruction survives the turn it asks for

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-08
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs) — with one deliberate exception, below
- [x] Focused on user value and business needs
- [ ] Written for non-technical stakeholders — **does not apply here, see below**
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

### What was actually checked, and how

Checked mechanically rather than by reading, because a checklist ticked by its own author is worth
what the check behind it is worth:

- **No code identifiers in the document.** Searched for every type, file and technology named in the
  originating description — the service class, the invocation command, the request-abort token, the
  repository, the settle method, the wire definition, the client interface. **None appears in the
  spec.** The defects are described in terms of *the broker*, *the machine holding the agent*, and
  *first-come settlement*, which is what will still be true when this build is gone.
- **No unresolved clarifications.** Zero markers.
- **Exactly one file path**, at line 27: the test that pins the loss. Kept deliberately. A reader
  who doubts Defect 1 should be able to run the thing that demonstrates it, and a defect asserted
  with no way to reproduce it is the kind of claim this repository's review gate exists to distrust.

Two claims are load-bearing and were checked against the code rather than assumed:

- **FR-025, *"the one record with no bound at all"*** — the pruner's only deletion is from the
  message log; nothing prunes instructions. Verified by reading the pruner's statements.
- **Key Entities, *"gone when that agent is purged"*** — the instruction record cascades on the
  agent. Verified in the schema.

### The item that is not ticked, and why it is not

**"Written for non-technical stakeholders" does not apply to this repository and ticking it would be
a lie in both directions.** SPEC.md is the primary artifact and its audience is people implementing
a protocol; this document's audience is the same. It is written to be read by somebody who has not
seen this codebase — which is the useful half of that criterion, and is met — but it assumes a
reader who knows what a broker, a turn and an agent are, because the specification it amends
assumes exactly that.

Marking it complete would suggest the spec had been softened for an audience it does not have.
Leaving it unticked with this note is the honest record.

### Two things deliberately left for the plan

Both are genuinely open, and guessing them in a spec would have made them look settled:

- **How long the broker waits before answering *still running*.** The spec requires only that giving
  up be reported honestly and that it never be recorded as a failure. The number is a tuning
  decision with a browser timeout and a person's patience on either side of it.
- **Whether the sweep is a new mechanism or an extension of an existing one.** The spec requires the
  outcome; the plan decides whether the reaper that already exists for exchanges grows a second job
  or a second reaper is added beside it.
