# Tasks: A pinned graph of the work, and holding an agent

Written after the build, in the order it was done; every task is built and has a test.

## Phase 1: the hold in the store, broker and server
- [x] T001 Migration `011_agent_hold.sql`: `agents.held_utc` with the instant CHECK; SPEC.md §9 schema shows it. (`AgentHoldTests`, `SchemaConformanceTests`)
- [x] T002 `AgentRecord.HeldUtc`/`Held`; `AgentRepository` reads it, `HoldAsync`/`ResumeAsync` write it guarded on the current value and refuse the person seat and the facilitator. (`AgentHoldTests`)
- [x] T003 `OperatorConsole.HoldAsync`/`ResumeAsync`; `Operators.Hold`/`Resume` in the proto, `HoldReply`, `held_utc` on `RegistryRow`.
- [x] T004 `BrokerRuntime.Ask` refuses a held agent with `WakeOutcome.Held` (`WAKE_OUTCOME_HELD`); `_held` seeded from the store at start. `Start` refuses a held agent in words. (`HoldingAnAgentTests`)
- [x] T005 `Hold` records, then `RequestStop`; `Resume` clears, then wakes if the agent owes work under the resume policy. (`HoldingAnAgentTests`)
- [x] T006 Existing migration tests that rewind a store learn the new column. (`MigrationSixTests`, `HistoryReadsSchemaTests`, `AFailedMigration…`, `MigrationDiscoveryTests`)

## Phase 2: client and the hold on every surface
- [x] T007 `IParleyBrokerClient.HoldAsync`/`ResumeAsync`, `BrokerClient`, `HoldResult`/`HoldProcess`.
- [x] T008 `AgentHolding` (every project that serves the agent, one outcome, unreachable projects named), `HoldingWords`, `Standing.Held`/`Holding`. (`AgentGraphTests`)
- [x] T009 Web: `Act` arms `hold`, `resume`, `holdall`, `resumeall`, described on GET and done on POST; Hold/Resume on the agent page. (`GraphPanelTests`, `EveryActionHasADoorTests`)
- [x] T010 Terminal: Hold/Resume in an agent's row menu through `AgentHolding`. (`DriftTests`, `DeadSurfaceTests`)
- [x] T011 Senders told: `WakeOutcome.Held` in `parley_request` replies and the seat's list.

## Phase 3: the graph
- [x] T012 `AgentGraphBuilder.Build`: nodes, edges, bands, layout, caps, muted closed conversations. (`AgentGraphTests`)
- [x] T013 `Pages/Graph` fragment: SVG, list alternative, notes for what is not drawn. (`GraphPanelTests`)
- [x] T014 Layout: tab, panel, pin, hide-closed, 15-second refresh while open, nothing in a frame. (`GraphPanelTests`)

## Phase 4: project-wide
- [x] T015 `AgentHolding.AllAsync`; `holdall`/`resumeall` linked from the graph.

## Phase 5: spec, docs, version
- [x] T016 SPEC.md §10.2 rows, §11.1 paragraph, protocol 0.20, schema 11; `docs/operating.md`; CLAUDE.md state; version 0.64.0.
