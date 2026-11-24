# Research: Theme and layout refresh for the web console

Each decision says what was chosen, why, and what else was weighed.

## R1. Where the styles live

**Decision**: move the CSS out of `Pages/Shared/_Layout.cshtml` into four stylesheets served from the assembly at
`/brand/` (tokens, frame, components, pages), the way the console already serves its fonts. The layout keeps its markup
and the small inline script that sets the theme and density before first paint.

**Rationale**: all 3,435 lines of the layout's CSS are inline today, so every page carries the whole stylesheet, and a
direction or a pass means editing one very long block. Separate files let a direction be one tokens file, let each
pass touch one concern, and let the browser cache the styles across pages. Serving them from the assembly keeps the
console self-contained: no network, no extra files to install.

**Alternatives considered**:
- *Keep everything inline*: no move, but directions become copies of the whole layout and reviews become diffs of the
  wrong thing.
- *A CSS build step (Sass, PostCSS)*: brings a Node toolchain into a .NET repository for little gain; custom properties
  and nesting cover what is needed.
- *Razor partials holding style blocks*: still inline on every page, so no caching, and the scans that read the layout
  would have to follow partials.

**Risk and its check**: the move must change nothing. Pass 0 takes the full screenshot round before and after, and the
two must match pixel for pixel; the existing tests that read the layout's CSS (`MaterialTokensTests` and others) are
widened to read the stylesheets.

## R2. Comparing directions without shipping them

**Decision**: a direction is a tokens file (`directions/a.css`, `b.css`, `c.css`) that overrides the console's tokens.
`tools/Parley.Screens` gains a shot-list option, `stylesheets`, naming CSS files that are laid over every page in the
round before capture. One round per direction, same scenario, same shots.

**Rationale**: the person compares real screens with real sample work, at every size, in both themes, and nothing
unchosen ever enters the product. The tool already controls the browser, so adding a stylesheet is a few lines there.

**Alternatives considered**:
- *A hidden switch in the console (`?direction=b`)*: puts looks nobody chose into the shipped product, and a switch is a
  behaviour to test and secure.
- *A branch per direction*: three copies to keep current while the layout pass is going on.
- *Static mock-ups in a design tool*: they would not show the real pages, real data or real breakpoints.

## R3. Sizes the layout adapts to

**Decision**: Material 3's window size classes: compact under 600 CSS pixels, medium 600–839, expanded 840–1199, large
1200 and up. Navigation is a bottom navigation bar on compact, a navigation rail on medium and expanded, and the
navigation drawer on large (folding to the rail as today). Lists that are tables today become cards on compact. The
conversation's side panel and timeline fold away on compact and open as sheets. The graph panel sits beside the page
only on large, and is a full-screen view below that.

**Rationale**: these are the classes Material 3's own layout guidance is written for, so components behave as the
system intends, and they match the devices the person named (phone, tablet portrait, tablet landscape, desktop). The
console's eleven media queries today use six different widths (26, 40, 60, 60.01, 75 and 90 rem); one set of classes
replaces them.

**Alternatives considered**:
- *Keep today's widths and patch the phone*: leaves six breakpoints that disagree, which is how the phone broke.
- *Container queries for everything*: right for a few components (cards, the composer) and used there, but the frame's
  navigation follows the window, which media queries describe directly.

## R4. Holding contrast with a test

**Decision**: a new `ContrastTests` reads `tokens.css`, resolves every colour role in light and dark (including roles
defined as other roles), and computes the WCAG 2.2 contrast ratio of each pair the console uses: every `on-*` role on
its role, `on-surface` and `on-surface-variant` on each surface level, outline on each surface, and the extension
roles (present, working, blocked). Text pairs need 4.5:1; large text, icons and outlines 3:1. The same test runs
against each direction file before it is shown.

**Rationale**: feature 035 checked 108 pairs once, by hand. Nothing holds them, and this feature changes every colour.
The pair list comes from the token names, so a new role is checked as soon as it exists.

**Alternatives considered**:
- *axe or Lighthouse in a browser*: catches real rendered pairs, including images, but needs a browser in the unit run;
  kept as a check in the screenshot rounds instead.
- *APCA*: a better model of perceived contrast, but WCAG 2.2 AA is the standard the spec names and most audits use.

## R5. Type

**Decision**: stay with Roboto Flex, vendored, and use its axes: weight for names and titles, optical size following the
font size, and grade (`GRAD`) a little higher in dark mode so light text on dark keeps its weight without reflowing.
Message body moves from body-medium to body-large (16/24), with a measure of about 75 characters; metadata uses
label-small or body-small in `on-surface-variant`.

**Rationale**: Roboto Flex is already shipped and is a variable font, so the hierarchy the person asked for costs no new
download. Grade changes thickness without changing width, which is exactly what dark mode needs.

**Alternatives considered**: a second typeface for headings. Possible in a direction, if the person's images point that
way; it costs one more font file and is decided in pass 1.

## R6. Depth

**Decision**: depth from Material 3's tone-based surface levels (surface, container-low, container, container-high,
container-highest), with elevation shadows only on what floats (dialogs, menus, the pinned composer, the graph panel
when it overlays). Borders stay only where a boundary must be seen without colour (fields, focus).

**Rationale**: this is Material 3's own model of depth, and it works in dark mode, where shadows barely show. The
tokens already define all five levels; most pages use only one or two today.

## R7. Motion and state

**Decision**: Material 3's motion tokens (standard and emphasized easing, short and medium durations) for panels, sheets,
the composer and menus; state layers at 8% hover, 10% focus and 10% press; a visible focus ring on every control. All
motion stops under `prefers-reduced-motion: reduce`, as `MaterialTokensTests` already requires.

**Rationale**: these exist in part since feature 035 (F5, F6); the pass applies them everywhere and adds the motion the
new layout needs (sheets, the navigation drawer).

## R8. Touch targets

**Decision**: every interactive control is at least 48 by 48 CSS pixels to touch on every size class, through its own
size or a padded hit area, with 8 pixels between neighbouring targets on compact. A test scans the stylesheets for the
interactive selectors and their minimum size.

**Rationale**: 48 pixels is Material 3's minimum touch target and comfortably over WCAG 2.2's 24-pixel minimum (2.5.8).
Small icon buttons on the conversation page are the main offenders today.
