# Specification Quality Checklist: Parley file store

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-09-14
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

- Validated once against the spec as written; every item passed on the first iteration.
- "Through the broker" and "beside the project's store" name Parley's own components, which are the product's
  vocabulary rather than a technology choice; the wire protocol, storage engine and chunking are left to the plan.
- No clarification markers were needed: the owner settled transport, visibility, prune scope and process on
  2026-09-14, and each remaining open point had a default grounded in a standing constraint (recorded in Assumptions):
  files are read from and written to the agent's own repository because each agent changes only its own.
- **Revised after review, 2026-09-14.** The first draft made remove and prune the person's alone, on the grounds that no
  agent has authority over another. The owner pointed out that the same reason means an agent's own file is its own to
  take back. User Story 3 (an agent removes files it placed) was added, FR-013 to FR-017 rewritten around the placer
  recorded by the broker, and SC-004 added. Prune stays the person's, since it removes other agents' files. Re-validated:
  every item still passes.
- **Revised again, 2026-09-14.** The owner ruled that placing different content under an existing name is legal and
  asked for a SHA to show a file is different. Replacement is now allowed to the placer (the same ownership rule),
  identical content is a no-op, and every file carries a broker-computed SHA-256 shown on place, fetch and list. A fetch
  may state the hash it expects and is refused if the file changed. User Story 3 became "replaces or takes back", edge
  cases gained concurrent replacement and hash mismatch, and FR-004, FR-005, FR-010 to FR-012 and SC-004, SC-005 were
  added or rewritten. "SHA-256" is named because the owner asked for a SHA; it identifies content and is the one
  technical term the spec keeps. Re-validated: every item still passes.
- The owner then confirmed that only the agent that placed a file may replace it, which the revision had assumed from the
  ownership rule. The assumption is now recorded as the owner's decision; no requirement changed.
- **Revised a third time, 2026-09-14: files-open, files-folder, and files grouped by conversation.** The owner added two
  person-facing commands and answered four design questions: files are grouped by conversation (reversing the flat
  project location), files-open opens the stored file in place, a folder watcher re-hashes any change made outside
  Parley, and the terminal and web UI launch on the machine they run on. User Stories 6 and 7 were added; stories 1, 2,
  4 and 5 now name conversations; requirements were regrouped (FR-001 to FR-036) with new ones for conversation
  assignment, outside changes (FR-015 to FR-017) and opening (FR-026 to FR-030); SC-007 and SC-008 were added.
  Assumptions made to fill gaps and stated to the owner: files-open from another machine reports machine and path;
  files-folder with no conversation opens the whole location; a hand-copied file belongs to the person; destroying a
  conversation removes its files; a fetch overlapping an edit fails rather than mixing. Re-validated: every item passes.
  "SHA-256", "operating system" and "file browser" are the owner's own terms or plain descriptions, not technology
  choices.
- **Revised a fourth time, 2026-09-14: downloads and change notices.** The owner ruled that a file being downloaded is
  read-only to editors, and that an agent should be told when a file it fetched changes, with no time limit. Asked when a
  file stops counting as open in an editor, the owner dropped open-tracking altogether: the folder watcher telling agents
  of changes is enough. Answers: notices wait for the agent's next inbox check and wake nothing; every fetch, not only
  fetches made while a file was open, gets them. User Story 2 (change notices) was added and later stories renumbered;
  requirements are now FR-001 to FR-042, with FR-012 (read-only during a download) and FR-019 to FR-023 (fetch records and
  notices) new; SC-005, SC-006 and SC-009 were added. Assumptions stated to the owner: "active" means not retired or
  purged; one notice per file, withdrawn if the content returns to what was fetched; a notice is not a message and owes no
  reply; an editor shows a refused save its own way. Re-validated: every item passes.
- **`/speckit-analyze` findings, addressed one by one (2026-09-14).**
  - **H1 resolved.** The spec said an editor's save during a download is refused on every platform; only Windows can
    refuse another process's write. FR-012, US1 scenario 11, US7 scenario 3 and the read-only assumption now say: refused
    on Windows; on Linux and macOS the download fails saying the file changed and writes nothing. The no-mixed-file
    guarantee holds everywhere.
  - **H2 resolved by the owner's rule: only `.md` and `.txt` files.** Code is placed as `PriceSnapshot.cs.txt`. FR-004
    (the extension rule, and the advice to add `.txt`), US1 scenario 12, FR-032 (opening launches nothing for any other
    extension), two edge cases (hand-copied or renamed files with other extensions are ignored or gone), an assumption and
    the out-of-scope list were added or changed; the examples moved from a `.zip` to a class and a markdown note.
    research.md R4 and R10, data-model.md, contracts, quickstart, plan and tasks follow. Asked whether content is checked
    too, the owner chose name only: bytes are never inspected.
  - **H3 resolved.** US1 scenario 4 looked in the agent's own conversations; FR-010 looked across the project. The owner
    ruled: search the whole project; if more than one conversation holds the name, do nothing and ask which one, by
    conversation name. FR-010 and scenario 4 now say so for fetch, remove and open; research.md R8 and the contracts
    follow, and the wire gained the `FileRefusal` message the draft had referred to without defining.
  - **M1 resolved.** The name rules lived only in the plan. The owner set them: a file's path inside the location
    (conversation folder, separator, name) is at most 255 characters, the location's own path not counted; names differing
    only in case are one file for every letter. FR-003, FR-004 and two edge cases carry it; research.md R3/R4 replace
    ASCII-only `NOCASE` with a stored case-folded key and note long Windows paths; data-model.md and T012 follow.
  - **M2 resolved.** The spec said a rename keeps its placer; the plan's start-up scan gave an offline rename to the
    person. The owner chose matching by content hash: a new file whose hash matches exactly one file missing from the same
    folder is that file renamed and keeps its placer; otherwise it is the person's. FR-018, the rename edge case,
    research.md R6 and T038 carry it.
  - **M3 resolved.** Mapping a reserved conversation id `con` to `_con` collided with a conversation named `_con`, and
    checking the id rule (`[A-Za-z0-9._-]{1,64}`) found three more collisions the finding had not: `Pricing` beside
    `pricing` on case-insensitive filesystems, a trailing dot Windows strips, and a leading dot that hides the folder. The
    owner chose the id, with `~N` added when unsafe or colliding; ids cannot contain `~`. research.md R2, data-model.md,
    an edge case and T022/T023 carry it.
  - **M4 resolved.** Placing a file with no conversation named, by an agent holding no request, now starts a new
    conversation exactly as a Request would (the owner's call). Because §5.0 derives membership from exchanges, placement
    also joins the placer as a participant; FR-006, US1 scenario 13, an edge case for conversations holding only files,
    research.md R8, data-model.md and T003/T027/T031/T050/T051/T056/T057 carry it.
  - **M5 resolved.** Placement counts (FR-039, SC-016) were asserted nowhere and shown only on the command line. T049,
    T050, T051, T056 and T057 now assert and show them on all three surfaces.
  - **M6 resolved.** Four tasks named a directory and a description instead of a file. T016 now names
    `src/Parley.Setup/ProjectCommand.cs`, T018 `tests/Parley.Store.Tests/MigrationDiscoveryTests.cs`, T098
    `tests/Parley.Mcp.Tests/PluginTests.cs` and `tests/Parley.Cli.Tests/RenameTests.cs`, T099
    `src/Parley.Setup/Enrolment.cs` and `InvocationCommand.Prompt`.
  - **M7 resolved.** Upload staging moved out of the location to `<store>.files-incoming/` beside it (the owner's call),
    so the folder a person opens and the watcher watches never hold a half-uploaded file; `StoreFiles` gains a second
    suffix so destroying a project removes it. research.md R2, data-model.md, wire.md, plan.md, T016 and T022 carry it.
  - **C2 resolved.** Constitution 2.0.0 Principle IV bounds lists and notices per call, and neither the agent's file list
    nor inbox notices had a bound. The owner chose the paging Parley's operator lists already use, for both file lists (50
    by default, 200 at most, a next-page token; the location's total counts every file on every page), and a ceiling of
    200 notices per inbox check, oldest change first, with the count still waiting; the inbox `limit` still bounds
    messages only. FR-021, FR-024, US3 scenarios 1 and 6, the many-notices edge case, SC-005 and the new SC-017 carry it;
    research.md R7 and the new R17, data-model.md, both contracts, plan.md, and T003, T005, T036, T037, T039, T041, T044,
    T048 to T052 and T054 to T057 follow.
- **Second `/speckit-analyze` run (2026-09-14), every finding resolved before implementation.**
  - **C4 resolved** (the owner's call: 200 each). An ambiguity refusal names at most 200 conversations with a count of the
    rest; a removal request naming more than 200 files is refused whole, for agents and the person. FR-010, FR-026,
    FR-029, SC-017, research.md R17, wire.md (`conversations_more`, `FILE_REJECTION_TOO_MANY_NAMES`), commands.md, T059,
    T064, T065. The existing `assignable_conversations` of a refused Request (protocol 0.5) is recorded, not changed.
  - **H4 resolved.** A folder name stored on every file row could not be unique across conversations and was lost with a
    conversation's last file. It now has its own table, `conversation_folders`, kept until the conversation is destroyed:
    five tables. data-model.md, research.md R2/R3/R15, plan.md, spec Key Entities, T004, T017–T019, T021, T022, T038.
  - **H5 resolved.** `FileLocationReply` carries a `FileRefusal`, so opening an ambiguous name can list its
    conversations (US7 scenario 6). T082.
  - **M8–M12 resolved.** FR-024 names the two paged operator lists instead of claiming all are; `name_key` added to
    `file_fetches` and `file_notices`; `ConversationFilesTotal`, `FilePlacementCount` and `PlaceOutcome` defined; a
    rename is an update in place that keeps a purged placer (R6, T017, T038); tests added for destroy notices (T036),
    project destroy removing both folders (T016), a full disk (T023) and a closed conversation keeping its files (T073).
  - **L1** (the owner's call): T046 became a test that a seat's drain ignores notices. **L2** (the owner's call):
    `parley_files` became `parley_file_list`, and the inbox reply's count is `file_notices_waiting`, the name `Waiting`
    already uses. **L3**: FR-028 and FR-041 cross-reference FR-018 and FR-015 rather than restate them. **L4**:
    `FetchedFile` is refused unless it matches a stream the broker served that agent (FR-019, R7, wire.md, T028, T032).
    **L5**: antiforgery refusals in T066, T074, T083, T091. **L6**: R4's stale 128 and R10's superseded decision
    corrected. **L7**: the spec's opening speaks of SPEC §1 in the past tense and cites no analysis ID. **L8**: zero-byte
    file and the name-length message in T027.
- **Third `/speckit-analyze` run (2026-09-14).**
  - **H6 resolved** (the owner's call: purge takes the files). Purge deletes every conversation an agent took part in, and
    placing a file makes the placer a participant, so a purged placer's files go with those conversations: bytes deleted,
    counted in purge's toll, "gone" notices raised. The edge case now separates retired (files stay) from purged, FR-031
    lists purge, research.md R3/R6 and data-model.md follow, and T017, T021, T036 and T038 no longer test a purged placer
    keeping a file.
  - **M13 resolved.** Destroying or purging now deletes the folder on disk once downloads finish (T047, with tests).
  - **M14 resolved.** A hand-copied or renamed file whose name breaks any name rule is ignored like a wrong extension
    (spec edge cases, R6, T038).
  - **L9, L10 resolved.** A refused `FetchedFile` is reported by the tool, and concurrent fetches are each reportable (R7,
    commands.md, T028, T034); a folder already on disk counts as taken when a name is chosen (R2, data-model.md, T022,
    T023).
- The spec deliberately includes a "Why this is a protocol change" section, because it reverses SPEC.md §1's "never
  files" and the plan must change SPEC.md before code.
