# Specification Quality Checklist: The surfaces are their own tools

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-08
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

Three things about this spec are deliberate and worth defending at review rather than
"fixing":

- **It names existing components and quotes SPEC.md.** A spec that described the cost in the
  abstract would be unable to state *why* a terminal change ends a conversation, which is the
  entire case for the feature. The named things are the subject of the change, not an
  implementation choice smuggled in — the boundary between packages is what is being specified.
- **One scope question was settled by the owner rather than guessed**: the console moves out too,
  because on a headless Linux agent host it is not merely unused but unusable — 16 confines the
  transport to a trusted path and the console listens on loopback — while being the heaviest
  dependency in the tree. The remaining open question, how the one-command experience is softened
  across three tools, is recorded as an assumption with the decision named for the plan.
- **SC-002 is measured by replaying real history** rather than by a forward-looking guess, because
  the repository already holds the evidence: 29 of the changed source files in the last cycle were
  the terminal's.

One finding was made while writing this and is recorded in the spec body rather than deferred: the
terminal reaches SQLite today through `NodeCatalogue` → `Parley.Headless` → `Parley.Store`, and no
check could have caught it because `GraphTests` does not walk the terminal. FR-004 and FR-005 make
that a property of the build rather than of the layout.
