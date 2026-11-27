# Data model: Theme and layout refresh

This feature stores nothing new. The "entities" are the design artefacts the work is organised around, and where each
lives.

## Direction

One candidate look, compared before anything is built.

| Field | What it is |
|---|---|
| Letter | `a`, `b` or `c` |
| Name | A short, plain description, for example "Calm slate" |
| Tokens | `directions/<letter>.css`: overrides of the console's tokens (contract: [ui-contract.md](contracts/ui-contract.md)) |
| Draws on | Which inspiration images it uses, and what it takes from each |
| Round | The screenshots of every page in the shot list at 4 sizes × 2 themes, taken with the tokens laid over the console |

**Rules**: every direction defines every token in the contract for light and dark (no fallback to today's values); passes
the contrast test before it is shown; changes look only, never layout or behaviour (the layout pass is shared by all).

**States**: *drafted* → *shown* (its round sent to the person) → *chosen*, *mixed* (parts taken into another) or
*set aside*. The person's decision is written to `decision.md`.

## Token set

The values a theme is made of, in `src/Parley.Web/Brand/tokens.css` once a direction is chosen.

| Group | Contents |
|---|---|
| Colour roles | Material 3's roles (primary, secondary, tertiary, error, each with on-, container and on-container; surface and its five container levels; outline and outline-variant; inverse roles), light and dark |
| Extension roles | Meaning colours: present, working, waiting on the person, blocked, frozen, open outside Parley |
| Type | Typeface, the type scale (display to label), roles for names, body, metadata and code, the dark-mode grade |
| Shape | The shape scale (extra-small to full) |
| Elevation | Levels 0–3 for what floats |
| Motion | Easing curves and durations |
| Density | Normal and compact spacing |
| Size classes | The four width bands (as custom media or the documented widths) |

**Rules**: every rule outside `tokens.css` uses tokens, never a literal colour (held today by `MaterialTokensTests`,
widened to the new stylesheets); every text and outline pair meets WCAG 2.2 AA in both themes (`ContrastTests`).

## Size class

| Class | Width (CSS px) | Device | Navigation | Lists | Conversation | Graph panel |
|---|---|---|---|---|---|---|
| Compact | < 600 | Phone | Bottom navigation bar, project pages in a drawer | Cards | Full width; side panel and timeline as sheets | Full screen |
| Medium | 600–839 | Tablet portrait | Navigation rail | Cards or two-line rows | Full width; timeline folds | Full screen |
| Expanded | 840–1199 | Tablet landscape | Navigation rail | Tables | Panel and transcript | Full screen, or beside if the transcript keeps its measure |
| Large | ≥ 1200 | Desktop | Navigation drawer, folding to the rail | Tables | Panel, transcript and timeline | Beside the page |

## Screenshot round

| Field | What it is |
|---|---|
| Name | `042-<pass or direction>` |
| Scenario | The sample deployment (reused from round 0.64.48, extended where a page needs data) |
| Shots | The busiest pages: conversation, inbox (three views), agents, agent page, graph, roadmaps, files, settings |
| Sizes | 390×844 (phone), 820×1180 (tablet portrait), 1180×820 (tablet landscape), 1440×1000 (desktop); plus 320×640 for the smallest phone check |
| Themes | Light and dark |
| Stylesheets | For a direction round, the direction's tokens file |

## Inspiration image

| Field | What it is |
|---|---|
| File | Placed by the person in `artifacts/inspiration/` on this machine (git-ignored, never committed), or fetched there from the Parley conversation `UI-refresh-and-fixes` |
| Note | What the person likes about it, in their words: a text file beside the images |
