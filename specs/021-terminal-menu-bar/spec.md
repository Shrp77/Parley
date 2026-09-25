# Feature Specification: a menu bar in the terminal

**Status**: captured, not started. **Starts after 019's review gate is met.**
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

## Open questions

1. **Availability.** Conditional actions are absent today — `Withdraw` or `Reinstate`, never both;
   Join or Leave. A menu bar conventionally shows them disabled. Greyed is friendlier and needs each
   action to carry an availability predicate *and* a reason, or it is a dead item with no
   explanation. Not decided.
2. **Accelerators.** `r` is remove-project and withdraw-agent; `l` is files and leave-seat.
   Unambiguous scoped to focus, which D2 preserves — but a menu conventionally shows the key beside
   the label, and two menus showing `r` for different things is worth a look before it ships.
3. **Mouse.** A menu bar invites clicking. This surface has a recorded history of mouse reporting
   being left switched on; `ConsoleRestore` handles the process dying, but nothing has deliberately
   *used* the mouse here yet.

## Not in scope

No protocol change, no schema change, no wire change. This is one surface.
