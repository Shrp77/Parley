# Implementation Plan: Material 3 across the web console

**Branch**: `035-material-3-web-ui` | **Date**: 2026-11-05 | **Spec**: [spec.md](spec.md)

## Summary

Restyle the whole web console to Material 3 with hand-written CSS: generated colour roles from #2b5cd9 (content scheme),
Roboto Flex served from the assembly, M3 type/shape/elevation/state/motion tokens, then every control kind restyled once,
then every page checked in light and dark at three widths. The audit ([audit.md](audit.md)) is the map.

## Technical Context

**Language/Version**: C# / .NET 10, Razor Pages; CSS inline in `Pages/Shared/_Layout.cshtml`; vanilla JS already on pages
**Primary Dependencies**: none added. Roboto Flex WOFF2 embedded as a resource (OFL)
**Storage**: N/A
**Testing**: xUnit v3 web tests (`Parley.Web.Tests`); page captures in headless Edge; a contrast check over the scheme
**Target Platform**: modern desktop browsers on the operator's machine (Edge, Chrome, Firefox)
**Project Type**: web console within an existing .NET solution
**Performance Goals**: no added network requests beyond one cached font file; no added script
**Constraints**: works without script where it does today; no network fetch at run time; CSP and framing unchanged; peer text
encoded; existing tests keep passing; terminal untouched
**Scale/Scope**: 23 pages, one shared stylesheet (~2,150 lines), three pages with their own style blocks

## Constitution Check

| Constraint (CLAUDE.md / constitution) | Effect |
|---|---|
| Anything drawing somebody else's text neutralises it | Unchanged: styling only; Markdown rules in `MarkdownText` untouched. |
| No page can be framed (except the roadmap's embed) | `ConsoleHost` headers unchanged. |
| A POST-only bind is `[FromForm]` | No binding changes. |
| Every operator operation on CLI, terminal and web UI | No operations added or removed. |
| Destructive acts need a description plus a deliberate act | Kept; only colours and shapes change (FR-012). |
| Version bump for shipped behaviour | Patch bump per phase. |

No violations.

## Project Structure

```text
src/Parley.Web/
├── Brand/roboto-flex.woff2, Brand/RobotoFlex-OFL.txt   # new embedded resources
├── ConsoleHost.cs                                       # one more brand route
└── Pages/Shared/_Layout.cshtml                          # tokens, base, controls
    Pages/**/*.cshtml                                    # class adjustments where a control's emphasis changes
tests/Parley.Web.Tests/MaterialTokensTests.cs            # tokens present, font served, no stray colours
specs/035-material-3-web-ui/captures/                    # light/dark captures per page (not committed: local)
```

## Phases

1. **Foundations** (F1–F6): font route and resource; `--md-sys-*` tokens light/dark; old variables aliased to roles; type,
   shape, elevation, state-layer and motion tokens; body and headings on the type scale; agent slots re-toned. Test: tokens
   and font route; no hex colour outside the token block (allow-listed exceptions documented). Commit.
2. **Controls** (C1–C28): buttons (filled/tonal/outlined/text/icon/FAB), fields, select and menus, checkbox/radio/switch,
   chips and badges, dialogs, cards, lists and tables, navigation drawer/rail, top app bar, banners, focus. Test suites and
   captures. Commit.
3. **Pages**: walk every page in the audit at 400/900/1400px, light and dark; fix layout, spacing and emphasis per page;
   contrast check over every text/surface pair. Commit.
4. **Gate**: smoke run; review per `review/`.
