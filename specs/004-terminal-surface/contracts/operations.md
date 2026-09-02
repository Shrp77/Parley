# Contract: what the terminal surface may call, and on which client

This feature adds no operation. It is a second reader of what `specs/003-parley-web-console`
specified and built, and this file exists to pin *which side of SPEC.md §10.2 each call sits on*
— because the whole point of that section is that an adapter must have nothing to bind by
accident, and this surface binds both halves in one process for the first time outside the
console.

## The two clients stay two clients

`Parley.Client` exposes them separately, as `BrokerDirectory` already does:

- `ClientFor(project)` returns the **Operators** client. Everything a person reads *about* a
  deployment comes from here.
- `AgentsFor(project)` returns the **Agents** client, and is reached **only** through `SeatBook`
  — the one type that knows a person's name and checks it against the registry before using it.

A screen that wants to read the deployment must not reach `AgentsFor`, and a screen that speaks
as the person must not reach `ClientFor`. The separation is the guarantee; the naming is not.

## Operator reads — the person as operator (§10.2)

| Call | Serves | Notes |
|---|---|---|
| `DescribeRegistry` | FR-003 | Agents, presence, tier, owned workspace. |
| `ListConversations` | FR-004 | The list; specified by `003` before it was built. |
| `Export` | FR-004 | The transcript, including per-message visibility. |
| `Implements` | SC-003 | Protocol version, so a surface can say when it and the broker disagree. |

Not called by this feature, and listed so their absence is deliberate: `Retire`, `Reinstate`,
`Purge`, `ForceConclude`, `Instruct`, `Prune`. The web console binds them. **The terminal does
not**, in this scope — a destructive operator action taken by a keystroke in a full-screen
application is a different risk from one taken by a POST behind a page that describes it first,
and it needs its own argument rather than arriving as a side effect of a navigation feature.

## Agent operations — the person as participant (§10.1)

Reached only through a held seat.

| Call | Serves | Notes |
|---|---|---|
| `Attach` | FR-006, FR-011 | The held stream *is* the seat (§4.0). Ending it is leaving. |
| `Waiting` | FR-008 | Counts only, per §13. |
| `DrainInbox` | FR-008 | Debts first, per §7.3. Draining marks delivered — reading is a state change. |
| `GetSubQuestionIds`, `ResumeOwedBy` | FR-008 | What is owed, and on what. |
| `Answer` | FR-008 | One resolution per sub-question (§5.3). |
| `Clarify`, `Notify` | FR-008 | Sending into an open conversation. |
| `Ask`, `AskRole` | FR-009 | Opening one. Rule 8.1's intent and consequences are collected first. |

## Refused before the wire

The composer enforces what the store would otherwise refuse after a round trip, because a
refusal an agent cannot act on is what SPEC.md §7.3 exists to prevent and a person deserves the
same:

- rule 8.5's size cap, stated as a limit rather than discovered as a failure;
- rule 8.1's consequences minimum;
- a sub-question set that is empty.

The broker remains the authority. These are a courtesy in front of it, never a replacement — and
when the broker refuses anyway, **its words are shown**, which is the shape `#71` settled for the
adapter on 2026-09-02.

## Written, not sent

| Action | How | Notes |
|---|---|---|
| Name a project (FR-015) | in-process, the same code `parley project add` runs | The manifest has one writer and this is it, so no second writer appears. |
| Onboard an agent (FR-015) | in-process, the same code the enrolment command runs | |

Neither goes near a broker, which is why they are here rather than in either table above.
