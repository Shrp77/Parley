# Research: Material 3 across the web console

## R1. Which colour scheme

**Decision**: Material 3's *content* scheme (2021 colour specification), generated from the seed #2b5cd9 by Google's
Material Color Utilities (the `materialyoucolor` port, version 3.0.4, `SchemeContent`, contrast 0). The light and dark
role values are written into the stylesheet as `--md-sys-color-*` custom properties.

**Rationale**: The owner chose to keep today's blue. The default *tonal spot* scheme dulls the seed to #4c5c92, a grey-blue
that no longer reads as Parley. The content scheme is one of Material 3's standard variants and keeps the seed's chroma: its
primary container is exactly #2b5cd9, its primary #0042bb (light) and #b5c4ff (dark).

**Alternatives considered**: Tonal spot (Material's default; rejected as above). The library's 2025 specification
(rejected: its light-theme inverse surface is near-black and its error container is pink, which match neither the published
M3 guidance nor the Theme Builder). Hand-picked colours (rejected: that is what the console has today).

## R2. Agent slot colours

**Decision**: Keep the eight hues, re-toned in HCT to tone 40 (light) and tone 75 (dark) with chroma at least 36, so each
reads against the new surfaces and the eight stay distinct.

**Rationale**: The slots tell agents apart; they are not scheme roles. Re-toning keeps each agent's colour recognisable
while matching the scheme's contrast.

## R3. Typeface

**Decision**: Roboto Flex from Google's font repository (`ofl/robotoflex`, SIL Open Font License 1.1), cut with fontTools:
every axis pinned at its default except weight (100–900) and optical size (8–144), and characters limited to Latin,
Latin-1, Latin Extended-A, general punctuation, arrows and a few symbols. 94 KB as WOFF2. Served from the assembly at
`/brand/roboto-flex.woff2`, like the icon font; the licence ships beside it.

**Rationale**: The owner's choice; works offline; small.

## R4. Type scale, shape, elevation, state, motion

**Decision**: Material 3's published tokens, as custom properties:

- **Type**: display/headline/title/body/label at large, medium and small, with M3's sizes, line heights, weights and
  tracking (for example body-large 16/24, title-medium 16/24 weight 500, label-large 14/20 weight 500).
- **Shape**: none 0, extra-small 4px, small 8px, medium 12px, large 16px, extra-large 28px, full.
- **Elevation**: levels 0–5 as M3's two-layer shadows, with raised surfaces taking surface-container tones rather than
  tinted shadows.
- **State layers**: hover 8%, focus 10%, pressed 10%, dragged 16% of the content colour, drawn as an overlay.
- **Motion**: standard easing cubic-bezier(0.2, 0, 0, 1), durations short 100–200ms, medium 250–400ms, all off under
  `prefers-reduced-motion`.

**Rationale**: These are the system's published values; using them directly is what "follows Material 3" means.

## R5. How the change is applied

**Decision**: Introduce the M3 tokens, then point the console's existing variables (`--ink`, `--page`, `--card`, `--rule`,
`--accent`, `--alarm` and the rest) at M3 roles, so every page moves together before any control is touched. Then restyle
each control kind once, at the element or shared class, and finally walk each page against the audit.

**Rationale**: The stylesheet already routes almost every colour through a dozen variables. Aliasing them is one change with
whole-console reach, and it keeps the selectors tests pin (the composer's pinning, the frame offsets) intact.

## R6. Verification

**Decision**: Page captures in headless Edge from loopback copies of pages rendered by the web test consoles, light and dark
(`--force-dark-mode` / emulated scheme), at 400, 900 and 1400 pixels wide; contrast checked by computing WCAG ratios for each
text-on-surface pair the stylesheet uses, in both themes; the existing test suites as the regression net.
