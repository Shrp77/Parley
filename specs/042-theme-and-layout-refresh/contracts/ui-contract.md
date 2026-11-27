# UI contract: what every direction defines, and what each size class shows

The console exposes no new interface to programs. This contract is between the design artefacts and the console:
what a direction's tokens file must define so it can be laid over the console, and what a person sees at each size.

## 1. Tokens a direction defines

A direction file sets these custom properties under `:root` (light) and under both
`@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }` and `:root[data-theme="dark"]` (dark), the
same selectors the console uses today, so the person's choice on the Settings page keeps working.

**Colour roles** (`--md-sys-color-*`): primary, on-primary, primary-container, on-primary-container; the same four for
secondary, tertiary and error; surface, on-surface, surface-variant, on-surface-variant, surface-dim, surface-bright,
surface-container-lowest, -low, (plain), -high, -highest; outline, outline-variant; inverse-surface, inverse-on-surface,
inverse-primary; scrim, shadow.

**Extension roles** (`--md-ext-color-*`): present, working, waiting (on the person), blocked, frozen, outside (open
outside Parley), and each one's `on-` pair where text sits on it.

**Type** (`--md-ref-typeface`, `--md-sys-typescale-*`): the typeface, and size, line height, weight and tracking for
display, headline, title, body and label at large, medium and small; `--md-ext-type-grade-dark`.

**Shape** (`--md-sys-shape-corner-*`): extra-small, small, medium, large, extra-large, full.

**Elevation** (`--md-sys-elevation-level0` … `level3`).

**Motion** (`--md-sys-motion-easing-*`, `--md-sys-motion-duration-*`): standard and emphasized easing; short and medium
durations.

A direction may define no selector other than these token blocks. Layout and components are shared by every direction.

## 2. What each size class shows

| | Compact (< 600) | Medium (600–839) | Expanded (840–1199) | Large (≥ 1200) |
|---|---|---|---|---|
| Top bar | Project name and the person's seat; overflow menu | As compact, plus the version | Full | Full |
| Navigation | Bottom bar: Projects, Agents, Inbox, Conversations, More | Rail | Rail | Drawer, folds to rail |
| Lists (Projects, Agents, Conversations, Files, Health, Roadmaps) | Cards with every column's information | Cards or two-line rows | Tables | Tables |
| Inbox | Cards; a row opens in place | Cards | Table rows | Table rows |
| Conversation | Transcript full width; panel and timeline as sheets; composer full width, pinned | Transcript full width; panel as a sheet | Panel + transcript | Panel + transcript + timeline |
| Message text | Full width | Up to ~75 characters | Up to ~75 characters | Up to ~75 characters |
| Graph panel | Full-screen view | Full-screen view | Full screen unless the transcript keeps its measure | Pinned beside the page |
| Dialogs | Full-screen | Centred | Centred | Centred |
| Touch targets | ≥ 48 × 48, 8px apart | ≥ 48 × 48 | ≥ 48 × 48 | ≥ 48 × 48 (pointer may be smaller visually, hit area stays) |

## 3. What does not change

Every page's address, every form's fields and handlers, every key the console takes, the one message view and the one
composer, the frame rules (`ConsoleHost.Embedded`) and the no-script behaviour of each page.
