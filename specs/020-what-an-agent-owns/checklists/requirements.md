# Specification Quality Checklist: What an agent owns, asked rather than assumed

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-22
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

**This feature amends a closed question, and that is the single most important thing about it.**
SPEC.md §17 question 10 is marked closed and says a field a model could write "would be no more
trustworthy than the prose it replaces, merely centralised". The spec does not step around that; it
answers it, in a section written before the user stories, on four grounds — chiefly that question 10 has
already been amended to permit a *draft*, so the settled question is whose draft rather than whether one
may exist at all.

**The cost is stated rather than buried, because a later change will need to know it was weighed.**
Question 10 already narrowed its own claim once, from *the field was composed by a person* to *a person
read it and said yes*. This narrows it again, to *a person was shown a proposal, was free to argue with
it, and agreed to what resulted*. And the residual risk question 10 accepted — anchoring — is made
**worse**: a reasoned proposal from a counterparty that answers back anchors harder than a quoted README
line, and a person who does not engage has rubber-stamped a self-declared charter, which is the exact
state question 10 says is not worth reading. FR-012, FR-013 and FR-017 mitigate it; nothing eliminates it.
An owner reversing this decision later should reverse it on that paragraph.

**Two things make the amendment more than a concession.** The agent is the only source that can answer
the exclusion half without guessing — question 10's third condition forbids inventing it, and an agent
need not invent it, since it can name who it believes owns the rest and that claim can be checked against
that peer's own line (FR-004, FR-017, US4). And a transcript is a stronger form of question 10's second
condition than the checkbox it replaces: a boolean can be ticked unread, and an argument carried to a
conclusion cannot.

**No protocol change, and that was verified rather than assumed.** The broker still receives one value
from a launch environment, still refuses it in the same schema, and still cannot tell how it was composed.
FR-018 and FR-029 keep the write on the tooling side: an agent answers a message, a person decides, and
the component that already writes launch configurations writes it. §13 and §10.1 are untouched.

**The length bound and the character class are deliberately separated.** FR-023 removes the hundred; FR-022
keeps every refusal of characters that could break a roster line. The class is not a style rule — the
migration's own comment records that U+2028, U+2029, U+202E, U+061C, U+00AD, U+FEFF and the U+E0020–E007F
tag characters were every one of them accepted before it existed, and were found by sweeping the whole of
Unicode rather than by reasoning. Anyone reading FR-023 as licence to simplify that CHECK has misread it.

**FR-024 and FR-026 are what keep this honest rather than merely permissive.** The bound stays expressible
and stays in storage, following the two precedents in this repository that removed caps without removing
constraints; and the lengths agents actually propose become observable, so the number can later be set
from evidence the way the message cap's 8,000 was, rather than from judgement.

**One dependency is hard.** 019 must ship first. It removes the worksheet this replaces, and its
enrolment is what leaves agents registered, catalogued and startable — an agent that cannot be started
cannot be asked, and an unregistered agent is unroutable by §14 and would refuse the question outright.

**Scope warning, repeated from 019 and still unpaid.** 017 and 018 both owe their review gates, 019 is
specified and unbuilt, and nothing on this branch has reached CI. This is the fourth feature in the queue.
