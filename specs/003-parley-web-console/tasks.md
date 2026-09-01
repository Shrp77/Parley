---
description: "Task list for the Parley web console"
---

# Tasks: Parley Web Console

**Input**: Design documents from `/specs/003-parley-web-console/`
**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/operators-additions.md](./contracts/operators-additions.md)

**Tests**: Included, and not as a style preference. The constitution makes a green build the whole
lint gate, requires every claim the documents make to be checked, and requires an invariant to be
verified by reconstructing the defect it exists for. Two of the tasks below add SQL to the store
and two add RPCs to a service boundary; shipping either untested would breach that directly.

**Organization**: By user story, so each is independently implementable and shippable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable — different files, no dependency on incomplete work
- **[Story]**: which user story the task serves

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The two new projects exist, build clean under the repository's gates, and are in the
solution.

- [X] T001 Create `src/Parley.Web/Parley.Web.csproj` targeting `net10.0` with `Microsoft.NET.Sdk.Web`, referencing `src/Parley.Transport` and `src/Parley.Configuration` and nothing else
- [X] T002 [P] Create `tests/Parley.Web.Tests/Parley.Web.Tests.csproj` with `<OutputType>Exe</OutputType>`, `IsTestProject`, `NoWarn=CS1591` and the `BannedApiAnalyzers` block, copying the shape of `tests/Parley.Spec.Tests/Parley.Spec.Tests.csproj`
- [X] T003 Add both projects to `Parley.slnx` under a real folder name, never `/`
- [X] T004 Add any new package versions to `Directory.Packages.props` only — a `Version=` attribute in a csproj is a restore error under CPM. *(None were needed: ASP.NET Core comes from the shared framework via `Microsoft.NET.Sdk.Web`, `Grpc.Net.Client` arrives transitively from `Parley.Transport`, and `xunit.v3` was already centralised.)*
- [X] T005 Confirm `dotnet build` is clean with zero warnings; documentation is enforced, so every public member in `Parley.Web` needs an XML comment whose `<remarks>` carries *why*

**Checkpoint**: Both projects build; nothing else has changed.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Everything every story needs — reaching the brokers, containing the ones that cannot
be reached, and binding safely. No user story can start until this is done.

- [X] T006 Implement `src/Parley.Web/BrokerDirectory.cs`: read the project manifest through `Parley.Configuration` and construct one `Operators` client per project, keyed by (project, endpoint)
- [X] T007 Give each client a connect timeout in the low seconds, matching `src/Parley.Mcp/BrokerLink.cs` and for the same reason — a stopped broker is stopped, and waiting longer does not find it (SC-004)
- [X] T008 Contain a failing project in `BrokerDirectory`: an unreachable broker marks that project unreachable and MUST NOT propagate to any other project's view (FR-004)
- [X] T009 Contain a version-mismatched project the same way, naming both the console's protocol version and the broker's, without refusing to start (FR-026)
  - *The operator surface exposes no protocol version.* `protocol_version` is a field on
    `AttachRequest` only — the Agents flow. No message on `service Operators` carries one in
    either direction, so an operator client cannot learn what version a broker speaks and
    FR-026 cannot be implemented as written. This is a third gap of the same class as the two
    in `plan.md`'s Gate 1, and it belongs in the same `SPEC.md` §10.2 change (T014). Until it
    is decided, a mismatched broker is contained as merely unreachable, which is true but
    unhelpful — it names the wrong cause.
- [X] T010 Bind loopback by default in `src/Parley.Web/Program.cs`; a non-loopback binding requires explicit configuration and announces itself at startup (FR-022, §16)
- [X] T011 Configure logging in `Program.cs` to record requests and errors and never message text or payloads (FR-031)
- [X] T012 [P] Create the shared layout in `src/Parley.Web/Pages/Shared/`, keyboard navigable and screen-reader legible, with a visible "as of" indicator the list views will fill in (FR-024, FR-029)
- [X] T013 [P] Add `tests/Parley.Web.Tests/BrokerDirectoryTests.cs` covering: one unreachable project does not affect another; a version mismatch is contained rather than fatal; two projects holding an agent of the same name stay distinct (FR-002, FR-004, FR-026)

**Checkpoint**: The console starts, reads the manifest, reaches the brokers it can, and names the
ones it cannot. No page renders domain data yet.

---

## Phase 3: User Story 1 — Read a conversation end to end (Priority: P1) 🎯 MVP

**Goal**: An operator finds a discussion and reads the whole of it, correctly attributed.

**Independent test**: Seed the §5.0 three-participant case — `orders` asks `billing`; `orders`
asks `inventory` under the same identifier; then `billing` asks `inventory` under it — answer
some and leave one open. Open the console and reconstruct who asked what, in what order, and who
saw which message, without reading a database or running a command.

### The specification change comes first — Gate 1

> A pull request that reverses T014 and T015 is the defect principle I exists to prevent.

- [X] T014 [US1] Describe broker V1 in `SPEC.md` §10.2: what a conforming broker exposes to an operator console, and what an agent must be able to receive from the operator. State that the surface says which version it is, and that the two are separate contracts because an agent must never acquire an operator command
- [X] T015 [US1] Run `tests/Parley.Spec.Tests/bin/Debug/net10.0/Parley.Spec.Tests.exe` directly and confirm green: references resolve, no table is ragged, every schema fragment still executes
- [X] T016 [US1] Preserve `SPEC.md`'s encoding when editing — BOM, CRLF, zero bare LF. A pattern anchored to end-of-line otherwise matches everything on two runners and nothing on the third

### Wire and store

- [X] T014a [US1] Declare `IParleyBrokerClient` in `src/Parley.Core/` in domain types — `AgentSummary`, `Message`, `Exchange`, `ConversationState`, `WaitForGraph` already live there, which is why the contract belongs there and pulls in nothing
- [X] T014b [US1] Declare the agent-facing instruction interface beside it, carrying no operator command whatsoever (FR-035)
- [X] T014c [US1] Add `tests/Parley.Core.Tests` cases asserting the agent-facing interface declares nothing that retires, purges, force-closes or prunes — the split made structural rather than remembered
- [X] T017 [US1] Add `ListConversationsRequest`, `ListConversationsReply`, `ConversationSummary` and the `ListConversations` rpc to `proto/parley.proto`, on `service Operators` only. The request carries a participant, a time window and paging, and **no project identifier**: membership is which endpoint the caller connected to, and a field here would give the protocol the concept §4.1 says it must not acquire (FR-003)
- [X] T018 [US1] Add `ExchangeState` and the repeated field to `ExportReply` in `proto/parley.proto` — a new field number beside the existing one, so an older client is unaffected. No project identifier on either message (FR-003)
- [X] T019 [US1] Implement the conversation listing query in `src/Parley.Store/ConversationRepository.cs`: participants derived from the exchanges, ordered by most recent activity, paged, parameterised (CA2100 is an error)
- [X] T020 [US1] Implement the exchange-state read in `src/Parley.Store/ExchangeRepository.cs`, returning the §6 state for every exchange in a conversation
- [X] T021 [P] [US1] Add `tests/Parley.Store.Tests` cases for both queries: derived participants match the exchanges; paging is stable across pages; a conversation with one exchange behaves; an agent that gate-crashed a conversation appears as a participant because it is party to an exchange, and nothing asserts agreement
- [X] T022 [US1] Add `ListConversationsAsync` and the exchange-state read to `src/Parley.Broker/OperatorConsole.cs` — never to `Broker`, so `SurfaceBoundaryTests` does not move
- [X] T023 [US1] Bind both to `src/Parley.Server/OperatorsService.cs`; confirm nothing was added to `AgentsService` (FR-021)
- [X] T024 [P] [US1] Add `tests/Parley.Server.Tests` cases over a real loopback socket: the listing pages, the server bounds `page_size` regardless of what the caller asks for; neither operation is reachable on `Agents`; and neither new request nor reply carries a project identifier, so the fan-out stays entirely client-side (FR-003, FR-021, SC-007)
- [X] T025 [US1] Confirm `tests/Parley.Broker.Tests/SurfaceBoundaryTests.cs` still passes unchanged (FR-021, SC-007)

### The console

- [X] T026 [US1] Implement the visibility derivation in `src/Parley.Web/Rendering/Visibility.cs` exactly as `data-model.md` states it: participants are the union of `from`/`to`; a message's readers are its own pair; non-readers are the difference (FR-010)
- [X] T027 [P] [US1] Add `tests/Parley.Web.Tests/VisibilityTests.cs` reconstructing the three-participant case and asserting that the message `billing` sent to `orders` is marked not seen by `inventory` — the defect the annotation exists to prevent
- [X] T028 [US1] Build the conversation list page in `src/Parley.Web/Pages/Conversations/Index.cshtml`, filterable by participant and by time, scoped to the selected project and never across projects (FR-015, FR-032)
- [X] T029 [US1] Build the transcript page in `src/Parley.Web/Pages/Conversations/Detail.cshtml`: ordered by the export's own order, every message naming its pair, annotations inline rather than behind a control (FR-008, FR-009, FR-011)
- [X] T030 [US1] State on the page that ordering follows the broker's record rather than any wall clock, and show `created_utc` and `delivered_utc` as information (FR-014)
- [X] T031 [US1] Render exchange state — open, answered, concluded, orphaned, ghosted — and never render an unresolved exchange as resolved (FR-012)
- [X] T032 [US1] Render responder identity per message, including `parley:broker` for a forced `unknown` (FR-013)
- [X] T033 [US1] Carry pair and visibility as text, with colour or iconography only reinforcing them (FR-030)
- [X] T034 [US1] Show a staleness notice when the open conversation has gained messages, and do not append to the view silently (FR-025)
- [X] T035 [US1] Implement the readable export as a handler on `Conversations/Detail`, writing through `src/Parley.Web/Rendering/Transcript.cs` — the rendered transcript with attribution and visibility intact (FR-027, SC-008)
- [X] T036 [US1] Implement the raw export of the messages as the operator surface returns them (FR-028)
- [X] T037 [P] [US1] Add `tests/Parley.Web.Tests/RenderingTests.cs`: a message whose text contains markup and text imitating the console's own annotation renders as content, changes nothing about the page, and cannot be mistaken for the console speaking (FR-016)
- [X] T038 [P] [US1] Add a test asserting the readable export still states, for every message, which participants saw it (SC-008)

**Checkpoint**: User Story 1 is complete and shippable on its own. This is the MVP.

---

## Phase 4: User Story 2 — See every project and who is registered in it (Priority: P2)

**Goal**: The state of the deployment at a glance.

**Independent test**: With two projects configured and one broker stopped, the console lists both,
shows the running one's agents with their presence, and reports the stopped one as unreachable
without blocking the page.

- [X] T039 [US2] Build the projects page in `src/Parley.Web/Pages/Index.cshtml`, listing every project from the manifest with its reachability — the only page that is not project-scoped (FR-001, FR-004, FR-032)
- [X] T040 [US2] Handle the no-projects-configured case explicitly: say that none are named and how to name one, rather than rendering an empty list indistinguishable from a fault
- [X] T041 [US2] Build the agents page in `src/Parley.Web/Pages/Projects/Agents.cshtml` from `DescribeRegistry`: name, `owns`, tier, presence (FR-005)
- [X] T042 [US2] Show a retired agent as retired rather than omitting it, and let retirement outrank presence (FR-006)
- [X] T043 [US2] Show an absent `owns` line as not stated, and never substitute a description (FR-007)
- [X] T044 [US2] Refresh the project and agent list views without operator action, showing how current the display is (FR-024)
- [X] T045 [P] [US2] Add `tests/Parley.Web.Tests/RosterTests.cs`: retired outranks presence; an absent `owns` renders as absent; two projects with an agent of the same name never merge (FR-002, FR-006, FR-007)

**Checkpoint**: Stories 1 and 2 both work, independently.

---

## Phase 5: User Story 3 — Learn how to register a new agent (Priority: P3)

**Goal**: Somebody adding a repository can do it from this page alone.

**Independent test**: Give the instructions to somebody who has not registered an agent before and
confirm they succeed without consulting another document.

- [X] T046 [US3] Build the registration page in `src/Parley.Web/Pages/Projects/Register.cshtml`, naming the selected project's endpoint rather than a placeholder (FR-017)
- [X] T047 [US3] State that an agent's identity comes from its launch configuration and cannot be supplied by any call (FR-018, constitution III)
- [X] T048 [US3] Describe how to confirm the registration succeeded — the agent appearing in that project's roster (FR-019)
- [X] T049 [US3] Ensure no resolved credential value can appear on this page or anywhere else; credentials appear only as the references they are configured as (FR-020)
- [X] T050 [P] [US3] Add `tests/Parley.Web.Tests/RegistrationGuidanceTests.cs` asserting the endpoint is the selected project's and that no resolved credential value is rendered

**Checkpoint**: All three stories work independently.

---

## Phase 6: User Story 4 — Act on what you find (Priority: P2)

**Goal**: The operator fixes what they found, from where they found it.

**Independent test**: Retire an agent from the console and see it read as retired; reinstate it and
see it not; instruct an agent to change what it says it owns and see the change appear after that
agent next starts.

- [X] T063 [US4] Add the operator commands to `IParleyBrokerClient` and implement them over the wire in `src/Parley.Web/`: retire, reinstate, purge, force-close, prune
- [X] T064 [US4] Build the confirmation step in `src/Parley.Web/Pages/Projects/Act.cshtml`, stating what will be destroyed before it is (FR-033, SC-009)
- [X] T065 [US4] Surface the health reads the broker already offers — what was refused for being too long, clarify depth, referrals, notification volume — on a per-project page
- [X] T066 [US4] Show who is waiting on whom, so an operator can see a discussion nobody will finish
- [X] T067 [US4] Implement the instruction path: the console asks the broker, the broker asks the machine's node, the node writes to the standard input it already owns (§4)
- [X] T068 [US4] Record an instruction as coming from the operator and never as peer traffic (FR-034)
- [X] T069 [US4] Report an instruction to an agent on an unreachable machine as undelivered rather than as sent
- [X] T070 [P] [US4] Add `tests/Parley.Web.Tests` cases: a purge without confirmation does nothing; an instruction is attributed to the operator; an agent may refuse an instruction and that is not a failure
- [X] T071 [P] [US4] Add a `tests/Parley.Server.Tests` case asserting no operator command is reachable on the agent-facing service

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T051 Bump `VersionPrefix` in `Directory.Build.props` **in the same change**, because this alters shipped behaviour in `src`. A rebuilt tool at a version already on PATH is not installed, and the failure is silent
- [X] T052 [P] Update `CLAUDE.md`'s Layout section to describe `src/Parley.Web` and `tests/Parley.Web.Tests`, including why the console is a client of the broker rather than part of it
- [X] T053 [P] Add the two new test projects to the CI matrix so they run on Windows, Linux and macOS
- [X] T054 Verify the console has no SQLite dependency at all — not a package reference, not a connection string. `Parley.Server` is the only thing that opens the database
- [X] T055 Verify nothing in the rendering path emits a message payload raw; Razor encodes by default, so the rule is that nothing opts out for a message body (FR-016)
- [X] T056 Verify every acting control describes what it will do before it does it, that none of them is reachable by following a link, and that the frame does not tell a reader the console is read-only (FR-023, FR-033)
- [X] T057 Walk `quickstart.md` end to end on a real deployment, including the step that matters most: read the transcript, write down who saw what, and compare it against the exchanges you created; and that somebody who has never registered an agent succeeds from the instructions alone (SC-001, SC-002, SC-003, SC-006)
- [X] T058 Run the full suite from the test hosts directly — `tests/Parley.*.Tests/bin/*/net10.0/Parley.*.Tests.exe` — rather than trusting `dotnet test`, which reports `Zero tests ran` on at least one machine here while the suite is fine
- [X] T059 Run a case-insensitive `git grep` for the originating project's names before committing, excluding `.gitignore`
- [X] T060 Run the three review lenses **after the last edit, not alongside it** — correctness and concurrency; security and data integrity; API surface and maintainability — rotating and re-running each after its fixes, to convergence rather than to a fixed count. Record how many rounds it took
- [X] T061 Verify CI ran against `HEAD` by comparing the run's commit against `git rev-parse HEAD`, rather than trusting a green that belongs to the previous run
- [X] T062 Seed a store with roughly twenty agents and twelve months of exchanges, then confirm any conversation is reachable within three interactions of opening the console and that the listing pages rather than degrading (SC-005)

---

## Dependencies

```text
Phase 1 (Setup)
      ↓
Phase 2 (Foundational) ── blocks every story
      ↓
      ├─────────────────────────────┬──────────────────────────┐
      ↓                             ↓                          ↓
Phase 3: US1 (P1) MVP        Phase 4: US2 (P2)         Phase 5: US3 (P3)
  T014-T016 SPEC.md first
      ↓
  T017-T025 wire + store
      ↓
  T026-T038 console
      ↓
      └─────────────────────────────┴──────────────────────────┘
                                    ↓
                          Phase 6 (Polish)
```

**Story independence**: US2 and US3 need nothing from US1 — they run entirely on
`DescribeRegistry` and the manifest, both of which exist today. Only US1 needs the protocol
change, which is why the §10.2 work sits inside its phase rather than in Foundational: putting it
there would have made two shippable stories wait on a specification edit they do not need.

**The one hard ordering**: T014 before T017. The specification describes the operation before the
wire carries it.

## Parallel execution examples

**Phase 2**: T012 and T013 run alongside T006–T011 — different files, no shared state.

**Phase 3, after T018**: T019 and T020 are different repositories and can proceed together, as can
their tests T021 and T024. T026 and T027 can be written against the derivation before any page
exists.

**Phase 3, console block**: T030 through T034 all edit the transcript page and must be
sequential. T035 and T036 are separate files and parallelise; T037 and T038 are tests and
parallelise with each other.

**Across phases**: once Phase 2 is checkpointed, US2 (T039–T045) and US3 (T046–T050) can be built
by another pair of hands entirely while US1 is in progress.

## Implementation strategy

**MVP is Phase 3 alone.** User Story 1 is the capability nothing else provides: the roster is
already available from the command line, and the shape of a discussion is not. A console that
only read conversations would be worth shipping.

**Then increment.** US2 makes US1 navigable and answers "is my deployment healthy" without a
command per project. US3 is guidance and can follow at any time.

**One warning about sequencing.** T014 is a change to the primary artifact of this repository, and
the plan gates the whole feature behind it. If review rejects either operation as specified, the
work that depends on it is T017–T038 — the entire MVP. Get §10.2 agreed before building on it,
not after.

## Task counts

| Phase | Tasks |
|---|---|
| Setup | 5 |
| Foundational | 8 |
| US1 (P1) | 25 |
| US2 (P2) | 7 |
| US3 (P3) | 5 |
| US4 (P2) | 9 |
| Polish | 12 |
| **Total** | **74** |

## Review record

Eleven rounds, run after the last edit and rotating the three lenses. Rounds one to ten each
found something; round eleven found nothing above low severity, which is where it stopped.

| Round | Found |
|---|---|
| 1 | The console's `BrokerDirectory` is a singleton behind a web server and kept its channels in a plain `Dictionary`: not a duplicated channel under race but a bucket chain a later read never returns from. Project probes ran in sequence, so the page cost the sum of the timeouts. An unreachable broker produced an empty export rather than no export. A malformed agent name threw out of a page handler. |
| 2 | An instruction reached the agent as bare text, against §11.5's requirement that it be marked as the operator's -- so an agent could not tell it had been instructed, and could not knowingly exercise the refusal the same section grants. A payload carrying a newline could forge a visibility annotation in the text export. |
| 3 | Round two's flattening escaped backslashes as well as line breaks, doubling them in every real JSON payload to defend a case `json_valid` does not permit. |
| 4 | The staleness poll reported "nothing has arrived" when it could not reach the broker at all, so a transcript asserted it was current for as long as the tab stayed open. A duplicate exchange id would have thrown the page rather than rendering it. |
| 5 | A participant filter that could not be parsed was dropped, returning every conversation in the project -- not an empty result somebody would question but a full one they would read as the answer. |
| 6 | The same silent widening one level down, in the broker, where no page is there to say so. An over-long instruction failed as a constraint violation rather than as a refusal naming the cap. |
| 7 | Nothing. |
| 8 | Walking the quickstart against a real broker: `waiting` was declared, wired into the client and rendered on a page, and never bound on the server -- nothing had ever asked for it through the transport. The console served no stylesheet when started from any directory but its own, because the content root defaults to the working directory. Neither was reachable from the test suite. |
| 9 | `waiting` had an implementation and no specification: §10.2 never declared it. The check that should have caught that was reading a hand-kept copy of the operation list rather than the document, so it now reads SPEC.md, in both directions. |
| 10 | The agent-surface check enumerated operator operations by hand, so the operation added in round eight was never among the ones it looked for. It reflects over the operator client now. |
| 11 | Nothing. |

The two findings in round eight are the argument for T057. Both were invisible to a green suite:
`WebApplicationFactory` sets the content root from the test assembly, so the console is only ever
wrong where nothing was testing, and no test had asked for `waiting` over a socket. A suite that
passes and a program that runs are different claims.

### The subcommand, reviewed separately

`parley console` was added after the rotation above had converged, so it was rotated again.
Six rounds; the last two found nothing.

| Round | Found |
|---|---|
| A | Naming a project was silently ignored, and a malformed address died several frames deep inside Kestrel. |
| B | The console listed nothing on a machine configured entirely by explicit paths — which is how this deployment actually runs, and is what somebody sees first after installing. Every other command reaches the broker there. |
| C | The fallback was a second nullable string beside the manifest path, and a swapped call read an address as a path that does not exist: indistinguishable from an unconfigured machine. It has its own type now, so the swap is a compiler error. |
| D | Nothing. |
| E | Installing it and looking: the stylesheet 404d from `parley console` while working from the build output, because `wwwroot` is not carried into a tool package. Inlined, and the static-asset machinery switched off. |
| F | Nothing. |

Round E is the second time in this feature that installing or starting the thing found what a
green suite could not, and the reason is the same both times: the tests run in a shape the
deployed program never has. Reviewing an install by reading it does not work.

A defect in `install.ps1` surfaced alongside it, though not from a lens: it called
`autostart install` with the store alone, so reinstalling over a working deployment silently
dropped `--node-config` and left the broker starting without the agents it hosts. Windows now
reads the catalogue back out of the entry it is replacing; there is no portable equivalent, so
the POSIX script takes the flag and says what happens without one.

### A third rotation, over the branch as installed

Run after the console was installed and pointed at the live deployment, so the probes were
against a running broker rather than a fixture. Three rounds; the last two found nothing.

| Round | Found |
|---|---|
| i | Paging dropped conversations that share an instant. The order breaks ties on the identifier and the resume bound did not, so a page boundary landing inside such a group skipped every remaining member. Not contrived: the Windows system clock ticks about every 15.6ms, so two agents answering at the same moment are written with the same timestamp — and the conversation that vanishes is simply absent. The resume now carries the identifier too, and the wire token carries both halves. |
| ii | Nothing. The console was checked across a broker restart — it reports the project unreachable while the broker is down and lists everything again when it returns — and a real six-participant conversation was read through it: thirty messages, thirty visibility annotations, each naming the four agents not party to that message. |
| iii | Nothing. |

The tie in round i is the third defect in this feature that a green suite did not show, and the
first two were also found by running the thing. The pattern is worth stating plainly: the fixture
that exercises paging spaced its conversations 36 hours apart, so it could not produce the one
input that breaks the query it was written to test.
