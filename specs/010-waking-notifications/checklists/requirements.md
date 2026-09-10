# Specification Quality Checklist: Waking notifications

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-10
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs) — **with a recorded deviation, see Notes**
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders — **partially, see Notes**
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
- [x] No implementation details leak into specification — **with the same deviation**

## Notes

**The deviation is deliberate and is this repository's convention rather than a lapse.** The generic
template asks for no implementation detail and a non-technical reader. Here `SPEC.md` is the primary
artifact and the code is a reference implementation of it, so a feature that changes the document
must name the sections it changes, and a feature that reverses a recorded decision must quote it.
Specs 007, 008 and 009 all name files and methods for the same reason.

What was held to instead: **every functional requirement is stated as behaviour**, and the code
names appear only in the rationale, the assumptions and the carried defect. FR-001 to FR-024 can each
be read and tested without opening a source file. The success criteria name no type, file or table.

**Three things this spec settles that the input listed as open**, recorded here so a reader can see
they were decided rather than skipped:

1. *Where the bound lives and what shape it is.* Per recipient across all senders, keyed on an
   outstanding unsatisfied start rather than on elapsed time, with a ceiling as the failure valve
   (FR-005 to FR-009). The elapsed-time-only design was rejected because "is one already coming?"
   has an exact answer and a timer only approximates it.
2. *What the sender is told.* A four-way outcome on the reply, reported by both composers, never
   presented as a failed send (FR-011 to FR-013). This is the half of the reported defect that was
   not about waking at all.
3. *Whether the broadcast is a protocol operation.* It is not — N ordinary notifications, on §5.5's
   precedent (FR-016).

**One thing deliberately left to planning**: the ceiling's actual number. FR-009 fixes its form and
the assumptions fix how it is justified; the value is a judgement the plan makes and the record
marks provisional, following the message cap's precedent.

**One thing left open on purpose**: §17 question 1, what bounds Notify's volume. This feature bounds
turns, not messages, and says so in its own opening rather than letting a reader infer that the
question was closed.
