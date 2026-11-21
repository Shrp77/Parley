# Data model: The Inbox follows an exchange after it is answered

Nothing here is stored by the broker or a surface, except the one setting in R8. Every row is derived, each time it is
shown, from what the broker already keeps. All types below live in Parley.Client unless named otherwise.

## InboxView (enum)

`WaitingOnYou`, `InFlight`, `Closed`. Which one an exchange is in follows research R1 and is exclusive.

## InboxRow

One exchange the person is part of.

| Field | Meaning |
|---|---|
| `Project` | The project the exchange is in. |
| `Exchange` | Its number. Unique within the project. |
| `Conversation` | The conversation it is in now (after any move). |
| `Agent` | The other side: who waits on the person, whom the person waits on, or who was on it with them. |
| `View` | Which `InboxView`. |
| `Opened` | When it opened. |
| `Last` | The last message on it (`Message`), or null when retention removed its words. |
| `Standing` | In flight only: an `InFlightStanding`. |
| `Ended` | Closed only: an `ExchangeEnding`. |
| `FollowUp` | Whether a follow-up may be offered: false for the facilitator. |

Rule: an exchange appears at most once across all views.

## InFlightStanding

What a chip says, from the registry row and the flags report (research R2).

| Field | Meaning |
|---|---|
| `State` | `Working`, `Idle`, `Running`, `NotRunning`, `CannotStart`, `OpenOutside`, `Unknown`. |
| `Since` | When the running turn started, for `Working`. |
| `Flag` | Null, or `Stalled`, `NotCollected`, `PermissionRefused`, `Retired`. |
| `Words` | The chip's words and the sentence beside it, written once here for both surfaces. |

## ExchangeEnding

How a closed exchange ended.

| Field | Meaning |
|---|---|
| `Closed` | When it closed. |
| `How` | `Done`, `Answered`, `Received`, `Blocked`, `Settled`, `ClosedByPerson`, `Closed`. |
| `Blocker` | For `Blocked`: missing information, conflict or permission. |
| `Reason` | For `ClosedByPerson`: the reason given, if any. |
| `SettledBy` | For `Settled`: the agent and the message that settled it. |

## InboxReading

What one read of a project gives a surface.

| Field | Meaning |
|---|---|
| `Rows` | Every `InboxRow`, all views. |
| `Counts` | Rows per view. |
| `Window` | The closed window used, in days, and which page of it ("show older"). |
| `Problem` | Set when the project could not be read; its rows are then absent, and the surface says so. |

## ExchangeReference

A reference found in a message's text (research R5).

| Field | Meaning |
|---|---|
| `Exchange` | The number. |
| `Start`, `Length` | Where `#N` is in the text, so a surface can mark exactly that. |

## ExchangeDetail

What the reference panel and the terminal's Exchange dialog draw.

| Field | Meaning |
|---|---|
| `Project`, `Exchange`, `Conversation` | Where it is. Conversation is null when there is no such exchange. |
| `Messages` | Every message on it that the store still holds, oldest first. |
| `Snapshot` | Its state, when it opened and closed, and its outcome. |
| `WaitingOn` | For an open exchange: who it waits on. |

## ParleySettings (Parley.Configuration)

The one stored thing: `<root>/settings.json`.

| Key | Type | Default | Rule |
|---|---|---|---|
| `inbox.closedDays` | whole number | 7 | 1 to 365. Missing, the wrong type or out of range reads as 7. |

Written atomically by rename. Unknown keys, a byte-order mark and line endings are kept as they were.
