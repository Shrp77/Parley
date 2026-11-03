# Implementation Plan: Who is working

**Branch**: `030-who-is-working` | **Date**: 2026-11-02 | **Spec**: [spec.md](spec.md)

## Summary

`Registry` already carries each agent's turn standing to clients, and the lists ignore it. `Activities.Now` derives what a list says, the terminal's `Standing` gains headings from the same turn facts, and the web's rows use it and sort by it. No wire or schema change, so a patch version.

## Constitution Check

- **An agent whose turns nobody reports is unknown**: `Now` and `FromTurns` return presence alone, and tests hold it.
- **Agents replies never carry turn information**: unchanged. This is only the operator registry.
- **Anything drawing from a read checks it is still in view**: `AgentsOnceAsync` does.
- **Hold screens to what is drawn**: the terminal test reads the laid-out rows.

## Terminal refresh

`AgentsOnceAsync` runs on the mail watch's interval, because a seat is held whenever a project is open. It draws only when what the list says has changed, and never from a failed read.
