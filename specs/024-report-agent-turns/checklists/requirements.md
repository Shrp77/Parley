# Specification Quality Checklist: Report agent turns, so working and stopped are told apart

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-10-03
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

- The spec uses this repository's own vocabulary — broker, machine/node, turn, flag, operator surface, SPEC.md
  sections — because the stakeholder is the owner and those are the protocol's terms, not implementation choices. No
  language, framework, wire format or table layout is named.
- FR-015 requires SPEC.md to be amended before code, per CLAUDE.md's rule for protocol changes.
- Branch: the `before_specify` git hook was not run; 024 is on `019-layout` with 019–023, by the owner's arrangement.
- 023 (always a seat) still has phases 5–8 open; `.specify/feature.json` now points at 024 and must be pointed back
  to 023 to resume it.
