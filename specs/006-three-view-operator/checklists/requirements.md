# Specification Quality Checklist: Three-view operator surfaces

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-05
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
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

**All three settled by `plan.md` on 2026-09-05.** Kept below with their outcomes, because a checklist
that erases the question once it is answered cannot tell a reader which items were ever in doubt:

1. **FR-011** requires an added agent to be startable with no further human act, while SPEC.md
   §12.3 forbids a surface registering on a repository's behalf — the prohibition 005's planning
   used to reject exactly this. **Settled: honoured in full, with no protocol concession.** §12.3's
   closing paragraph routes spawn-on-demand through §12's manifest, and `NodesService.RequestStart`
   guards only on the node's catalogue naming the role, with no registry lookup. A new `start`
   operation launches it; the adapter registers the agent it attaches. `research.md` R1.
2. **FR-016** leaves the behaviour of an archived conversation that receives a message to be
   decided. **Settled: it stays archived**, delivery untouched, the row marked as having had activity
   since. Auto-un-archiving would let an agent reverse an operator's decision, which is the one
   direction of authority Principle II does not have. `research.md` R4.
3. **FR-006** requires a destroyed conversation to leave something an operator can be told, without
   saying what. **Settled: a tombstone of facts and no content** — identifier, when, message count,
   participants. §9.4's argument against keeping content stands, and the primary key stops the name
   being reused under somebody's existing export. `research.md` R3.

Two assumptions the spec made that research found wrong, corrected in place rather than built:
colour is name-derived and global rather than per conversation (R7), and a conversation's name is its
identifier rather than a field beside it (R5).

References to SPEC.md sections are domain vocabulary in this repository, not implementation detail:
the protocol is the product and the code is a reference implementation of it.
