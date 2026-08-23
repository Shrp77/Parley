# Tasks: What each agent owns, in the registry

**Status**: complete. 784 tests, all platforms green.

Ordered as they were done: the document first, then storage, then the value, then the three
readers. Every guard was probed with a reconstruction of the defect it exists for; the results
are at the end.

## Specification (US1, US2, FR-011)

- [x] **T001** `SPEC.md` §12.1 registry DDL gains `owns` and its CHECK, byte-identical to the
      migration — `SchemaConformanceTests` compares the columns and the CHECK literals.
- [x] **T002** `SPEC.md` §10.1 `agents()` says the roster carries what each agent owns.
- [x] **T003** `SPEC.md` §17 question 10 closed: the decision, the argument, the disclosure
      trade-off kept rather than discarded, and what was rejected.
- [x] **T004** References written as the document writes them — `principle 2.2`, not `§2.2`;
      `§17, question 10`, not `§17.10`. `Every_section_reference_resolves` resolves headings only.

## Storage (FR-001, FR-005, FR-006)

- [x] **T005** `agents.owns` in the initial schema, with the four-clause CHECK.
- [x] **T006** `AgentRecord.Owns`, both inserts, the manifest upsert, and both row projections.
- [x] **T007** `AgentRepository.RefreshOwnershipAsync` — the narrow, retired-skipping update
      that makes the statement correctable (research.md R1).
- [x] **T008** `OwnershipTests` — round trip, the limit, each refusal, non-English text,
      replacement, withdrawal, retired left alone, unknown not created, and that the refresh
      touches nothing else.

## The value (FR-005, FR-006)

- [x] **T009** `Parley.Core.OwnershipStatement`: the cap, `IsValid`, `Read`.
- [x] **T010** `OwnershipStatementTests`, including the multi-line roster-spoofing case.

## The launch (FR-002, FR-003, FR-004)

- [x] **T011** `DeploymentEnvironment.OwnsVariable` — in Core, because the server and the
      operator's tool both name it and neither references the other.
- [x] **T012** `ServerOptions` reads it, validates it, and refuses the launch with EX_CONFIG.
- [x] **T013** `BrokerSession` registers with it and refreshes it on every launch.

## The readers (FR-007, FR-008, FR-009, FR-012)

- [x] **T014** `parley_agents` returns `owns`, omitting the key when absent; tool description
      says who wrote it and what to do when it is missing.
- [x] **T015** `ParleyInstructions` says the roster is how you pick who to ask.
- [x] **T016** `parley agents` prints it, and prints `(unset — set PARLEY_OWNS …)` when there
      is none. This is the whole of the adoption nudge; see the clarification in spec.md.
- [x] **T017** `MixedWorkloadTests` projection list updated **with a reason** — the one field
      ever added deliberately.
- [x] **T018** `OwnershipRoutingTests` end to end, `OwnershipListingTests` for the operator.
- [x] **T019** README: `PARLEY_OWNS` in the launch configuration, and a new step 3 saying what
      to write and why. "Three things" became four.

## Verification

- [x] **T020** Twelve mutations, each failing exactly the check it was aimed at:

      cap raised back to 160 · validator blind to control characters · `Read` no longer
      collapsing blank · schema without the length bound · without the control-character bound ·
      without the NUL bound · launch never refreshing · refresh without the retired guard ·
      roster dropping the key · projection reading none of it · operator listing hiding the gap ·
      a bundled statement spanning two lines

- [x] **T021** Full suite green: Core 115, Store 299, Broker 202, Mcp 113, Cli 39, Spec 16.

## Not done here

- Per the clarification in spec.md, the Claude Code plugin's `/parley:peers` still writes its
  block by hand. It becomes roster-driven once a build carrying this field is installable.
- `workspace` remains write-once and therefore stale after a repository moves (research.md R1).
  Separate problem, separate argument.
