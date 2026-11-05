# Audit: the web console against Material 3

Every page of `src/Parley.Web` and every kind of control on it, with what each becomes. This is the map the work follows
(FR-001). Status is updated as phases land.

## A. Controls

| # | Today | Where | Material 3 equivalent | Notes |
|---|---|---|---|---|
| C1 | `button` (filled, accent) | every form | **Filled button** (40px, full shape, label-large, primary / on-primary) | The page's main action only. |
| C2 | `button.quiet`, `.button.quiet`, `.composer-toggle` | most pages | **Outlined button** (outline role) | Secondary actions. |
| C3 | `.button` links styled as buttons | Index, Inbox, Agents, Roadmap grids | **Filled** or **outlined button** by emphasis | Same shapes as C1/C2. |
| C4 | `.file-button` (Export, Spin off, Move entries) | conversation panel | **Tonal button** (secondary-container) | Lower emphasis than Send. |
| C5 | `button.danger` | Destroy, Files, Settings, Act | **Filled button, error role** where it is the act; **outlined error** where it opens a confirmation | Keeps every typed-name and second-press step. |
| C6 | `.strip-button`, `#panel-close`, `.attachment-remove`, `.chip-x`, bell | conversation, settings, frame | **Icon button** (standard / outlined, 40px target) | |
| C7 | `.thread-focus`, `.spin-off`, inline links | transcript meta | **Text button** | |
| C8 | `.new-below`, `.view-create` add | conversation, grids | **Extended FAB** (new message) / **small FAB** (create) | |
| C9 | `input`, `textarea`, `select` | every form | **Outlined text field** (extra-small shape, 56px; dense 40px in toolbars) with label above | Labels stay real `<label>` elements. |
| C10 | `.pick` (agent filter dropdown), `select` picker | conversation, filters | **Outlined select / menu** (surface-container, extra-small shape, level 2) | |
| C11 | `input[type=checkbox]`, radio | trees, filters, forms | **Checkbox** / **radio** (18px, primary, 40px state layer) | Indeterminate kept. |
| C12 | `.toggle` switch | grid filters | **Switch** (52×32 track, handle grows when on) | |
| C13 | `.chip`, `.verdict`, `.tag`, `.waiting-count`, `.msgs`, `.count` | settings, transcript, panel | **Input chip** (removable), **assist / suggestion chip** (read-only facts), **badge** (counts) | `.tag` (closed by a person) keeps the error colour and its words. |
| C14 | `.seat-waiting`, `.strip-count` | rail, frame, strip | **Badge** (small and large) | |
| C15 | `dialog.default-dialog`, new-conversation dialog | Index, Agents, Settings, Enrol | **Basic dialog** (extra-large shape, surface-container-high, headline-small, actions right) | |
| C16 | `.bell-list`, `.export-list`, `.sent-info` popover | frame, conversation | **Menu** (surface-container, extra-small shape, level 2, 48px items) | |
| C17 | `.letter`, `.view-card`, `.act-cards li`, panel boxes (`.find`, `.filter`, `.recipients`, `.waiting-*`, `.convo-head`, `.convo-links`) | many | **Card**: outlined (most), filled (panel tools), elevated (view cards) | Medium shape. |
| C18 | `table`, `.dgrid`, `.grid` | Health, Files, grids, roadmaps | **Data table / list** on surface, header in title-small, rows with state layers | Sticky headers kept. |
| C19 | `.rail` | every page | **Navigation drawer** (expanded) / **navigation rail** (collapsed): active indicator pill in secondary-container | Collapse button kept. |
| C20 | `header` frame | every page | **Small top app bar** (64px, surface, scrolled-under tone) | |
| C21 | `.unreachable`, `.file-problems-banner`, `ul.sent` | many | **Banner-style card** in error-container / surface-container with icon | Not a snackbar: these persist and carry links. |
| C22 | `title=` tooltips | icon buttons | **Plain tooltip** stays the browser's own | A hand-built tooltip needs script and adds nothing. |
| C23 | transcript `.bubble`, `.who`, `.moved-note`, `li.day` | conversation | **Card** (outlined, small/medium shape) with **avatar**; notes and day markers as **assist chips** | Agent slot colour on the leading edge, re-toned (research R2). |
| C24 | `.timeline` minimap | conversation | No direct equivalent | Kept, drawn as a narrow outlined card with ticks in slot colours; focus ring on ticks. |
| C25 | `.attachments` chips | composer | **Input chips with thumbnail** (avatar slot) | |
| C26 | `details/summary` disclosures (`.disclosure`, tree groups, grid rows) | many | **List item with trailing/leading expand icon** | Native `<details>` kept, so no script is needed. |
| C27 | focus | everywhere | **Focus indicator**: 3px secondary outline, 2px offset | Never removed. |
| C28 | Markdown in `.said` | transcript | Body-medium type, code in surface-container-highest, tables outlined | Headings stay near reading size (cannot imitate console labels). |

## B. Foundations

| # | Today | Material 3 |
|---|---|---|
| F1 | 12 hand-picked colour variables, light and dark | Content scheme from #2b5cd9 (research R1), all roles, light and dark; old variables become aliases |
| F2 | System font, 15px body | Roboto Flex; body-large 16/24 for reading, body-medium 14/20 for dense UI; headline-small for page titles |
| F3 | Radii 4–20px chosen per control | Shape scale (4/8/12/16/28/full) |
| F4 | Ad hoc shadows | Elevation levels 0–3 |
| F5 | Brightness filter on hover | State layers (8/10/10%) |
| F6 | 0.12–0.15s transitions | Standard easing and durations; off under reduced motion |

## C. Pages

| Page | Layout pattern | Main controls (by row above) |
|---|---|---|
| Projects (Index) | Toolbar + data list in an elevated card; create as small FAB/disclosure; new-project dialog | C3 C8 C9 C10 C12 C15 C18 |
| All Agents / project Agents tab (Index) | Same as Projects | C3 C9 C10 C12 C18 |
| Inbox | Toolbar + list of message cards with inline reply fields | C1 C2 C9 C10 C17 C18 |
| Notifications | List | C1 C17 |
| Conversation (Detail) | Supporting pane (panel) + transcript + minimap + pinned composer | C1 C2 C4 C6 C7 C8 C9 C10 C11 C13 C16 C17 C23–C28 |
| Spin off | Form card | C1 C9 C11 |
| Move entries | Form card with list | C1 C2 C9 C10 C11 |
| Destroy | Form card, destructive | C5 C9 |
| Project conversations (Agents) | Toolbar + list, new-conversation dialog | C1 C2 C3 C9 C11 C15 C18 |
| Agent | Detail card with table | C17 C18 |
| Act | Destructive confirmation card | C5 C9 |
| Register / Enrol | Form cards, dialog | C1 C2 C9 C11 C15 |
| Naming | Content card | C17 |
| Ownership | Content card | C17 |
| Files | Form card + data table | C1 C2 C5 C9 C10 C11 C18 |
| File problems | Data table with actions | C1 C9 C18 |
| Health | Data tables | C18 |
| Settings | Sections of cards, chips, dialog, tree | C1 C2 C11 C13 C15 C26 |
| Roadmaps (index) | List | C18 |
| Roadmap | Data list with expandable rows, embedded conversation frame | C3 C18 C26 |
| Proposal | Form card | C1 C2 C9 |
| Edit (roadmap) | Form card | C1 C2 C9 |
| Decline | Form card | C1 C2 |
| Frame (every page) | Navigation drawer/rail + small top app bar + banners | C6 C14 C16 C19 C20 C21 |

## D. Decisions taken in the audit

- **Snackbars are not used.** Every message the console shows today either persists until dismissed or carries a link; a
  snackbar that disappears would lose it. They become banner-style cards (C21).
- **Tooltips stay the browser's** (C22). A custom tooltip needs script for every icon button and would not work without it.
- **The minimap has no Material 3 equivalent** (C24) and stays, restyled with the system's tokens.
- **No layout grid change to the conversation page's three regions**: it already matches M3's list-detail / supporting-pane
  pattern; it is restyled, not rebuilt.

## Status

| Phase | State |
|---|---|
| Foundations (F1–F6) | done: fb24598d |
| Controls (C1–C28) | done: f34f0eb6 |
| Pages | done: all 24 rendered pages captured in light and dark at 1400px, eight busiest at 400 and 900px; nothing overflows at 400px; 108 colour pairs pass WCAG AA in both themes |
