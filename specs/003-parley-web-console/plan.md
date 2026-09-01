# Implementation Plan: Parley Web Console

**Branch**: `003-parley-web-console` | **Date**: 2026-08-30 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/003-parley-web-console/spec.md`

## Summary

An operator-facing web console that lists the projects configured on this machine, the agents
registered in each, and the conversations held in each — the last rendered as an annotated chat
transcript on a timeline — and acts on what it finds: retiring, reinstating, purging,
force-closing, and instructing an agent.

Its capabilities are declared as two interfaces in `Parley.Core`, together called broker V1: one
for what a conforming broker exposes to an operator console, one for what an agent must be able to
receive from the operator. An implementation satisfies them or fails to compile. Neither this plan
nor the specification restates their members, because a second copy of a declaration drifts and the
compiler cannot check prose.

It is a **client of the `Operators` gRPC service**, exactly as `Parley.Cli` is. It opens no
database: SPEC.md §4 gives the store to one broker process, and a second reader of the file
would be the corruption that section exists to prevent. It reads the project list through
`Parley.Configuration`, which exists precisely so that more than one program can read the
manifest.

**The plan's principal finding is that the feature cannot be built against today's operator
surface.** Two of the specification's requirements have no operation behind them, and the
specification anticipated this: *"If any view here requires something that surface does not
offer, that is a protocol change and belongs in `SPEC.md` before it belongs here."* Both gaps
are documented in [research.md](./research.md) and are gated below.

## Technical Context

**Language/Version**: C# / .NET 10, matching the rest of the repository
**Primary Dependencies**: ASP.NET Core (server-rendered Razor Pages); `Grpc.Net.Client` for the
`Operators` service; `Parley.Configuration` for the project manifest. **No npm, no JavaScript
build step, no client framework.**
**Storage**: None. The console holds no state of its own and opens no database. Every fact it
renders is fetched from a broker over the transport, per request.
**Testing**: xUnit v3 on Microsoft.Testing.Platform, `<OutputType>Exe</OutputType>`, in
`tests/Parley.Web.Tests`, matching every other test project in the repository.
**Target Platform**: Windows, Linux and macOS — the same three CI runs as the rest.
**Project Type**: Web application, server-rendered. One new source project, one new test project.
**Performance Goals**: *Design guidance, not acceptance criteria.* A project's roster renders in
under one second on a deployment of twenty agents; a conversation of two hundred messages in
under two seconds. The binding targets are SC-004 — an unreachable broker reported within five
seconds and never blocking another project's view — and SC-005, navigability at scale.
**Constraints**: Binds loopback only unless explicitly configured otherwise (§16). Never opens
the store. Never binds anything from the `Agents` service. Never renders a resolved credential.
Message text is HTML-encoded on every path, and never written to the console's own logs (FR-031).
A broker that is not V1 is named as such and contained to its own project (FR-026). Pair and
visibility are carried as text, not colour (FR-030). Nothing destructive happens without the
operator being shown what it destroys (FR-033).
**Scale/Scope**: A handful of projects per machine, tens of agents per project, and a message
log that grows for as long as the deployment runs — so conversation listing must be paged from
the first version rather than retrofitted.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Assessment |
|---|---|
| **I. The specification is the product** | **GATE — not yet passed.** Two requirements need operations that do not exist. `SPEC.md` §10.2 must gain them before code is written. See Gate 1 below. |
| **II. No agent has authority over another** | Passes. The console is the operator asymmetry the principle already names — a person, not an agent. It introduces no agent-to-agent authority and sends no message. |
| **III. Identity is fixed by the launch** | Passes, and is load-bearing in User Story 3. The console *explains* registration and never performs one, because an agent's name must come from the launch configuration of the process that will act as it. FR-018 states this to the reader. |
| **IV. Terseness is a security control** | Passes with a caveat. Nothing the console returns enters an agent's context, so the message cap is not the control here. The equivalent discipline is result bounding: the new listing operation is paged, because an unbounded operator query is the same mistake one level up. |
| **V. Invariants live in storage** | Not engaged. Both new operations are reads. No new invariant is introduced, so none needs a constraint. |
| **Agent and operator surfaces are different** | Passes, and is the main structural requirement. Both new operations go on the `Operators` service and on `OperatorConsole`. Nothing is added to `Agents` or to `Broker`, so `SurfaceBoundaryTests` should not move. |
| **`Parley.Core` has no I/O** | Passes. The console references `Parley.Transport` and `Parley.Configuration`; it has no reason to touch `Parley.Core` at all. |
| **A green build is the whole lint gate** | Drives the stack choice. A JavaScript toolchain would be a second build with its own lint story, outside warnings-as-errors and outside `NuGetAudit`. Server-rendered Razor keeps one gate. |
| **The repository is public** | Placeholder agents `orders`, `billing`, `inventory` throughout the artifacts. Verified by grep before commit. |

### Gate 1 — broker V1 is specified before it is built

Three of the specification's requirements have nothing behind them on today's operator surface.
`Export` takes a conversation identifier and nothing produces one, so a conversation cannot be
found. Its reply carries messages and no exchange state, so an abandoned question and an answered
one render identically. And no message on the operator surface carries a protocol version in
either direction, so a client cannot tell a broker of the wrong version from one that is switched
off.

V1 settles all three by declaring what a broker must expose rather than discovering what it
happens to. The interface says which version it is first — that is what makes the rest of the
contract checkable.

Per principle I and the specification's own closing assumption, `SPEC.md` §10.2 must describe
both operations before either is implemented. `/speckit-tasks` must order the spec change first
and the code second, and the pull request that adds the RPCs must carry the §10.2 edit.

Everything else in the specification is satisfiable today — see the coverage table in
[research.md](./research.md).

## Project Structure

### Documentation (this feature)

```text
specs/003-parley-web-console/
├── plan.md              # This file
├── research.md          # Phase 0 output — surface audit, gaps, stack decisions
├── data-model.md        # Phase 1 output — view model derived from the wire types
├── quickstart.md        # Phase 1 output — how to run and verify it
├── contracts/
│   └── operators-additions.md   # The two proposed RPCs, as a §10.2 change
├── checklists/
│   └── requirements.md  # Written by /speckit-specify
└── tasks.md             # Phase 2 output — NOT created by /speckit-plan
```

### Source Code (repository root)

```text
src/Parley.Core/                 # Changed: the two V1 interfaces, in domain types
src/Parley.Web/                  # New. ASP.NET Core, server-rendered.
├── Program.cs                   # Host, loopback binding, project discovery
├── BrokerDirectory.cs           # projects.json -> one Operators client per project
├── Pages/                       # Razor Pages: projects, agents, conversations, register
└── Rendering/                   # Payload shaping and the visibility annotation

tests/Parley.Web.Tests/          # New. xUnit v3, MTP, OutputType Exe.

src/Parley.Transport/            # Changed: two RPCs added to service Operators
src/Parley.Broker/               # Changed: two methods added to OperatorConsole
src/Parley.Server/               # Changed: OperatorsService binds the two new RPCs
SPEC.md                          # Changed first: §10.2 gains both operations
```

**Structure Decision**: One new source project and one new test project, in the existing flat
`src/` and `tests/` layout. `Parley.Web` sits beside `Parley.Cli` as a second operator-side
client of the same service — it does not sit inside `Parley.Server`, because the console is a
client of the broker and putting it in the broker process would give the web host a route to the
store that the architecture spends §4 denying it. `Parley.Core`, `Parley.Store` and `Parley.Mcp`
are untouched.

## Complexity Tracking

> Two additions that must be justified rather than assumed.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| A new source project (`Parley.Web`) | The console is a long-running host with a web dependency; `Parley.Cli` is a short-lived console tool distributed as a dotnet tool. Fusing them would put ASP.NET Core into the CLI's dependency graph for every operator who only ever runs `parley agents`. | Adding pages to `Parley.Cli` was considered and rejected on that dependency cost. Adding them to `Parley.Server` was rejected because it would put a web host in the one process that opens the database. |
| Two new RPCs on `Operators` | FR-012 and FR-015 have no data behind them today. Both are reads of state the store already holds. | Deriving conversation lists client-side would require exporting every conversation to find out which exist, which is an unbounded read of the whole message log to answer a listing question. |

Neither is a protocol *behaviour* change: no message type, no state transition, no new
invariant. Both are operator reads of what the store already records.
