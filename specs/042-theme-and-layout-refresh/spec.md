# Feature Specification: Theme and layout refresh for the web console

**Feature Branch**: `042-theme-and-layout-refresh`
**Created**: 2026-11-24
**Status**: Draft
**Input**: User description: "Theme and layout refresh for the web console, on Material 3 as the base." The owner asked
on 2026-11-24 for subtle shades and tones that give depth, type that is easy to read and draws the eye to what matters,
and some flair on the controls, keeping Material 3 as the base. Asked about the details, the owner answered:

- Light and dark get equal care: "we shouldn't dictate what the user can / can't do."
- Density and size: "it's nigh impossible to use on a phone, on a tablet in portrait it's a bit better, on landscape or a
  desktop, it's a bit compact."
- The owner has inspiration images, several showing different things they like, which they place in `artifacts/inspiration/`
  on this machine with a text file describing what they like about each.

Feature 035 moved the console to Material 3 (`specs/035-material-3-web-ui/`, with its page-by-page audit in `audit.md`).
This feature keeps that base and changes how it looks and how it fits each screen. It changes nothing the console does.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The person answers an agent from their phone (Priority: P1)

The person is away from their desk. An agent has asked them a question. They open the console on their phone, find the
question in the Inbox, read the conversation it came from, and answer it, without zooming, scrolling sideways or
hunting for tiny buttons.

**Why this priority**: The owner called the console "nigh impossible to use on a phone". Remote access by phone was
just documented (docs/remote-access.md), so this is the place a broken layout hurts most. The rest of the feature is
polish; this is a fix.

**Independent Test**: On a phone-sized screen, open the Inbox, open a waiting question, read its conversation, write
and send an answer, and open the graph. Every step works with one thumb, nothing runs off the side of the screen, and
no text needs zooming to read.

**Acceptance Scenarios**:

1. **Given** a phone held upright, **When** the person opens any page of the console, **Then** the page fits the width
   of the screen, with no sideways scrolling, and its navigation is reachable from the bottom of the screen or a menu.
2. **Given** the Inbox on a phone, **When** it lists waiting questions, **Then** each one reads as a card with who asked,
   in which conversation and when, rather than as a table row cut off at the edge.
3. **Given** a conversation on a phone, **When** the person reads it and writes an answer, **Then** the messages and the
   message box use the full width, and the box stays reachable while they read.
4. **Given** any button, link, tick box or menu on a phone, **When** the person taps it, **Then** it is large enough to hit
   first time, and nothing else sits close enough to be hit instead.
5. **Given** the graph panel on a phone, **When** the person opens it, **Then** it takes the screen in its own view
   rather than squeezing the conversation beside it.

---

### User Story 2 - The person chooses the look from pictures (Priority: P2)

Before anything is built for good, the person is shown two or three distinct directions for the look: colour, surfaces,
type and controls. Each one is shown on the same real screens, at phone, tablet and desktop sizes, in light and in dark.
The person picks one, or asks for parts of several, and only then is it built.

**Why this priority**: Taste is the person's, and words like "subtle" and "depth" mean different things to different
people. Choosing from pictures avoids building a look the person did not want. It comes before the visual stories
because they depend on its outcome.

**Independent Test**: The directions are delivered as a set of screenshots of the same screens, one set per direction,
and the person can say which they prefer and why, without running anything.

**Acceptance Scenarios**:

1. **Given** the person's inspiration images, **When** the directions are prepared, **Then** each direction says which of
   the images it draws on and what it takes from them.
2. **Given** two or three directions, **When** they are shown, **Then** every direction is shown on the same screens with
   the same sample work, at phone, tablet portrait, tablet landscape and desktop sizes, in light and in dark.
3. **Given** the person's choice, **When** the work is built, **Then** what is built matches the chosen direction, and
   any change from it is shown to the person and agreed before it lands.

---

### User Story 3 - Depth and a clear reading order, in both themes (Priority: P3)

Whichever theme the person picks, the console has depth: the page, its panels, its cards and anything that floats sit
on visibly different shades, so the eye can tell what holds what without lines drawn round everything. Text guides the
eye: names and titles stand out, the words of a message are comfortable to read, and times, numbers and labels step
back. Colour is kept for what needs attention: something waiting on the person, an agent at work, something blocked.

**Why this priority**: This is the heart of the owner's request ("subtle shades, tones that promote depth and font
styling that makes it easy to read and draws the eyes to the relevant parts"), but it is safe to ship after the layout
fix and the choice of direction.

**Independent Test**: Screenshots of the busiest pages (a long conversation, the Inbox, the agents list, the graph) in
both themes show at least three distinct surface levels, a clear difference between names, body text and metadata, and
colour only on items that need attention.

**Acceptance Scenarios**:

1. **Given** either theme, **When** a page shows panels, cards and a dialog, **Then** each sits on a different shade, and
   a shadow appears only under what floats above the page (dialogs, menus, the pinned message box).
2. **Given** a conversation, **When** the person reads it, **Then** the sender's name is the strongest text in each
   message, the message's words are the easiest to read, and its time, exchange number and "written by" line are smaller
   and quieter than both.
3. **Given** a list of agents and exchanges, **When** some are waiting on the person, working or blocked, **Then** those
   stand out by colour, and the rest share calm, slightly tinted neutrals.
4. **Given** the person switches between light and dark, **When** they compare the same page, **Then** neither theme looks
   like an afterthought: both have the same depth, hierarchy and readability.

---

### User Story 4 - Controls that respond (Priority: P4)

Buttons, chips, menus, tabs and list rows answer the person's touch, click or key: a visible response on hover and
press, smooth opening and closing of panels and the message box, and a clear ring around whatever the keyboard has
reached. Status badges and chips have some character, for example a gentle sign of life on an agent that is working.

**Why this priority**: The owner is "not opposed to adding flair to the controls", and responsive controls make the
console feel solid. It is the last layer, built on the chosen direction.

**Independent Test**: With a mouse, a keyboard and a touch screen, each kind of control shows a response to hover,
press and focus, and with the system's "reduce motion" setting on, nothing moves except what is needed to show state.

**Acceptance Scenarios**:

1. **Given** any control, **When** the person hovers, presses or reaches it with the keyboard, **Then** it shows a
   distinct, consistent response for each.
2. **Given** the system's reduce-motion setting is on, **When** panels open or badges change, **Then** they change
   without animation.
3. **Given** an agent that is working, **When** its badge is shown, **Then** it reads as active at a glance, and it is not
   distracting enough to pull the eye from reading.

---

### User Story 5 - More room on a tablet in landscape and on a desktop (Priority: P5)

On a wide screen the console uses the space: less cramped spacing, comfortable line lengths, and panels that sit side
by side when there is room for both, such as the graph beside a conversation without squeezing it.

**Why this priority**: The owner found these sizes "a bit compact", which is a smaller problem than the phone. It may
partly be solved by the other stories.

**Independent Test**: At tablet-landscape and desktop sizes, the busiest pages show readable line lengths, spacing that
does not feel crowded, and the graph pinned beside a conversation leaves the conversation at a readable width.

**Acceptance Scenarios**:

1. **Given** a desktop screen, **When** a conversation is open, **Then** message text keeps a comfortable line length
   rather than stretching to the full width.
2. **Given** the graph pinned beside a conversation, **When** the screen is too narrow for both at a readable width,
   **Then** the console gives one of them the screen rather than squeezing the conversation to a few words a line.
3. **Given** the person prefers a denser view, **When** they choose the compact setting on the Settings page, **Then** the
   spacing tightens, and the choice is kept in their browser as today.

---

### Edge Cases

- **Very small phones and large text.** A phone 320 points wide, or a person who has made text larger in their browser
  or system, still gets a page that fits and reads, with nothing clipped.
- **Long words and names.** A long conversation name, agent name, file name or unbroken string in a message wraps or
  is shortened with a way to see the whole, and never pushes the page wider than the screen.
- **Rotation.** Turning a tablet or phone between portrait and landscape keeps the person's place and anything typed.
- **The framed conversation.** The roadmap page shows a conversation inside a frame. The new layout works inside that
  frame as well as on its own.
- **No script.** Pages that work without script today still work without it. The look may be simpler, but nothing is
  lost.
- **Reduce motion and high contrast.** With the system's reduce-motion setting on, animations stop. With a forced-colours
  or high-contrast mode, text and controls stay visible and usable.
- **Inspiration images that pull different ways.** If the person's images suggest conflicting looks, the directions
  show the alternatives side by side rather than blending them into something none of them is.

## Requirements *(mandatory)*

### Functional Requirements

**Choosing the direction**

- **FR-001**: Before the look is built, two or three distinct directions MUST be prepared, each covering colour,
  surfaces, type and controls, and each saying which of the person's inspiration images it draws on.
- **FR-002**: Each direction MUST be shown as screenshots of the same set of screens, with the same sample work, at four
  sizes (phone, tablet portrait, tablet landscape, desktop), in light and in dark.
- **FR-003**: The person's choice MUST be recorded in the feature's folder before the build starts, and every later
  change from it MUST be shown to the person and agreed.

**Layout at every size**

- **FR-004**: Every page of the console MUST fit the width of the screen at every size from a 320-point-wide phone up,
  with no sideways scrolling of the page itself.
- **FR-005**: On a phone, navigation MUST be reachable without stretching: from the bottom of the screen or from a menu
  that opens over the page.
- **FR-006**: On a phone, lists and tables that do not fit (the Inbox, the agents, the conversations, files, roadmaps,
  health) MUST be shown as cards or stacked rows that keep every piece of information the table showed.
- **FR-007**: On a phone, a conversation's messages and its message box MUST use the full width, and the message box MUST
  stay reachable while the person reads.
- **FR-008**: Every control a person can tap MUST be at least 48 by 48 CSS pixels to touch, or have that much space around
  its target, at every size.
- **FR-009**: The graph panel MUST never squeeze a conversation below a readable width. Where both cannot fit side by
  side at a readable width, it MUST open in a view of its own.
- **FR-010**: On tablet landscape and desktop, message text MUST keep a comfortable line length (around 75 characters at
  most), and spacing MUST be roomier than today unless the person has chosen the compact setting.

**Look and hierarchy, in both themes**

- **FR-011**: Light and dark themes MUST both be complete: every colour role, surface level and state the console uses
  is defined for each, and neither falls back to the other.
- **FR-012**: Pages MUST show depth through at least three distinct surface shades (page, panel or card, and raised
  items), with shadows only under elements that float above the page.
- **FR-013**: Text MUST have a clear order: names and titles strongest, body text easiest to read, and metadata (times,
  exchange numbers, "written by", sizes, hashes) smaller and quieter.
- **FR-014**: Colour MUST be reserved for meaning: what is waiting on the person, an agent at work, something blocked or
  failed, and the primary action on a page. Everything else uses tinted neutrals.
- **FR-015**: Every pair of text and its background, and every control's outline against its surroundings, MUST meet
  WCAG 2.2 AA contrast in both themes, and a test MUST fail if a pair drops below it.
- **FR-016**: The person's existing choices (light, dark or the system's theme, and normal or compact density) MUST keep
  working and keep being remembered in their browser.

**Controls and motion**

- **FR-017**: Every interactive control MUST show distinct, consistent states for hover, press and keyboard focus, and
  the focus state MUST be visible against both themes' surfaces.
- **FR-018**: Panels, menus, dialogs and the message box MUST open and close with motion that respects the system's
  reduce-motion setting, showing no animation when it is on.
- **FR-019**: Status badges and chips (working, waiting on background work, frozen, open outside Parley, blocked) MUST
  each be recognisable at a glance without reading their words, and MUST still carry their words for anyone who needs
  them.

**What must not change**

- **FR-020**: Nothing the console does MUST change: every page, action, form, link and keyboard key works exactly as
  before, and the existing tests of the console's behaviour MUST pass unchanged.
- **FR-021**: Every message MUST still be drawn by the one message view, and every box a person types in by the one
  message box, as today.
- **FR-022**: The terminal (`parley-terminal`) MUST NOT change.
- **FR-023**: The console MUST keep working with no script, as each page does today, and inside the roadmap page's frame.

**How the work is shown**

- **FR-024**: After each pass of the build (colour and surfaces, type, layout, controls and motion), the same set of
  screenshots MUST be retaken at the four sizes in both themes and shown to the person beside the previous set.

### Key Entities

- **Direction**: one candidate look, made of colour roles for light and dark, surface levels, a type scale, shapes,
  state layers and motion. It has a name, the inspiration images it draws on, and a set of screenshots.
- **Screen size class**: a band of screen widths the layout adapts to: phone, tablet portrait, tablet landscape and
  desktop.
- **Screenshot round**: the same screens, with the same sample work, captured at every size class in both themes, for
  one direction or after one pass of the build.
- **Inspiration image**: a picture the person provides, with a note on what they like about it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a phone, the person can go from opening the console to having answered a waiting question in under one
  minute, with no zooming and no sideways scrolling.
- **SC-002**: Every page of the console fits a 320-point-wide screen and a 390-point-wide screen with nothing cut off and
  no sideways scrolling, checked on every page in both themes.
- **SC-003**: 100% of tappable controls meet the 48-by-48 touch size, or have the space around them, at phone size.
- **SC-004**: 100% of text and control colour pairs meet WCAG 2.2 AA in both themes, and the test that checks them fails
  on any pair that does not.
- **SC-005**: The person chooses a direction within one round of screenshots, or within two if they ask for a mix.
- **SC-006**: Every existing test of the console's behaviour passes without being changed.
- **SC-007**: The person, shown the before and after screenshots of the busiest pages side by side, says that the after
  set has more depth, is easier to read, and is usable on their phone.

## Assumptions

- **Material 3 stays the base.** The directions differ in colour, surfaces, type, shape and motion within Material 3's
  system, not in abandoning it. The owner is not attached to the current seed colour, type choices or spacing.
- **The size classes are Material 3's window size classes**: compact (under 600 CSS pixels, phones), medium (600 to 839,
  tablet portrait), expanded (840 to 1199, tablet landscape) and large (1200 and up, desktop).
- **Roboto Flex stays the typeface** unless a direction shows a reason to change and the person agrees. It is already
  shipped with the console and needs no network to load.
- **The screenshot tool (`tools/Parley.Screens`, PR #113) is used for every round**, with phone and tablet sizes added to
  its shot list. If it has not merged when the first round is due, it is run from its branch.
- **The person's inspiration images arrive in `artifacts/inspiration/` on this machine**, with a text file saying what
  they like about each; that folder is ignored by git, so pictures of other products never enter this public
  repository. If they have not arrived when
  the directions are prepared, the directions are built from the brief alone and the images are applied in a second
  round.
- **The compact density setting stays.** The new default spacing is roomier on large screens, and the compact choice on
  the Settings page tightens it, as today.
- **"No behaviour change" includes keys and forms.** Every key the console takes, every form's fields and every page's
  address stay as they are, so the existing tests of the console hold the line.
- **This is a visual feature.** It needs no change to the protocol, the broker, the store or the terminal, and no
  migration.
