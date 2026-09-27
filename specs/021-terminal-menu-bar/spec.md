# Feature Specification: a menu bar in the terminal

**Status**: **built** on 2026-09-27 — D1, D2 and D3, with the three open questions settled below.
The review gate is one gate over 019, 020 and 021 together, run on the owner's all-clear.
**Created**: 2026-09-25
**Owner's decisions**: taken 2026-09-25, recorded below with the reasoning.

## Why this exists

The terminal offers every action behind `?`, which opens a flat list of what the focused region
can do. That is discoverable only to somebody who already knows to press `?`. The owner asked
for "a File, Edit, Windows etc. menu system" — an ordinary menu bar, the thing every other
full-screen program has.

**This was raised once before and lost.** It was deferred behind 019 and never written down, and
the owner had to ask again. That is the whole reason this file exists ahead of any work on it.

## What the owner decided

### D1. The action carries metadata; it does not carry the UI

The owner's words: *"The action can be a self-sustained entity with metadata that guides the UI,
but does NOT dictate the UI."*

This corrects an earlier framing of mine — "the menu is generated from `RegionAction`" — which
reads as the action knowing what a menu is. It does not. An action states facts about itself and
each surface decides what to do with them. The `?` dialog, the menu bar and the status line become
three readings of one list rather than three lists.

Each action gains:

| Field | What it says |
|---|---|
| `Category` | What it is about — Deployment, Project, Agent, Conversation, View, Help |
| `Group` | An entity of its own, with a `Name` and an `Order`, shared by the actions in it |
| `Order` | Where it sits inside its group |

`Group` is a record rather than a string precisely so that group ordering is a property of the
group. A bare string would have every action in a group repeating that group's position, and two
actions in one group would disagree about it the first time one was edited.

**Rendering rule**: sort by `(Group.Order, Order)`; emit a separator wherever `Group` changes.

Each surface reads what it needs and ignores the rest. The `?` dialog may ignore `Category`
entirely. The status line may take the lowest orders and drop the rest, which is what `Shell.Fits`
already does by hand.

### D2. The bar is scoped to the focused region

Items outside the focused region are **not** offered. The categories are on the bar; what is under
them is the focused region's.

This preserves User Story 2's rule from 005 — *an action belongs to the region that shows the thing
acted on* — and it avoids a defect CLAUDE.md records as having **appeared four times**: reading one
region's selection from another view. `SeatSelected` read `Projects.Selected`, which is null in a
project view. A global menu offering "Withdraw…" while the projects pane has focus walks straight
back into that, because the action has no selection to act on.

The rejected alternative worth keeping: a global bar where choosing an item **focuses that region
first** and then acts. It avoids the cross-region read by never doing it, and it is the design to
revisit if scoping proves too thin in use. It was not taken because it makes every action's target
the action's business rather than the region's, which is the larger change.

### D3. The bar is always visible, on the top row

Conventional and discoverable: the menus can be seen to exist without knowing to press anything.

**It costs a row permanently**, and this surface budgets rows tightly — status line, message line,
the transcript floor, a composer that grows to ten rows. On a 24-row terminal the bar competes with
the message line, which CLAUDE.md records being added *because* every message the surface produced
was being computed and shown to nobody. The arrangement in `Shell.Lay` assigns heights from one
place; the bar joins that, rather than being subtracted somewhere new.

The rejected alternative: `PopoverMenu`, summoned with Alt, costing no row. Available in
Terminal.Gui 2.4.17 and rejected on discoverability.

## What the menus hold

Mapped from the actions that exist today. Categories, not File/Edit/Window: this is not a document
editor, there is no file to open, nothing to cut, and no windows to arrange. Those titles would each
be empty or padded with things that do not belong under them.

| Menu | Holds today |
|---|---|
| Deployment | Refresh · Open web UI (F8) · Health · Follow log · Quit |
| Project | New… · Remove… · Prune… · Enrol repositories… |
| Agent | Add… · Start every unregistered · Withdraw… · Reinstate… · Take seat · Leave · Instruct… |
| Conversation | Start… · Archive · Close… · Destroy… · Files… · Sort · Show archived |
| View | Back · Fold transcript · Copy transcript |
| Help | Keys · About (build and protocol) |

## What must stay true

- **`EveryKeyIsOfferedTests`**: no action exists only as a keystroke. The bar must be another
  reader of the same list, never a second hand-written list — two lists agree until one is edited,
  which is this repository's most-repeated defect.
- **`SurfaceTests`**: `Retire`, `Reinstate`, `Purge`, `ForceConclude`, `Prune` and `Instruct` each
  appear in exactly one file, which must carry the door. A menu entry naming one of those words in
  a second file fails the build, and that is the point.
- **`DeadSurfaceTests`**: a constant named only in its own file is reported. New labels must be
  reached through the action table or be private.
- The narrow-terminal rule: the status line's ladder must still name the way back and `?` at every
  width.

## Open questions, settled while building

Each was decided from precedent in this repository rather than from taste, and each is recorded so it
can be overruled on evidence.

1. **Availability — the action is shown, with the reason, and never hidden.** A menu bar conventionally
   greys what cannot be taken; this surface has something stronger to go on. **0.28.2 records the owner
   unable to find how to start a conversation because the action was hidden until a seat was held, and
   reaching for Instruct instead** — which is not a message and never becomes one. An action nobody can
   find is indistinguishable from one that does not exist. So 020's ask is offered with no seat and says
   what is missing when it is pressed, and that is the pattern: offer it, and let the refusal explain.
   No availability predicate was added, because nothing yet needs one that a worded refusal does not do
   better.

2. **Accelerators — the key is shown beside the label, and it is unambiguous because of D2.** `r` is
   remove-project and withdraw-agent; `l` is files and leave-seat. Scoping the bar to the focused region
   means the two are never on screen together, so the ambiguity the question feared cannot be rendered. A
   menu that hid the key would teach somebody to reach for the mouse for something one keystroke does.

3. **Mouse — left to the toolkit, deliberately and with the record checked.** The menus are
   `Prompting.Choose` dialogs, which the toolkit already makes clickable, so nothing here enables mouse
   reporting that was not enabled before. The recorded hazard is reporting left switched **on** after the
   process dies, and `ConsoleRestore` handles that by reading the modes out of Terminal.Gui's own
   assembly rather than keeping a list — so it covers whatever a dialog turns on without being told.

## What was built, and what it cost

**The bar is reached with Alt and a category's first letter**, not a bare letter. A focused view is
offered a key before the surface's own handler is — 0.30.4's correction — so opening the Conversation
menu had to work while somebody is typing into the composer, where `c` is a `c`.

**A menu is a `Prompting.Choose` dialog rather than the toolkit's `MenuBarv2`.** It inherits what was
paid for once: a description that sizes itself (0.18.10), a list that scrolls (0.25.1), labels that
survive an underscore (0.25.2), and a session a test can drive. A second dialog written for menus would
have inherited none of it.

**`Category` is nullable rather than defaulted**, so an unclassified action is a build failure rather
than a menu quietly growing an `Other` heading nobody meant. The region supplies the default and the
handful the table files elsewhere say so themselves — Health, the log and the web UI belong to the
deployment however they are reached.

**`DeadSurfaceTests` found that `ActionCategory.Help` existed and nothing used it**, which was a real
gap and not a tidy-up: the category had been built and the actions the table puts under it had not.
*Keys* and *About* exist now, and About names this tool's build and protocol rather than the broker's,
because they install separately.

**The bar's row broke a promise, and an existing test caught it.** `Show()` computed the message line's
room as `height - 1 - LeastRegionRows`, counting the status line and not the new row on top — so on a
short terminal the message grew into the six rows the regions are guaranteed. The floor is what is
protected; whichever chrome is added comes out of the message's room. Fixed at the arithmetic, not by
loosening the test.

## Not in scope

No protocol change, no schema change, no wire change. This is one surface.
