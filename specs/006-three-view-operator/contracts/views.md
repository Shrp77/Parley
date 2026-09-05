# Contract: the three views

What each view shows, what it can do, and how a person gets between them. Binding on both surfaces:
the terminal draws them with Terminal.Gui and the console as Razor pages, but **what is on them is
derived once, in `Parley.Client`**, so the two cannot disagree about what a view is.

## The shape

```text
Projects ──choose──> Project ──choose──> Conversation
   <──────back───────   <──────back───────
```

Three views, one thing each, a defined way back, and a defined subject. Nothing else. This replaces
005's four regions with Tab between them, which the owner used and found *"confusing and too busy"*.

**Back returns to where the person was, not to the top.** Coming back from a project lands on that
project, still highlighted; coming back from a conversation lands on that conversation, still
highlighted, in a list still sorted the way it was left.

## View 1 — Projects

| Shows | From |
|---|---|
| Every project this machine names | the manifest |
| For the highlighted project only: its agents and its conversations, as a summary | `Registry`, `Conversations` |
| Whether the highlighted project's broker is answering | the read that failed, said as such |

| Can do | Door |
|---|---|
| Create a project | as 004 |
| Name the running deployment | as 004, offered only while one is unnamed |
| Remove a project | as 004: forgetting offered beside destroying, the project's name typed back |
| Follow the broker log | as 005 |
| Open in the console (F8) | the console's projects page |

**The summary follows the highlight without navigation.** That is the whole of User Story 1: the
question *"which project do I want?"* is answered before anything is opened.

**An unreachable broker says so in the summary** rather than showing a project with no agents and no
conversations, which is 005's containment rule and is the difference between "empty" and "unknown".

## View 2 — Project

Two halves, agents and conversations, both about the one project this view is on.

### Agents

| Column | Meaning |
|---|---|
| Name | the role |
| Standing | one phrase pairing: has it ever run; may anything here start it; is it attached now |
| Startup | whether it may be started with nobody watching |

The standing phrase replaces *catalogued, not yet registered* and *not in this catalogue*, which the
owner did not understand. The two facts to convey are in `data-model.md`; the words are chosen with
the owner, because the reason they are changing is that the last set was not.

| Can do | Door |
|---|---|
| Add an agent | described — the two files, and that a process will be started — then written, then started |
| Retire / Reinstate / Purge | as 005, unchanged, name typed back |
| Change startup behaviour | described; takes effect on the running node without a restart |
| Start now | offered on an agent that is not running and whose role the catalogue names |

**Adding leaves a startable agent.** Not *catalogued, awaiting a human*. `plan.md` Q1.

**A seat is never offered for retire or purge**, whoever holds it. Unchanged from 005.

### Conversations

| Column | Meaning |
|---|---|
| Name | the identifier, which is the name |
| Last activity | the timestamp of the most recent message, in the reader's local time |

Sortable by either, and **the highlight stays on the same conversation across a sort** — a sort that
moved the selection would be a sort that loses the person's place.

Beside the list, the highlighted conversation: its participants, its message count, whether anything
in it is still open, and whether it is archived.

| Can do | Door |
|---|---|
| Start a new conversation | none |
| Archive / Unarchive | none — reversible with one keystroke |
| Destroy | described, participants and count named, the identifier typed back; an open exchange's asker named and accepted first |
| Show archived | a toggle, view state, remembered for the session |
| Open in the console (F8) | the console's page for this project |

**Archived conversations are absent unless the toggle is on, and the count is always shown.** A
conversation that has had activity since it was archived is marked when shown. That is how an
archived conversation cannot silently hide traffic — `research.md` R4, which decided archiving does
not un-archive itself.

## View 3 — Conversation

The transcript, and what 005 shipped is what this exists to replace: its owner's word for it was
*"one long ass blob"*.

| Property | Requirement |
|---|---|
| Sender colour | per agent, from `Palette`, the same colour everywhere |
| Header | timestamp, sender to recipient, type, state — visually apart from the body **by a means that survives colour being stripped** |
| Time | the reader's local time; the stored value stays UTC and the export stays UTC |
| Body | the author's own line breaks and paragraphs, preserved |
| JSON payloads | laid out over lines, indented |
| Visibility annotation | unchanged from 003: where a participant was not party to a message, it still says so |

| Can do | Door |
|---|---|
| Take part — answer, ask, tell, take delivery | as 005, and only with a seat |
| Move through a long conversation without losing the current message | none |
| Open in the console (F8) | the console's page for this conversation |

**Colour carries no fact.** Every message names its sender and recipient in words and the visibility
annotation names who was not party to it, so a reader who sees no colour at all loses nothing but
convenience. That is `Palette`'s own rule and FR-018 is what keeps it true when the layout changes
around it.

**Nothing an agent wrote may alter the screen an operator is deciding on.** 005's `Safe` still
neutralises control characters, and the new rendering — which introduces real line breaks where there
were none — must not become a route around it. A payload cannot open a paragraph the surface treats
as its own.

## Across all three

- **One keystroke, `F8`, opens the console on the thing being looked at.** Not its front page. Built
  from one place so a route the console renames cannot leave the terminal deep-linking to a 404.
- **Every key is listed where the person can find it**, and no action exists only as a key: whatever
  a key does, a pointer reaches too.
- **Nothing truncates.** Where something does not fit the width, the surface says so. 005's rule,
  and the reason its tab strip was rejected.
- **Nothing polls.** A view refreshes when the person acts or asks; a timer moving a selection under
  somebody's hand is what 005's reads are shaped to avoid.
