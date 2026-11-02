# Implementation Plan: Close a request without answering

**Branch**: `029-close-a-request` | **Date**: 2026-11-02 | **Spec**: [spec.md](spec.md)

## Summary

A new operator operation, `close_request(exchange, reason?)`, ends one open exchange the person is a party to with the broker's existing `closed` Result. It reuses what a person's close of a conversation writes, so no schema change is needed.

## Technical Context

C# on .NET 10, SQLite through Dapper, gRPC, Terminal.Gui v2, Razor Pages. No migration.

## Constitution Check

- **Agent and operator operations on different types**: on `OperatorConsole` and `Operators` only; `SurfaceBoundaryTests` holds it.
- **The broker writes no message on anyone's behalf, with one exception**: the exception is widened from a conversation to one Request, and SPEC §5.0 says so. No words go in the person's name.
- **No agent has authority over another**: only the person may do this, and only on requests they are a party to.
- **Nothing times out**: nothing here is automatic.
- **Every operator operation on the terminal and web UI**: `Alt+j` in a transcript, and a form on the Inbox.

## Why only the person's own

Closing a request between two agents would decide for the receiver what its work came to. It would also strand an ownership question, which `force_conclude` handles and this does not need to. The person can still close the whole conversation.
