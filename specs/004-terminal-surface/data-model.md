# Data model — what the terminal surface holds while it runs

**It stores nothing.** SPEC.md §4 gives the store to one broker process, and FR-016 keeps this a
client. Everything below lives in memory for as long as the application is open, and every fact
in it came from a broker or from this machine's manifest.

That is the whole model, and it is short on purpose: a terminal application that accumulated its
own copy of a deployment would be a second answer to questions the broker already answers, and
the two would part company exactly as a cookie parted company from a seat.

## Held for the life of the application

| Thing | Where it comes from | Lifetime |
|---|---|---|
| **Projects** | this machine's manifest, re-read when its stamp moves (FR-002) | refreshed, never cached past a change |
| **Channel per project** | opened on first use, keyed by endpoint | the application's life; the expensive thing, which is why it is the one thing kept |
| **Seat** | the person, once, explicitly (FR-006) | the held `Attach` stream; released on exit including on interrupt (FR-011) |
| **Delivered messages** | what a drain returned | the seat's life — a drain marks them delivered, so they cannot be asked for again |

## Read per view, never held

Agents, conversations, transcripts and presence are **asked for when a screen is drawn** and not
remembered between draws.

This is not laziness about caching. A broker that was answering when a page was built tells the
reader nothing about whether it is answering now, and a count that was right when it was pushed
tells them nothing about whether it is right now — which is the defect the web console shipped
twice, once for a waiting count and once for a seat. The terminal starts on the other side of
that lesson.

## Derived, not stored

| Derivation | Lives in | Why it is shared |
|---|---|---|
| Transcript ordering and grouping | `Parley.Client/Rendering/Transcript` | SC-003: two surfaces must never disagree about who took part |
| Per-message visibility | `Parley.Client/Rendering/Visibility` | the annotation a reader trusts; forging it is the thing worth preventing |
| Roster shaping | `Parley.Client/Rendering/Roster`, `RosterView` | |
| A stable colour and badge per agent | `Parley.Client/Rendering/Palette` | decoration only — it never carries a fact, in either surface (FR-005) |

## The one thing read from disk

The broker's log (FR-013), opened to follow rather than to own — see `research.md` R4. It is the
only input here that does not arrive over the transport, and it is a convenience view that can be
dropped without touching anything else.
