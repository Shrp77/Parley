# Specification Quality Checklist: A terminal surface a person takes part from

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-02
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

**On "no implementation details".** The spec names Spectre.Console nowhere; the input did, and it
is a `/speckit-plan` decision rather than a requirement. `parley top` likewise appears only in the
Input quote — no requirement depends on the command being called that.

**On the one open question.** Q1 (what is shared with the web console) is deliberately *not* a
`[NEEDS CLARIFICATION]` marker. It is not a gap in what the user wants; it is a design decision
with a cost on either side, and the spec states the constraint that governs it — SC-003 forbids
the two surfaces disagreeing, and the web console's dependency graph must stay free of the broker
and the store. That is enough for planning to decide it, and it is the question the user
explicitly asked the spec to raise rather than assume.

**Two requirements are there because of defects found on 2026-09-02**, and both are worth keeping
as requirements rather than as review notes:

- FR-012 (seated only while a seat is held) restates a bug the web console shipped: a cookie
  outlived its seat and the frame showed a person as present while the page beneath it said they
  were seated nowhere.
- FR-002 (re-read the manifest while running) restates another: the console read the manifest
  once at startup, so a project named while it ran never appeared.

A second surface that repeats either would be worse than the first, because the two would then
disagree with each other as well as with the deployment.
