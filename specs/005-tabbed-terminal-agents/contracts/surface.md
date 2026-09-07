# Contract: the surface — regions, keys, and the words each state uses

What a test may hold the screen to. Every phrase below is a string constant on the view that
draws it, so a test asserts against the constant rather than a copy, and a change to the words is
a change to one place.

## Regions

Four, in Tab order, each titled in words:

| Region | Title | Shows | Actions that belong to it |
|---|---|---|---|
| Projects | `Projects` | name, whether the broker answers, description when any | name the running deployment; create; remove (keep / forget / destroy, unchanged from 004) |
| Agents | `Agents` | the union of registry and catalogue (data-model.md) | take a seat; leave it; add an agent; remove one (keep / retire / purge); reinstate |
| Conversations | `Conversations` | the project's conversations, most recent first | open a transcript |
| Transcript | `Transcript` | 004's transcript, per-message visibility in words | what is waiting; answer; ask; notify |

Two more views are reachable and are not regions: the broker's **Log**, and the **Enrolment**
dialog (scan / plan / apply of a manifest), both as 004 has them.

## Keys

| Key | Does |
|---|---|
| Tab / Shift+Tab | next / previous region, in the order above and round again |
| ↑ ↓ | move within the focused region |
| Enter | open, take, or accept the proposal |
| Escape | close the dialog, or go back one level; at the top, nothing |
| the letter shown beside an action in the status line | that action, when its region has focus |
| Ctrl+Q | leave. Ctrl+C is not intercepted, for the reason `TopCommand` records |

Every action is on the status line for the focused region, and every one is also a button or a
menu entry the mouse reaches. No action exists only as a key.

## The status line

Always present, always says which region has focus in words: `Focus: Agents`. Colour may repeat
that; a test that strips colour from the rendered screen must still find the phrase (SC-006).

## Words each state uses

| State | Phrase |
|---|---|
| broker not answering | `not answering` — the project's regions show it within five seconds and stay usable (SC-008) |
| agent presence | `running`, `startable`, `stranded`, `retired` — SPEC.md §14's own words, through `Roster.State` |
| catalogued only | `catalogued, not yet registered` |
| registered, not catalogued | `not in this catalogue` |
| a seat | `seat` |
| seat released for idleness | `Your seat was released: nothing touched it for 20 minutes.` |
| removal, keep | `Kept.` |
| removal, wrong name | `That is not this agent's name. Nothing changed.` |
| removal, retired | `Retired '<name>'.` then the list of what was withdrawn |
| removal, purged | `Purged '<name>'.` then the broker's toll per table, then what was withdrawn |
| reinstated | `Reinstated '<name>'. No file was recreated: add it again for it to be startable.` |
| added | `Added '<name>'.` then the two paths written |
| add refused | the reason, at the prompt, and `Nothing was written.` |
| catalogue created | `Created <path> and recorded it on '<project>'. Its broker restarts to host the node from it.` |

## Layout

At the threshold width and above, side by side: Projects left, Agents over Conversations in the
middle, Transcript right. Below it, the four are the pages of a tab control in the same order,
and Tab moves between pages. The status line is present in both. Nothing is truncated to a
silent ellipsis: a column that does not fit is dropped with its heading, never cut mid-word
(004's narrow-terminal rule, unchanged).

## What a test may assume

- A view is found by its title and a row by its text. Never by coordinates.
- The rendered screen is available as text (`IApplication.ToString()`), and the assertions in
  SC-006 are made against it with every attribute stripped.
- Time is injectable: the seat's idle rule and the log tail's tick run on the application's
  time provider, so a test advances twenty minutes rather than waiting them.
