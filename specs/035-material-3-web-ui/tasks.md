# Tasks: Material 3 across the web console

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [audit.md](audit.md)

## Phase 1: Setup

- [ ] T001 Write the audit of every page and control in specs/035-material-3-web-ui/audit.md
- [ ] T002 Add Roboto Flex as src/Parley.Web/Brand/roboto-flex.woff2 with its licence, embedded in src/Parley.Web/Parley.Web.csproj and served from src/Parley.Web/ConsoleHost.cs

## Phase 2: Foundations (US2)

- [ ] T003 [US2] Add Material 3 colour roles (content scheme from #2b5cd9) light and dark in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T004 [US2] Alias the console's colour variables to M3 roles in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T005 [US2] Add type, shape, elevation, state-layer and motion tokens, and set body and headings on the type scale in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T006 [US2] Re-tone the agent slot colours in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T007 [US2] Replace stray hard-coded colours with roles across src/Parley.Web/Pages/**/*.cshtml
- [ ] T008 [US2] Add tests/Parley.Web.Tests/MaterialTokensTests.cs: tokens in both themes, font served from the assembly, no hard-coded colour outside the token block
- [ ] T009 [US2] Commit phase 1 with a version bump

## Phase 3: Controls (US3)

- [ ] T010 [US3] Buttons: filled, tonal, outlined, text, icon, FAB, error variants with state layers in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T011 [US3] Text fields, selects, menus and the agent filter in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T012 [US3] Checkbox, radio and switch in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T013 [US3] Chips and badges, dialogs, cards, lists and tables in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T014 [US3] Navigation drawer/rail and top app bar in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T015 [US3] Banners and the focus indicator in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [ ] T016 [US3] Map each page's controls to the right emphasis (classes) across src/Parley.Web/Pages/**/*.cshtml
- [ ] T017 [US3] Run the web tests, capture, commit phase 2

## Phase 4: Pages (US4)

- [ ] T018 [US4] Capture every page light/dark at 400, 900 and 1400px from test consoles, and fix layout per the audit
- [ ] T019 [US4] Contrast check every text/surface pair the stylesheet uses, in both themes
- [ ] T020 [US4] Check pages without script and with reduced motion; commit phase 3

## Phase 5: Polish

- [ ] T021 Smoke run, hygiene check, mark the audit's status, report to the owner
