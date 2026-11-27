# Tasks: Soft message limit and message length statistics

## Foundation

- [x] T001 `MessageLimits`: hard 512,000 and soft 16,000; every use of the old constant reviewed. (FR-001, FR-003, FR-015)
- [x] T002 Migration 005: rebuild `messages` and `ownership_questions`, keep sequences, recreate triggers and indexes, add `message_lengths`, its trigger and backfill. (FR-002, FR-007, FR-009, FR-013)
- [x] T003 [Integration] `MigrationFiveTests`: a schema 4 store with rows in every affected table; all rows and sequences kept; integrity and foreign key checks pass; triggers still refuse what they refused; 8,001 and 512,000 stored; 512,001 refused; a failed migration leaves the store as it was; 100,000 messages timed. (SC-004, SC-006)
- [x] T004 Update `MigrationDiscoveryTests` and SPEC's schema text; a test holds the code's limit and storage's together. (FR-015)

## User Story 1: a long message gets through

- [x] T005 `long_message` on successful replies; wording of `toolong`, tool descriptions and instructions. (FR-004, FR-016)
- [x] T006 [Integration] A 100,000-character Result through the real server, collected whole; exact boundaries. (SC-001)
- [ ] T007 **Not done.** Check through a real Claude Code client, and record what happened in research.md. (Scenario 1.4) An automated attempt was blocked, because it starts another autonomous session; quickstart step 3 does it by hand after installing.

## User Story 2: a person keeps their words

- [x] T008 [Integration] Terminal: over the limit, refused, box unchanged, refusal counted; 20,000 sent. Fix what fails. (FR-006, FR-012)
- [x] T009 [Integration] Web: the same on the conversation page and the inbox's forms. Fix what fails. (SC-002, SC-003)

## User Story 3: the statistics

- [x] T010 `MessageLengthRepository` and `MessageLengthReport`; tests with known lengths, including after pruning, a purge and a rename. (FR-008, FR-010)
- [x] T011 Through `OperatorConsole`, the proto, `OperatorsService`, `IParleyBrokerClient`, `BrokerClient`. (FR-011)
- [x] T012 Terminal health report; web Health page. The terminal's lines are tested; the web page's new tables are not, because no console holds a broker with records. (SC-005)

## User Story 4 and the rule

- [x] T013 Long messages on the transcript: the terminal draws two messages at the limit in under 10 seconds, and the conversation page round-trips one past it. Scrolling, and the web page's rendering in a browser, were not measured.
- [x] T014 One rule, everywhere: SPEC 8.4, §9.3, §10.2, §17 q16 (with the owner's report), CLAUDE.md, constitution, docs, comments. (FR-014)
- [x] T015 Version: minor bump; CLAUDE.md state line, including that 027 moves to migration 006.
- [ ] T016 Unit suite three times, adapter suite, then a pull request. The owner merges.
