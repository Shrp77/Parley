# Specification Quality Checklist: One place, and one way in

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

**On "non-technical stakeholders".** The user here is an operator configuring a machine, so the
vocabulary is theirs: projects, stores, endpoints, ports, repositories. What the checklist forbids is
*implementation* detail, and the spec holds that line — it names no file format, no file name, no locking
primitive, no command name, no language and no library. FR-009 to FR-015 say a record exists at the root
and is held exclusively while a port is handed out; they do not say what the file is called or how the
hold is taken.

**Five decisions were the owner's and are recorded as settled**, not re-opened: the single manifest is
replaced by a folder per project; existing stores are moved; creating a project allocates its port; the
port record is a machine-level file with its own narrow lock; and the command line sheds everything a
person would act on, keeping what a program calls and what brings a machine up. The last of those also
settled that starting at logon keeps its three verbs and loses its options.

**Three things were pushed back on during the writing, and two of them are protocol walls.**

1. *"Scan just finds and registers agents."* It cannot register them. SPEC.md §12.3 makes registration
   the adapter's own act on attach, and nothing may write a registration row on a repository's behalf —
   018 restated that as a standing constraint. FR-037 gets the owner's outcome by the only honest route:
   configure, then **start**, and each agent registers itself. That path already exists.
2. *Renaming the operator operation to match the terminal's "Say something" box.* FR-044 refuses it.
   §10.2 warns that surfacing an operator operation into an agent's tool set breaches §10.1, and says the
   protection is a naming convention. The room's composer sends real messages; giving the operator
   operation the same verb makes the two indistinguishable exactly where the document relies on them
   differing. The owner's underlying complaint is answered instead by that operation leaving the command
   line altogether.
3. *Removing the enrolment file outright.* It carries the `owns` line and its approval, and the registry's
   promise is that a person read that line and said yes. The spec removes the authoring step (FR-034) and
   refuses to lose what exists (FR-042 carries forward any statement a repository's launch already holds).
   Where a *new* line comes from is deliberately not answered here — see below.

**Two decisions were taken while this was being written, and both are settled.**

*Enrolling is the consent* (FR-037). A catalogue entry was a per-repository opt-in that defaulted to off,
because it is standing permission to start a checkout with nobody watching and spend the owner's tokens.
On a real machine that produced nineteen repositories enrolled, addressable and unstartable — the state
that prompted this whole feature. An agent that cannot be started cannot answer, so an enrolment that
leaves one that way has not enrolled anything. The consent does not vanish; FR-038 moves it to where a
person is actually looking, stated once before anything is configured and naming the count.

*The verb is not `scan`* (FR-034). Naming the first of three steps `scan` was honest while that step only
looked. A verb that finds, configures and starts is not a scan.

**What replaces the `owns` worksheet is a separate feature, and the owner's idea for it is better than
what it replaces.** Rather than asking a person to compose the line — which assumes they remember what a
repository was meant to own — ask *the agent*, through Parley, and discuss it until the two agree. The
mechanism already exists and points the right way: `AgentRepository.RefreshOwnershipAsync` makes `owns`
belong to the launch, refreshed on every start and cleared when the launch stops saying it, precisely so
that a statement can be corrected. Three things make it worth its own feature rather than a clause here:

- It answers the half this design has always admitted it cannot. The worksheet says a repository "can show
  what it contains and cannot show what it has decided not to own". An agent can say what it believes it
  does *not* own, which turns the gap into a question for a named peer.
- A discussion is what `owns_approved` was a weak proxy for. A checkbox can be ticked unread; a transcript
  in which a person argued a proposal to a conclusion is the evidence the registry's promise refers to.
- It has costs that need designing rather than assuming: it is agent-written text entering a registry
  every peer reads once per listing, so it must be marked as the agent's words and refused if it can break
  its own line; and it spends one turn per agent, so anything doing it in bulk must say the count first.

**The owner decided the length bound goes, and the shape of that is on record here so 020 inherits it.**
Today `agents.owns` carries a storage CHECK doing two jobs: `length(owns) <= 100`, and a character class
refusing control, format, line-separator, bidi-override, invisible and tag code points. **Only the length
goes.** The class stays and is not negotiable — its comment records that U+2028, U+2029, U+202E, U+061C,
U+00AD, U+FEFF and the U+E0020–E007F tag characters were every one of them accepted before it existed, and
were found by sweeping the whole of Unicode rather than by reasoning. A line that can break itself or
reorder what follows can imitate the entries around it, in a list every agent reads.

The 100 bought something real and the number is worth keeping in view: a roster entry costs about 75
characters of structure before the statement, charged to every agent once per peer per listing. At
nineteen agents that is roughly 3,300 characters today, about 9,000 if lines run to 400, and about 20,000
at a thousand — paid by everyone, every listing. The cost scales with the deployment rather than with the
line, which is why it was bounded in storage rather than asked for politely.

**What replaces it follows this repository's own precedent twice over.** 0.25.5 removed §7.1's
open-question cap and 0.25.8 removed the clarify cap, both on the ground that *a cap censors the evidence
that would justify it*, and both kept the CHECK and switched it off with a sentinel rather than deleting
the constraint — so a deployment may reinstate a bound and §9.3's rule that invariants live in storage
survives intact. The message cap's 8,000 is the one number here set from measured refusals rather than
from judgement, and this should end up set the same way.

So: the class kept, the length unbounded by default through the existing sentinel spelling, the ask
telling the agent *why* brevity matters rather than quoting it a limit — that this line is read by every
agent in the deployment whenever anyone lists the roster, and costs them context each time — and the
lengths measured so the number can later be chosen from evidence.

It is a schema change, so it carries a migration; and SPEC.md §9.3 holds that CHECK verbatim, where
`Parley.Spec.Tests` executes every schema fragment against SQLite. The document and the migration move
together or that suite fails. **This is why it belongs to 020 and not here**: 019 states plainly that it
changes no schema.

FR-042 is what keeps 019 from regressing in the meantime: existing lines survive, new repositories have
none, and peers fall back to names, which is already supported.

**One cost is stated in the Assumptions rather than buried.** FR-007 forbids naming a store outside a
project, and fourteen test files in this repository currently start a broker by store path through one
shared helper. That conversion is part of the work, and it is affordable only because creating a project
in a throwaway root now costs a name and no paths — which is the same change that makes FR-007 desirable.
A plan that treats it as incidental will be wrong about the size of this feature.

**Two requirements deliberately overlap and are not redundant.** FR-012 (the project's file records the
endpoint that is bound) and FR-009 (the machine records what it handed out) name two facts that concern
the same number. FR-013 says which wins, which is what stops the pair becoming two answers to one
question.

**One risk is recorded rather than resolved**, because resolving it is the plan's work: FR-028's "served
within seconds" is today met by comparing a single file and must become an observation of a directory
tree. Feature 015 records what filesystem watching costs when one rename raises a creation and a change
that each replace the pending look.

**Scope warning, recorded for the owner rather than for the plan.** This is the third large feature on
this branch whose review gate is unpaid — 017 and 018 both owe theirs, and nothing on the branch has
reached CI. Merging the layout and the command overhaul was the owner's explicit choice, made with that
cost stated. It is repeated here because the spec is where it will be read next.
