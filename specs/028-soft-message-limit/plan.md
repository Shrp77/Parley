# Implementation Plan: Soft message limit and message length statistics

**Branch**: `028-soft-message-limit` | **Date**: 2026-11-02 | **Spec**: [spec.md](spec.md)

## Summary

Raise the stored limit on a message from 8,000 to 512,000 characters, treat 16,000 as a soft limit that only informs, and record every stored message's length in a table that outlives pruning. Migration 005 rebuilds `messages` and `ownership_questions`, because a SQLite CHECK cannot be altered. A new operator operation, `message_lengths()`, reports the record on the terminal and in the web console.

## Technical Context

**Language/Version**: C# on .NET 10
**Primary Dependencies**: Microsoft.Data.Sqlite, Dapper, Grpc, ModelContextProtocol, Terminal.Gui v2, Razor Pages
**Storage**: SQLite, per project; schema 4 to 5
**Testing**: xUnit v3 on Microsoft.Testing.Platform; `[Unit]` and `[Integration]`
**Target Platform**: Windows, Linux, macOS
**Performance Goals**: Upgrading 100,000 messages under a minute
**Constraints**: Every row kept; the migration is one transaction; the wire changes freely
**Scale/Scope**: One new table, one new operation, three surfaces of text

## Constitution Check

- **Terseness is a security control**: reduced, by the owner's explicit decision, and recorded everywhere the rule is stated. This is the plan's one deliberate departure. See Complexity Tracking.
- **Message payloads are inert data**: unchanged. A length record holds no text.
- **Agent and operator operations on different types**: `message_lengths()` is on `OperatorConsole` and the `Operators` service only. `SurfaceBoundaryTests` holds it.
- **Every operator operation on the terminal and web UI**: both bind it through the health report. `DriftTests` holds it.
- **No route to SQLite from clients**: the report crosses the wire, like `refusals()`.
- **Schema 1 is frozen; a schema change is a new migration**: 005. Feature 027 moves to 006.

## Project Structure

```text
src/Parley.Core/MessageLimits.cs            Hard and soft limits
src/Parley.Core/MessageLengthReport.cs      New: the report's shape
src/Parley.Store/Migrations/005_*.sql       New: the rebuilds, the table, the trigger, the backfill
src/Parley.Store/MessageLengthRepository.cs New: the report's queries
src/Parley.Broker/OperatorConsole.cs        DescribeMessageLengthsAsync
proto/parley.proto, src/Parley.Server/OperatorsService.cs, src/Parley.Client/BrokerClient.cs,
src/Parley.Core/IParleyBrokerClient.cs      The operation, end to end
src/Parley.Mcp/ParleyTools.cs               The soft-limit note in a reply; wording
src/Parley.Mcp/ParleyInstructions.cs        Wording
src/Parley.Terminal/Health.cs, Shell.cs     The report on the terminal
src/Parley.Web/Pages/Projects/Health.*      The report on the web
SPEC.md, docs/, README.md, CLAUDE.md, .specify/memory/constitution.md   The one new rule
```

## Complexity Tracking

| Departure | Why | Simpler alternative rejected because |
|-----------|-----|--------------------------------------|
| A 512,000-character message weakens the terseness control by a factor of 64 | Owner's decision: people and agents were blocked from saying what they needed to | A flat 16,000 was the owner's first answer, and they replaced it with this |
| Two table rebuilds in one migration | A CHECK cannot be altered, and `ownership_questions` copies an agent's answer whole | Leaving that table at 8,000 would make a long answer fail with storage's own text |
