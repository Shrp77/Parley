# Tasks: A pinned graph of the work, and freezing an agent

Written after the build, in the order it was done; every task is built and has a test.

## Phase 1: the freeze in the store, broker and server
- [x] T001 Migration `011_agent_hold.sql`: `agents.frozen_utc` with the instant CHECK; SPEC.md §9 schema shows it. (`AgentFreezeTests`, `SchemaConformanceTests`)
- [x] T002 `AgentRecord.FrozenUtc`/`Frozen`; `AgentRepository` reads it, `FreezeAsync`/`UnfreezeAsync` write it guarded on the current value and refuse the person seat and the facilitator. (`AgentFreezeTests`)
- [x] T003 `OperatorConsole.FreezeAsync`/`UnfreezeAsync`; `Operators.Freeze`/`Unfreeze` in the proto, `FreezeReply`, `frozen_utc` on `RegistryRow`.
- [x] T004 `BrokerRuntime.Ask` refuses a frozen agent with `WakeOutcome.Frozen` (`WAKE_OUTCOME_FROZEN`); `_frozen` seeded from the store at start. `Start` refuses a frozen agent in words. (`FreezingAnAgentTests`)
- [x] T005 `Freeze` records, then `RequestStop`; `Unfreeze` clears, then wakes if the agent owes work under the resume policy. (`FreezingAnAgentTests`)
- [x] T006 Existing migration tests that rewind a store learn the new column. (`MigrationSixTests`, `HistoryReadsSchemaTests`, `AFailedMigration…`, `MigrationDiscoveryTests`)

## Phase 2: client and the freeze on every surface
- [x] T007 `IParleyBrokerClient.FreezeAsync`/`UnfreezeAsync`, `BrokerClient`, `FreezeResult`/`FreezeProcess`.
- [x] T008 `AgentFreezing` (every project that serves the agent, one outcome, unreachable projects named), `FreezingWords`, `Standing.Frozen`/`Freezing`. (`AgentGraphTests`)
- [x] T009 Web: `Act` arms `freeze`, `unfreeze`, `holdall`, `resumeall`, described on GET and done on POST; Freeze/Unfreeze on the agent page. (`GraphPanelTests`, `EveryActionHasADoorTests`)
- [x] T010 Terminal: Freeze/Unfreeze in an agent's row menu through `AgentFreezing`. (`DriftTests`, `DeadSurfaceTests`)
- [x] T011 Senders told: `WakeOutcome.Frozen` in `parley_request` replies and the seat's list.

## Phase 3: the graph
- [x] T012 `AgentGraphBuilder.Build`: nodes, edges, bands, layout, caps, muted closed conversations. (`AgentGraphTests`)
- [x] T013 `Pages/Graph` fragment: SVG, list alternative, notes for what is not drawn. (`GraphPanelTests`)
- [x] T014 Layout: tab, panel, pin, hide-closed, 15-second refresh while open, nothing in a frame. (`GraphPanelTests`)

## Phase 4: project-wide
- [x] T015 `AgentFreezing.AllAsync`; `holdall`/`resumeall` linked from the graph.

## Phase 5: spec, docs, version
- [x] T016 SPEC.md §10.2 rows, §11.1 paragraph, protocol 0.20, schema 11; `docs/operating.md`; CLAUDE.md state; version 0.64.0.
