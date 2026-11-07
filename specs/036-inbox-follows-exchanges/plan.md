# Implementation Plan: The Inbox follows an exchange after it is answered

**Branch**: `035-material-3-web-ui` (owner, 2026-11-06: "all in 35") | **Date**: 2026-11-06 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/036-inbox-follows-exchanges/spec.md`

## Summary

The Inbox gains *In flight* and *Closed* beside *Waiting on you*, every row links to its conversation and offers a
follow-up, and a message's `exchange #N` references open the exchange under the line they are on. Everything shown is
derived from what the broker already keeps: the wait-for graph, the registry, the flags report and conversation exports.
One new operator read (`LocateExchange`) maps an exchange number to its conversation, and one machine file
(`<root>/settings.json`) holds the closed window. The derivations live once in Parley.Client so the web console and the
terminal show the same rows and words.

## Technical Context

**Language/Version**: C# on .NET 10
**Primary Dependencies**: ASP.NET Core Razor Pages (web), Terminal.Gui v2 (terminal), gRPC (transport), Markdig (Markdown)
**Storage**: none new in the broker. One machine file, `<root>/settings.json`, through Parley.Configuration.
**Testing**: xUnit v3 on Microsoft.Testing.Platform; `[Unit]` / `[Integration]`; web tests over the scripted broker
(`RoadmapConsole`), terminal tests through the harness, server tests over loopback for the new read.
**Target Platform**: Windows, Linux, macOS (local, loopback broker)
**Project Type**: protocol reference implementation with two operator surfaces
**Performance Goals**: a view change shows within 10 s (SC-003), from the 5 s digest poll
**Constraints**: reading never changes state (FR-015); no new stored state about the Inbox (FR-008); one message view and
one text box on the web; every terminal key declared in a command table
**Scale/Scope**: tens of agents and hundreds of exchanges per project

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Holds? | How |
|---|---|---|
| I. The specification is the product | Yes | SPEC.md §5.6 states what the views show and the reference form; §10.2 lists `LocateExchange`. Written in the same change as the code. |
| II. No authority between agents | Yes | Nothing new is done to an agent. A follow-up is a person's Request, which §5.6 already allows. The facilitator is not offered follow-up. |
| III. Identity fixed by the launch | Yes | No new caller-supplied identity. Surfaces speak as the person seat, as today. |
| IV. Terseness | Yes | The instruction bullet is one line. No new message type; no message grows. |
| V. Invariants in storage | Yes | No new protocol invariant; no schema change. |
| Agent and operator operations apart | Yes | `LocateExchange` is on `Operators` only; `SurfaceBoundaryTests` keeps `Broker` from growing. |
| Payloads are inert | Yes | A reference is drawn as a control by a surface; the broker never reads one, and nothing addresses by it. |
| A person's line is a Request | Yes | Follow-up sends a Request in the row's conversation and never reopens the old exchange. |

No violations. Re-checked after Phase 1: unchanged.

## Project Structure

### Documentation (this feature)

```text
specs/036-inbox-follows-exchanges/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/interfaces.md
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
proto/parley.proto                          LocateExchange
src/Parley.Core/                            IParleyBrokerClient: LocateExchangeAsync
src/Parley.Store/ExchangeRepository.cs      conversation of an exchange (read)
src/Parley.Broker/OperatorConsole.cs        LocateExchangeAsync
src/Parley.Server/                          Operators service binding
src/Parley.Configuration/ParleySettings.cs  <root>/settings.json
src/Parley.Client/BrokerClient.cs           LocateExchangeAsync
src/Parley.Client/Seating/Inbox*.cs         InboxRow, AgentStanding, ExchangeEnding, InboxReading (the three views)
src/Parley.Client/Rendering/ExchangeReferences.cs   the grammar
src/Parley.Client/Seating/ExchangeDetail.cs         one exchange for the panel and the dialog
src/Parley.Client/Seating/StillOpen.cs      "exchange #N" in its own words
src/Parley.Mcp/ParleyInstructions.cs        the instruction bullet
src/Parley.Web/MarkdownText.cs              references become buttons
src/Parley.Web/Pages/Inbox.cshtml(.cs)      the three views, follow-up
src/Parley.Web/Pages/Exchanges/Panel.cshtml(.cs)    the panel fragment
src/Parley.Web/Pages/Settings.cshtml(.cs)   the closed window
src/Parley.Web/Pages/Shared/_Layout.cshtml  the panel script and styles
src/Parley.Terminal/InboxView.cs, ExchangeView.cs, Shell.cs, ShellCommands.cs, KeyContext.cs, CommandTables.cs
plugins/parley/README.md                    the reference form
SPEC.md                                     §5.6, §10.2
tests/…                                     one test class per story, plus the grammar and the settings file
```

**Structure Decision**: the existing projects, each change where its layer already is. Shared derivations in
Parley.Client so the two screens cannot differ.

## Complexity Tracking

None.
