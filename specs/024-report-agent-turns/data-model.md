# Data model: Report agent turns (024)

**Words**: the spec says *permission refusals*; the stored column and the wire field say `permission_denials`, kept from
the existing table. They are the same count.

## Turn (durable) — the existing `invocations` table

No schema change. Each **ended** turn the broker hears about becomes one row, with the columns the table already has:
`agent`, `waiting_before`, `waiting_after` (both filled by the broker, R2), `started_utc`, `elapsed_ms`, `exit_code`
(0 for a clean finish, non-zero otherwise), `timed_out`, `cost_usd`, `model_turns`, `permission_denials`.

Validation: the agent must exist and be on the reporting node's machine (R10); otherwise the report is ignored.

## Running turn (in memory) — per agent

| Field | Meaning |
|---|---|
| agent | who |
| started_utc | when the node said the turn began |
| machine | the node that said so; its stream ending clears the entry (FR-006) |

State: absent → running (start report) → absent (matching end report, or the node's stream ending). A start newer than an
end that arrives late wins: an end clears only a running entry whose start is not later than its own (spec edge case).

## Turn standing (read model) — on the operator's `AgentRecord`

| Field | Source |
|---|---|
| turns_reported | the agent's node joined with `reports_turns` (R5) |
| turn_running_since | the running entry, if any |
| last_turn_ended_utc | latest `invocations` row: `started_utc + elapsed_ms` |
| last_turn_clean | that row's `exit_code == 0 && !timed_out` |
| last_turn_permission_denials | that row's `permission_denials` |

Never on the agent-facing roster (FR-005).

## Activity (derived, both surfaces) — `Parley.Client.Activities`

| State | When |
|---|---|
| Unknown | turns not reported for this agent |
| Working | a turn is running — shown with how long it has run |
| Waiting on X | not working, and the agent is the waiter on an open exchange in this conversation |
| Stopped | not working, not waiting, owes work in this conversation it collected before its last turn ended — shown with how long ago, and "N permissions refused" when N > 0 |
| Running / Not running | none of the above: presence, as 0.37.5 — including an agent whose machine went away mid-turn, which presence already reports as not connected (spec edge case) |

## Flag — `FlagKind.PermissionRefused` (derived)

Raised for an open exchange whose holder collected it, whose holder's most recent ended turn finished after that
collection with `permission_denials > 0`, and who has said nothing in the conversation since. Carries the agent, the
conversation, and since-when (the turn's end). Disappears when any of those stops being true.
