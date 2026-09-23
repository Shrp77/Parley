# Specification Quality Checklist: An agent is an identity, not a name

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-22
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

**The three decisions that would otherwise be clarifications were taken by the owner before this was
written**, and are recorded in Assumptions rather than marked:

1. The identifier stays machine-side; messages are still addressed by name.
2. One identifier is one checkout, not one agent-in-a-checkout.
3. Enrolment mints it, into the repository, never derived from a path or a name.

**Two class names appear in "The defect this starts from"** (`RequestStart`, and the broker's own log
sentence). They are quoted as evidence of where the state is produced, which is what makes the defect
checkable rather than asserted; the requirements name none of them.

**SC-003 is the one worth arguing about.** "Cannot be represented" is a claim about a shape rather than a
measurement, and it is the claim this whole feature rests on. It is verified by trying: there must be no
arrangement of catalogue and membership files that produces a checkout startable in one project and not
another.
