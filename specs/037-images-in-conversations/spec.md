# Feature Specification: Images in the conversation, a Media view, and recipient chips

**Feature Branch**: `037-images-in-conversations`
**Created**: 2026-11-08
**Status**: Draft
**Input**: User description: "Images in the conversation, a Media view, and recipient chips in the web console. (1) An image a participant places in a conversation is shown in that conversation's transcript where it was placed, as a thumbnail that opens full size, so a person who asks an agent "what does this look like" sees the answer in the conversation. This reverses SPEC.md §9.5's rule that an operator surface shows a thumbnail only of what the person attaches and never serves a placed image back to a browser. (2) A Media view on a conversation, like WhatsApp's media screen. (3) In the composer, the To: line shows each chosen agent as a chip with an x that takes it off the To list in one click."

## Why

A person asks an agent "what does this look like?" The agent places a screenshot. Today the person sees a file name in
a list and has to open it in another program, and only on the machine that holds the files. The answer should be in the
conversation, where the question was asked.

This reverses a rule the owner made in SPEC.md §9.5: *"An operator surface shows a thumbnail only of what the person is
attaching, and does not serve a placed image back to a browser."* The owner decided on 2026-11-08 to reverse it for
usability. SPEC.md must record the change as a further **reduction in the control**, together with what still holds
and what the serving must answer (FR-010 to FR-016).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See an agent's image in the conversation (Priority: P1)

The person asks an agent how a page looks. The agent places a screenshot in the conversation. In the web console the
person sees a thumbnail in the transcript, at the point where it was placed, with who placed it. Clicking it shows the
image full size on the same page. Escape or a close button takes the person back where they were.

**Why this priority**: This is the reason for the feature. Without it the other two stories are conveniences.

**Independent Test**: An agent places `mockup.png` in a conversation. Open the conversation in the web console. A
thumbnail of `mockup.png` shows between the messages sent before and after the placement, naming the agent. Clicking
it shows the full image. Closing it leaves the reader at the same place in the transcript.

**Acceptance Scenarios**:

1. **Given** an agent placed a PNG, JPEG, GIF or WebP file in a conversation, **When** the person opens that
   conversation in the web console, **Then** the transcript shows a thumbnail of it at its placement time, with the
   placer's name, the file name and the time.
2. **Given** a thumbnail in the transcript, **When** the person clicks it or presses Enter on it, **Then** the image
   opens full size over the page, with its name, placer, size and a way to close it; closing returns focus to the
   thumbnail.
3. **Given** the person attached an image to their own message, **When** it is sent, **Then** it shows in the
   transcript the same way an agent's does.
4. **Given** a placed file was later removed, **When** the person reads the transcript, **Then** it is no longer
   shown there (the broker keeps no record of a removal; see FR-004).
5. **Given** the web console runs on a different machine from the broker (through the SSH tunnel), **When** the person
   opens a conversation with an image, **Then** the thumbnail still shows.
6. **Given** the terminal, **When** a conversation holds a placed file, **Then** the transcript shows a line at its
   placement time naming the placer, the file name, its kind and size, and the file can be opened from there in the
   machine's own program, as the Files view opens it today.

---

### User Story 2 - Media view of a conversation (Priority: P2)

From a conversation the person opens *Media*. It shows every image in the conversation as a grid of thumbnails, and
every other file (`.md`, `.txt`) as a list, newest first. Each opens the same way it does from the transcript. Each
item also says who placed it and when, and has a way back to its place in the transcript.

**Why this priority**: Once images are in transcripts, a long conversation needs one place to find them. It builds
on Story 1's display but can be tested on its own.

**Independent Test**: In a conversation holding three images and two text files, open Media. Three thumbnails show,
newest first, and two files in a list. Opening each behaves as in Story 1 or as the Files view does today.

**Acceptance Scenarios**:

1. **Given** a conversation with images and text files, **When** the person opens Media in the web console, **Then**
   images show as a grid of thumbnails and other files as a list, both newest first, each with placer and time.
2. **Given** an item in Media, **When** the person chooses "Show in conversation", **Then** the transcript opens
   scrolled to that placement.
3. **Given** a conversation with no files, **When** the person opens Media, **Then** it says there are no files in
   this conversation.
4. **Given** the terminal, **When** the person opens Media on a conversation, **Then** a dialog lists images first and
   then other files, newest first, each with name, placer, time and size, and Enter opens the chosen one in the
   machine's own program; the dialog says images cannot be drawn in a terminal.
5. **Given** removed files, **When** Media is open, **Then** they do not appear.

---

### User Story 3 - Recipients as chips on the To line (Priority: P3)

In the web console composer, the To line shows each chosen agent as a chip with an x. Clicking the x takes that agent
off the To list. The tick for that agent in the To box clears at the same moment. Ticking an agent in the To box adds
its chip.

**Why this priority**: A small speed-up for a common correction. It is independent of the other two stories.

**Independent Test**: Choose two agents in the To box. The To line shows two chips. Click the x on one. One chip
remains and that agent's tick in the To box is cleared. Send, and only the remaining agent is addressed.

**Acceptance Scenarios**:

1. **Given** agents ticked in the To box, **When** the composer is drawn, **Then** the To line shows one chip per
   ticked agent, in the To box's order, each with an x labelled "Remove <name>".
2. **Given** a chip, **When** the person clicks its x or focuses it and presses Enter, Space, Backspace or Delete,
   **Then** the agent leaves the To list, its tick clears, and focus moves to the next chip, or to the text box when
   there is none.
3. **Given** the last chip is removed, **When** the To line redraws, **Then** it reads "To: nobody chosen yet." as
   today and Send behaves as it does today with nobody chosen.
4. **Given** a conversation addressed to everyone in it, **When** the composer is drawn, **Then** the To line keeps
   its current wording ("To: everyone in this conversation.") and shows no chips.

---

### Edge Cases

- A file named `.png` whose bytes are not a PNG, such as HTML or a script. It must never be run or shown as anything
  but an image. When its bytes are not the format its name says, it shows as a tile ("not a readable image"), never as
  a page.
- A very large image, or one whose pixel size is enormous (a decompression bomb). Above the inline limit (Assumptions)
  it shows as a tile with its size and an explicit "Show anyway". It is never drawn by itself.
- An animated GIF or WebP. A thumbnail does not animate. The full view does.
- A conversation with hundreds of images. The transcript and Media load thumbnails only as they come into view.
- An image placed in a conversation that was later moved or spun off (§5.0). Files stay with their conversation, so
  the image stays where it is and does not follow a moved exchange, as today.
- The conversation page is embedded in the roadmap page (`ConsoleHost.Embedded`). Thumbnails and the full view work
  there too.
- An image's name holds control characters or right-to-left marks. Names are already restricted to `A–Z a–z 0–9 . _ -`
  (§9.5), so none can be present. The display still neutralises them, as it does all text somebody else wrote.

## Requirements *(mandatory)*

### Functional Requirements

**Images in the transcript**

- **FR-001**: The web console MUST show each placed image (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`) in the
  conversation's transcript at its placement time, as its own entry attributed to its placer (an agent, or the person).
  It is not drawn inside any message.
- **FR-002**: The position MUST come from the placement record (placer and time), never from parsing a message's text.
  Message payloads stay inert data (§13). A message that names a file does not move or show it.
- **FR-003**: A thumbnail MUST open a full-size view on the same page. That view MUST show the file name, placer, time
  and size. It MUST close with Escape or a close control, returning focus to the thumbnail.
- **FR-004**: A file placed and later removed is no longer shown, in the transcript or in Media. *Amended in planning:*
  the first draft kept a "removed" tile, but the broker keeps no record of a removal, and keeping one is a schema change
  that would have taken migration 008 from feature 027 (008 now records where a move falls, and 027 moves to 009). A removal is already told to those who fetched the file.
- **FR-005**: A non-image placed file (`.md`, `.txt`) MUST appear in the transcript as a file tile with its name,
  placer, time and size, opening the way the Files view opens it today.
- **FR-006**: The terminal MUST show every placement as a transcript line (placer, name, kind, size). From there the
  file MUST open in the machine's own program, under the same rules the Files view follows today.

**Media view**

- **FR-007**: The web console MUST offer a Media view on each conversation: images as a thumbnail grid, other files as
  a list, both newest first, each with placer and time, each opening as in FR-003 or FR-005, and each with a way to its
  place in the transcript.
- **FR-008**: The terminal MUST offer the same Media view as a dialog. It lists images and then other files, newest
  first, with name, placer, time and size, and opens the chosen file in the machine's own program. It MUST say that a
  terminal cannot draw images.
- **FR-009**: Media MUST show only files present now, as the transcript does (FR-004).

**Serving another participant's bytes to a browser** (what the reversal must answer)

- **FR-010**: The console MUST serve to a browser only files whose names end in one of the four image extensions, and
  only from the conversation the page is showing. Text files are never served to the browser by this feature; they
  open as today.
- **FR-011**: Each image MUST be served with the content type its extension names, with content-type sniffing switched
  off, and with response rules that forbid running anything in it (no scripts, no frames, no plugins), whether it is
  loaded in a page or opened by itself.
- **FR-012**: Before serving, the console MUST check that the file's leading bytes are the signature of the format its
  name says. A file that fails is shown as a "not a readable image" tile and is never served. This is the first place
  Parley inspects a placed file's bytes. The broker still does not, and placement is unchanged. SPEC.md MUST say so.
- **FR-013**: An image larger than the inline limit MUST show as a tile with its size until the person chooses to
  show it.
- **FR-014**: The serving MUST be read-only and MUST NOT mark anything delivered or change any record. Viewing an
  image is not an act on the deployment.
- **FR-015**: The serving MUST follow every rule the console already has: loopback and bound host names only, no
  framing outside its own embed, and the same refusal for a conversation or file that does not exist as for one the
  page may not show.
- **FR-016**: SPEC.md §9.5 MUST replace the sentence *"An operator surface shows a thumbnail only of what the person is
  attaching, and does not serve a placed image back to a browser"* with the new rule. It MUST record the change as a
  reduction in the control, saying:
  - **What still holds:** a model that opens an image reads any words in it. That threat is to models, and showing the
    image to the person changes nothing about it.
  - **What is new:** another participant's bytes now reach the person's browser.
  - **What answers it:** FR-010 to FR-013.
  - **What remains:** a flaw in the browser's own image decoder.

  The same change MUST sweep every other statement of the old rule (Files view remarks, the composer's comment, tests
  that hold it) so the new rule is the only one stated.

**Recipient chips**

- **FR-017**: The web console composer's To line MUST show each chosen agent as an input chip with a remove control.
  The remove control MUST be a real button with an accessible name naming the agent.
- **FR-018**: Removing a chip MUST untick that agent in the To box, and ticking or unticking in the To box MUST add or
  remove the chip. The two always agree, and what is sent is what both show.
- **FR-019**: Removing the last chip MUST leave the To line and Send exactly as they are today with nobody chosen. "To
  everyone in this conversation" shows no chips.
- **FR-020**: Chips MUST be reachable and removable from the keyboard (FR scenario 3.2) without a plain-character
  shortcut, consistent with the console's composer keys.

### Key Entities

- **Placement** (exists): a file in a conversation's folder, with name, size, hash, placer (an agent, or the person)
  and placement time. This feature reads it and adds nothing to it.
- **Transcript entry for a placement** (new, derived): a placement shown among messages by time. Not stored.
- **Media view** (new, derived): a conversation's present placements grouped into images and other files.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person who asks an agent for a screenshot can see it within one click of opening the conversation, with
  no other program and on any machine that reaches the console.
- **SC-002**: Every placed image in a conversation of 200 images is reachable from Media in two actions (open Media,
  choose the image).
- **SC-003**: A file named as an image whose bytes are HTML or script is never rendered or run by the browser, by any
  route the console offers (in a page, opened by itself, or in the full view). Tests hold this.
- **SC-004**: Taking an agent off the To list takes one click, and the To box and To line never disagree.
- **SC-005**: Opening a conversation with images loads no more image bytes than the thumbnails in view need.

## Assumptions

- The inline limit (FR-013) is 20 MB per image. Above it the person chooses to show it. The number is changeable and
  recorded like the message limits.
- Thumbnails are the original image scaled by the browser. No smaller copy is made or stored, because making one means
  decoding another participant's bytes in Parley's own process.
- An image placed before this feature shows in the transcript like any other. Nothing is migrated: placements already
  record placer and time.
- Agents see nothing new. No agent operation changes, and the protocol does not move for this feature beyond any
  operator read that serving needs (decided in the plan).
- The terminal shows no image and keeps its keys rule: Media gets an Alt-letter or declared chord in its command
  table, decided in the plan.
- The To chips are web-console-only. The terminal's way of choosing recipients is unchanged.
- This feature is built on the 035 branch (Material 3). Chips use 035's input chip and the full view uses its dialog
  styles.
