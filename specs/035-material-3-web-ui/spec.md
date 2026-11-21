# Feature Specification: Material 3 across the web console

**Feature Branch**: `035-material-3-web-ui`
**Created**: 2026-11-05
**Status**: Draft
**Input**: User description: "The whole web console is to follow Material 3 for design, layout and controls." The owner's
decisions of 2026-11-05: hand-built styles on the console's existing pages, not a third-party component library; the
seed colour is today's accent, #2b5cd9, with light and dark themes following the system setting; Roboto Flex is shipped
inside the console as the icon font already is, with nothing fetched from the internet; the work runs in phases, starting
with an audit the owner approves.

## Context

The operator web console is how the person reads conversations, answers agents, and runs a deployment. It has grown page
by page over many features: 23 pages, one shared stylesheet of about 2,150 lines, and controls each styled where they were
first needed. It already uses Material Symbols icons and a navigation rail, but its colours, type, shapes, buttons, fields,
menus and dialogs follow no one system, so the same kind of control looks and behaves differently from page to page.

Material 3 is Google's published design system. Following it gives the console one set of colour roles, one type scale,
one shape scale, one way of showing that something can be pressed, hovered or focused, and one shape for each kind of
control, in light and dark.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The owner approves a page-by-page plan before anything changes (Priority: P1)

Before any page changes, the owner receives an audit: every page of the console, every kind of control on it, and the
Material 3 control or pattern each becomes, with anything that has no Material 3 equivalent called out with a proposal.
The owner approves it, or changes it, before work starts.

**Why this priority**: The change touches every page a person uses. An agreed map is the only way to know when it is done,
and the owner asked for it.

**Independent Test**: The audit can be read on its own. Every page in the console appears in it, and every control on each
page has a row naming what it becomes.

**Acceptance Scenarios**:

1. **Given** the console as it is today, **When** the audit is delivered, **Then** each of the 23 pages is listed with each
   kind of control it carries and the Material 3 equivalent for each.
2. **Given** a control with no direct Material 3 equivalent (for example the conversation minimap), **When** the audit lists
   it, **Then** it says so and proposes how it will be made consistent with the system.
3. **Given** the owner's changes to the audit, **When** work starts, **Then** it follows the approved version.

---

### User Story 2 - The whole console looks like one product, in light and in dark (Priority: P1)

The person moves between pages (conversations, the inbox, agents, roadmaps, files, health, settings) and every page uses
the same colours for the same meaning, the same type sizes for the same kind of text, the same corner shapes and the same
depth for raised surfaces. Switching the computer between light and dark mode switches the console with it, and both
read clearly.

**Why this priority**: This is the visible result the owner asked for, and every later control builds on it.

**Independent Test**: Open any two pages in light and in dark. Each colour, type size, corner shape and surface depth on
one page means the same thing on the other.

**Acceptance Scenarios**:

1. **Given** the system is in light mode, **When** any page is opened, **Then** its colours come from one palette derived
   from the seed colour #2b5cd9.
2. **Given** the system switches to dark mode, **When** the page is shown, **Then** it uses the dark palette from the same
   seed, with no part left in light colours.
3. **Given** any two pages, **When** the same kind of text appears on both (a page title, a section heading, body text, a
   label), **Then** it has the same size, weight and line height on both.
4. **Given** the console is used with no internet connection, **When** any page loads, **Then** it looks the same as when
   connected: the typeface and icons are the console's own.

---

### User Story 3 - Every control behaves the way Material 3 says it should (Priority: P2)

Buttons, text fields, checkboxes, chips, menus, dialogs, lists, cards, the navigation rail, the top bar, notices and
tooltips each take the Material 3 form for their role. They all show hover, press and keyboard focus the same way, and a
disabled control looks disabled everywhere.

**Why this priority**: Consistent controls are what make the console predictable. The person learns how something behaves
once and it holds everywhere.

**Independent Test**: For each kind of control, find every place it appears and confirm each looks and responds the same,
including with the keyboard alone.

**Acceptance Scenarios**:

1. **Given** a page's main action and its secondary actions, **When** shown, **Then** the main action is the most emphatic
   button and the others take lesser emphasis, as Material 3 orders them.
2. **Given** any control that can be pressed, **When** the pointer is over it, it is pressed, or it has keyboard focus,
   **Then** it shows that state the same way as every other control.
3. **Given** a person using only the keyboard, **When** they move through any page, **Then** the focused control is always
   plainly visible and every action can still be reached.
4. **Given** a destructive action (destroying a conversation, purging, removing files), **When** its controls are shown,
   **Then** they use the error colour role and keep the deliberate step they require today.

---

### User Story 4 - Each page is laid out the Material 3 way at every window size (Priority: P3)

Each page is reworked so its layout follows Material 3: content in cards or lists where that is the pattern, spacing on the
system's grid, and the layout adapting to the window, from a narrow window to a wide one.

**Why this priority**: It completes the change but depends on stories 2 and 3, and each page can be done and checked on its
own.

**Independent Test**: For one page, compare light and dark captures at a narrow, a medium and a wide window against the
approved audit.

**Acceptance Scenarios**:

1. **Given** a page that has been reworked, **When** it is captured in light and dark at a narrow, medium and wide window,
   **Then** the captures match the approved audit and nothing overlaps, is cut off or scrolls sideways.
2. **Given** a page that works without script today, **When** it is opened with script turned off, **Then** it still shows
   its content and its forms still work.

---

### Edge Cases

- A colour that carries meaning today (an agent's slot colour, a closed-by-a-person marker, an alarm) keeps its meaning
  and is not left to colour alone where it is not today.
- An agent's text drawn as Markdown (headings, tables, code, quotes) takes the console's type and colours without being
  able to imitate the console's own controls or labels.
- A page embedded in the roadmap page's frame looks right inside the frame.
- A person who asks the system for reduced motion sees no animation that is not needed to understand a change.
- A very long name, path or message never breaks the layout or pushes controls off screen.
- High-contrast and forced-colour modes leave every control visible and usable.

## Requirements *(mandatory)*

### Functional Requirements

**Audit and phases**

- **FR-001**: An audit MUST list every page of the web console, every kind of control on each, and the Material 3
  equivalent for each, including what has no direct equivalent and how it will be handled.
- **FR-002**: No page MUST change its appearance before the owner has approved the audit.
- **FR-003**: The work MUST be delivered in phases (the system's foundations first, then controls, then page by page), and
  each phase MUST be tested, committed and reviewed before the next begins.

**Foundations**

- **FR-004**: Every colour in the console MUST come from a Material 3 colour scheme derived from the seed colour #2b5cd9,
  in a light and a dark variant.
- **FR-005**: The console MUST follow the system's light or dark setting, as it does today.
- **FR-006**: Text MUST use the Material 3 type scale in the Roboto Flex typeface, shipped with the console so nothing is
  fetched from elsewhere.
- **FR-007**: Corner shapes, surface depth, the hover, press and focus states, and motion MUST each follow the Material 3
  scale for them, applied the same way everywhere.
- **FR-008**: Text and controls MUST meet at least the WCAG 2.2 AA contrast levels in both themes.
- **FR-009**: Motion MUST respect the system's reduced-motion setting.

**Controls**

- **FR-010**: Each of these MUST take its Material 3 form wherever it appears: buttons at each level of emphasis, icon
  buttons, text fields, checkboxes, chips, menus, dialogs, lists, cards, the navigation rail, the top bar, notices and
  tooltips.
- **FR-011**: Every control MUST show a visible keyboard focus indicator, and no action MUST become reachable only by
  pointer.
- **FR-012**: Destructive actions MUST use the error colour role and keep every confirmation step they require today.
- **FR-013**: A disabled control MUST look disabled the same way everywhere.

**Pages**

- **FR-014**: Every page MUST be reworked to the approved audit, and captured in light and dark for the owner.
- **FR-015**: Layouts MUST adapt to narrow, medium and wide windows following Material 3's guidance, with no sideways
  scrolling of the page.

**What must not change**

- **FR-016**: Every page and form that works without script today MUST still work without script.
- **FR-017**: The console MUST NOT fetch anything from the internet at run time.
- **FR-018**: The console's security rules on framing, content and which hosts it answers MUST be unchanged.
- **FR-019**: Text written by an agent or any other participant MUST still be shown as content that cannot alter the page
  or imitate the console's own controls.
- **FR-020**: Every behaviour the console has today (following a conversation, filters, threads, the comment box,
  attachments, the agent tree, keyboard sending) MUST keep working, and the console's existing tests MUST keep passing.
- **FR-021**: The terminal surface MUST NOT change.

### Key Entities

- **Audit**: one row per page and kind of control, naming today's control, its Material 3 equivalent, and any decision
  needed.
- **Colour scheme**: the set of colour roles (primary, secondary, tertiary, error, surfaces and their "on" colours,
  outline) in light and dark, derived from the seed.
- **Page capture**: a picture of one page, in one theme, at one window size, kept as the record that the page meets the
  audit.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All 23 pages appear in the approved audit, and every row in it is marked done by the end.
- **SC-002**: Every page has light and dark captures at narrow, medium and wide window sizes, and the owner accepts them.
- **SC-003**: No text or control on any page falls below WCAG 2.2 AA contrast, in either theme.
- **SC-004**: Every interactive control on every page shows a visible focus indicator when reached by keyboard, and every
  action on every page can be completed with the keyboard alone.
- **SC-005**: Every existing test of the web console passes at the end of each phase.
- **SC-006**: With the network disconnected, every page renders identically to the connected render.
- **SC-007**: Each kind of control looks and responds identically in every place it appears, as checked against the
  audit.

## Assumptions

- "Material 3" means the guidance published at m3.material.io as of November 2026, including its colour, type, shape,
  elevation, state, motion and layout guidance and its component specifications.
- The colour scheme is derived from the seed with Material 3's standard method, and agents' slot colours are adjusted to sit
  within it while staying distinct from one another.
- Roboto Flex's licence (SIL Open Font License) allows shipping it inside the console.
- The person's use is mainly on a desktop browser, and narrow windows must still work.
- The work is reviewed under the repository's usual merge gate before it merges.
- The audit, the captures and the phase reviews are delivered to the owner as they are ready, in this branch.
