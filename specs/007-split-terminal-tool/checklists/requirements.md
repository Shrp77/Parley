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

Deliberate choices, to defend at review rather than "fix":

- **It names existing components and quotes SPEC.md.** The boundary between packages is what is
  being specified, so the components are the subject, not leaked implementation.
- **SC-002 is measured by replaying real history**, because the repository already holds the
  evidence: 29 of the changed source files last cycle were the terminal's.

Two corrections made during review, recorded so they are not re-argued:

- An earlier draft said the web UI was "unusable" on a headless machine. That was wrong. It is an
  HTTP server, runs without a display, and is reachable over an SSH port-forward, which 16 permits.
  The argument for splitting it is that it should be a choice, not that it is useless.
- An earlier draft was over-written. The owner asked for plainer prose. Rewritten shorter.

One defect found while writing and carried in the spec body: the terminal reaches SQLite today
through NodeCatalogue in Parley.Headless, which references Parley.Store. GraphTests could not have
caught it, because it does not walk the terminal. FR-004 and FR-005 make it checkable.
