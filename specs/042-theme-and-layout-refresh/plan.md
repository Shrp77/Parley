# Implementation Plan: Theme and layout refresh for the web console

**Branch**: `042-theme-and-layout-refresh` | **Date**: 2026-11-24 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/042-theme-and-layout-refresh/spec.md`

## Summary

The web console gets a new look on its Material 3 base, chosen by the person from two or three directions shown as
screenshots, and a layout that works from a 320-point phone to a desktop. Nothing it does changes.

The approach, from [research.md](research.md):

1. **Move the console's styles out of the layout into stylesheets the console serves** (R1). Today all 3,435 lines of
   `Pages/Shared/_Layout.cshtml` hold the CSS inline. The tokens go in one file and the components in others, so a
   direction is one tokens file and a pass touches one concern at a time.
2. **Compare directions without building them into the product** (R2). A direction is a tokens file under this
   feature's `directions/` folder. `tools/Parley.Screens` gains a shot-list option that lays a stylesheet over the page
   before capture, so each direction is photographed on the real console at every size, and nothing ships until the
   person picks.
3. **Adapt the layout by Material 3's window size classes** (R3), compact, medium, expanded and large, with a navigation
   bar on phones, the rail on tablets and the drawer on desktops; lists that become cards; and the graph panel that
   takes the screen when it cannot sit beside the conversation at a readable width.
4. **Hold contrast with a test** (R4). Every text-on-surface and outline pair the tokens define is computed in both
   themes and fails the build below WCAG 2.2 AA. Feature 035's 108 pairs were checked once, by hand; nothing holds them.
5. **Build in passes**, each retaking the same screenshot round: tokens (colour and surfaces), type, layout, controls and
   motion.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (Razor Pages); CSS; a little JavaScript in the layout as today
**Primary Dependencies**: ASP.NET Core Razor Pages (Parley.Web); Microsoft.Playwright 1.65.1 (tools/Parley.Screens only)
**Storage**: N/A. The person's theme and density stay in the browser's local storage.
**Testing**: xUnit v3 on Microsoft.Testing.Platform. Parley.Web.Tests (pages over WebApplicationFactory, token and
style scans such as MaterialTokensTests); Parley.Screens.Tests (round files); screenshot rounds by hand.
**Target Platform**: Current Chromium, Safari (iOS and macOS) and Firefox. Phones from 320 CSS pixels wide.
**Project Type**: Web application (server-rendered pages, progressive enhancement)
**Performance Goals**: No page grows its first-load bytes by more than 20% over today. Stylesheets are cached by the
browser, so later pages load less than today, when every page carries the whole stylesheet inline.
**Constraints**: No behaviour change (every existing behaviour test passes unchanged); pages work without script;
WCAG 2.2 AA contrast; touch targets of 48 CSS pixels; reduce motion honoured; no network fetch (fonts and styles served
by the console); framed conversation still works; the terminal untouched.
**Scale/Scope**: 27 pages and fragments (feature 035's audit, section C), 28 control kinds (audit section A), 2 themes, 4
size classes.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle or boundary | How this feature stands |
|---|---|
| I. The specification is the product | No protocol change. SPEC.md needs no edit. |
| II. No agent has authority over another | Untouched: presentation only. |
| III. Identity is fixed by the launch | Untouched. |
| IV. Terseness is a security control | Untouched: no message, payload or limit changes. |
| V. Invariants live in storage | Untouched: no schema change, no migration. |
| Agent and operator surfaces apart | Untouched: no operation is added, removed or moved. |
| No network bind; tunnel only | Untouched. The new stylesheets are served by the console itself, under the same host and frame rules (`ConsoleHost`). |
| One message view and one composer | Kept: `_MessageBody` and `_Composer` stay the only ones; `OneMessageViewAndOneComposerTests` keeps holding it. |
| Every action has a door; form binding; drift between surfaces | Untouched: no form, handler or route changes. The existing tests hold it. |
| A green build is the whole lint gate | New tests (contrast, stylesheet scans) join the unit run. |
| Every claim the documents make is checked | `docs/operating.md`'s description of the console is updated where the layout changes what a person sees (the phone navigation). |
| Review is three consecutive clean runs per lens | Applies to the merge as usual. |
| Versioning | A visual change to a shipped tool moves the patch: `VersionPrefix` 0.64.48 → 0.64.49 in the first pass that lands. |

**Result**: passes. Nothing here touches a principle or a boundary.

## Project Structure

### Documentation (this feature)

```text
specs/042-theme-and-layout-refresh/
├── spec.md
├── plan.md              # this file
├── research.md          # decisions R1–R8
├── data-model.md        # direction, token set, size class, screenshot round
├── quickstart.md        # how to take a round and compare directions
├── contracts/
│   └── ui-contract.md   # tokens every direction defines, size classes, what each class shows
├── checklists/
│   └── requirements.md
                         # the person's images live in artifacts/inspiration/ (git-ignored: pictures of other products stay out of this public repository)
├── directions/          # one tokens file per direction: a.css, b.css, c.css, each with a short README
└── decision.md          # the person's choice, written when they make it (FR-003)
```

### Source Code (repository root)

```text
src/Parley.Web/
├── Pages/Shared/_Layout.cshtml      # keeps markup and the inline first-paint script; links the stylesheets
├── Brand/                           # served at /brand/ from the assembly, as the fonts are today
│   ├── tokens.css                   # NEW: colour roles light and dark, surfaces, type, shape, elevation, motion, size classes
│   ├── frame.css                    # NEW: navigation bar, rail and drawer; top bar; banners
│   ├── components.css               # NEW: buttons, chips, fields, lists, cards, dialogs, menus, badges, state layers
│   └── pages.css                    # NEW: the rules specific to one page (conversation, inbox, graph, roadmap)
└── ConsoleHost.cs                   # serves /brand/*.css with the cache and type headers the fonts get

tests/Parley.Web.Tests/
├── MaterialTokensTests.cs           # widened to read the stylesheets as well as the layout
├── ContrastTests.cs                 # NEW: every text and outline pair, both themes, WCAG 2.2 AA
├── SizeClassesTests.cs              # NEW: every size class defined once; no page fixes a width wider than 320px
└── TouchTargetsTests.cs             # NEW: interactive controls declare the 48px minimum

tools/Parley.Screens/
├── ShotList.cs, Camera.cs           # NEW option: "stylesheets" laid over a page before capture (R2)
└── rounds/042-*/                    # the shot lists for this feature's rounds: 4 sizes × 2 themes
```

**Structure Decision**: the console stays one Razor Pages project. The only structural change is that its CSS moves from
the layout into four stylesheets served from the assembly, as its fonts already are. That is what makes a direction one
file and a pass one concern.

## Phases of the build

Each pass ends with a screenshot round (FR-024) shown to the person beside the previous one.

| Pass | What changes | Holds the line |
|---|---|---|
| 0. Extract (done) | The inline CSS moved to two stylesheets, `tokens.css` and `components.css`, unchanged. The 042-compare round matched pixel for pixel except where clock times are drawn (at most 0.12% of a picture). Tests that read the layout's CSS read the stylesheets too (`ScriptSource.Page`, `ScriptSource.WithStylesAsync`). | Pixel comparison of the round; every existing test. |
| 1. Directions | 2–3 token files under `directions/`; a round per direction at 4 sizes × 2 themes; the person picks (`decision.md`). | Contrast test run against each direction's tokens. |
| 2. Colour and surfaces (done) | Frost's surfaces, shadows and spacing land in `tokens.css` and the components; borders give way to surface levels; colour reserved for meaning; the top bar runs edge to edge. | ContrastTests; MaterialTokensTests. |
| 2b. Palettes (done) | The palette rule in C# (R9), `/brand/palette.css`, the ready-made palettes, and the colour choice on the Settings page. | ContrastTests over every palette and a sweep of base colours. |
| 3. Type | Type scale and roles; message measure; metadata quieter; dark-mode grade. | ContrastTests; type-scale test. |
| 4. Layout (after the theme, per the owner) | Size classes; navigation bar, rail and drawer; lists to cards; full-width conversation; graph panel placement; touch targets. | SizeClassesTests; TouchTargetsTests; the 320 and 390 rounds. |
| 5. Controls and motion | State layers, focus rings, motion tokens, badges with character; reduce motion. | Existing focus and motion tests, widened. |

## Complexity Tracking

| Choice | Why it is needed | Simpler alternative rejected because |
|---|---|---|
| Four stylesheets instead of one inline block | A direction must be one swappable file, and each pass one concern | Editing a 3,435-line inline block per direction makes directions three copies of the whole layout, and every comparison a diff of the wrong thing |
| A stylesheet option in the screenshot tool | Directions are compared on the real console without shipping any of them | A query switch in the console would put unchosen looks into the product, and a branch per direction triples the work of keeping them current |
