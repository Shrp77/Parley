# Specification Quality Checklist: Machine-level project manifest

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-08-28
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

All three clarifications resolved by the operator:

- **FR-019 / Q1 — one process hosts every configured project.** Implements SPEC.md §4.1's
  illustration directly. Brought two requirements the question did not: partial-start containment
  (FR-020) and stopping everything the process started (FR-021). The containment one matters most
  — a process that starts two of three brokers and exits zero is worse than one that fails.
- **FR-022 / Q2 — a launch configuration may name a project or carry an endpoint, endpoint
  wins.** The risk this admits is a repository whose configuration resolves differently on a
  teammate's machine, so FR-023 requires a loud refusal rather than a fall-back: connecting an
  agent to the wrong deployment is worse than an agent that does not start. FR-024 restates that
  identity is unaffected, and an assumption records that project resolution happens before
  connecting, so nothing about a project crosses the wire and FR-015 still holds.
- **FR-025 / Q3 — the tool manages the manifest.** Brought the withdrawal-versus-destruction
  distinction (FR-028): removing a project forgets how to reach it and deletes nothing, mirroring
  retire against purge in the existing operator commands.

One assumption still needs a reviewer's eye rather than a clarification: the spec proposes
**JSON** where SPEC.md §4.1's illustration says `parley.yaml`. If accepted, §4.1's example must be
amended in the same change, or the document will describe a file the implementation does not
produce.

Ready for `/speckit-plan`.
