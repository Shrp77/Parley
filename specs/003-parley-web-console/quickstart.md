# Quickstart: Parley Web Console

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-08-30

How to run the console and confirm it is telling the truth. Written for the person doing the
work; the acceptance criteria it maps to are named in brackets.

## Before it will run

A broker must be running for at least one project, and that project must be named in the
machine's manifest. The console reads the manifest and connects outward; it never opens the
store, so there is no database path to configure and it will refuse one.

```
dotnet run --project src/Parley.Web
```

It binds loopback and prints the address. Binding anything else is a deliberate act and the
console says so at startup when you do it.

## What to check, in order

### 1. Projects

Open the root. Every project in the manifest is listed. [FR-001]

Stop one project's broker and reload. That project reports as unreachable within about five
seconds; every other project still renders. [FR-004, SC-004]

Configure no projects at all. The page explains that none are named and how to name one, rather
than rendering an empty list that looks identical to a fault. [edge case]

### 2. Agents

Select a reachable project. Every registered agent appears with its name, what it says it owns,
its tier and its presence. [FR-005]

Retire an agent from the CLI and reload: it appears as retired rather than vanishing. [FR-006]

Find an agent with no `owns` line — the console says it has not stated one, and does not describe
it. [FR-007]

If two projects each contain an agent of the same name, confirm each appears only under its own
project. [FR-002, edge case]

### 3. A conversation — the part worth testing carefully

Build the three-participant case from §5.0 by hand: `orders` asks `billing`; `orders` asks
`inventory` under the same conversation identifier; then `billing` asks `inventory` under it too.
Answer some and leave one open.

Then check, in the transcript:

- All three exchanges appear, in the order the broker recorded them. [FR-008]
- Every message names its pair. [FR-009]
- On the message `billing` sent to `orders`, the transcript says inline that `inventory` did not
  see it. Not behind a control you have to open. [FR-010, FR-011]
- The open exchange is shown as open, not as concluded or omitted. [FR-012 — **needs Gap 1**]
- Responder identity appears for each message. [FR-013]
- The view states that ordering follows the broker's record, not a wall clock. [FR-014]

Then the one that matters most: **read the transcript and write down who saw what.** Compare it
against the exchanges you created. If a person reading the page would conclude that `inventory`
had seen `billing`'s answer to `orders`, the console has failed its central obligation, whatever
else works. [SC-002, SC-003]

### 4. Finding it in the first place

Locate that conversation without typing its identifier — by participant, and by time.
[FR-015 — **needs Gap 2**]

### 5. Message text is data

Have an agent send a message whose text contains HTML and a line imitating the console's own
labelling — for instance text that looks like a visibility annotation. Confirm it renders as
content, changes nothing about the page, and cannot be mistaken for the console speaking.
[FR-016, edge case]

### 6. Registration instructions

Open the instructions for a selected project. They name that project's endpoint rather than a
placeholder, state that identity comes from the launch configuration, and say how to confirm the
registration worked. No resolved credential value appears anywhere. [FR-017…FR-020]

Then use them: register a new agent in a scratch repository and confirm it appears in the
roster. [SC-006]

### 7. The boundary

Confirm the console can only do what the broker interface declares — there is no second route to
a broker and no route at all to a store. Then confirm nothing destructive completes without being
shown first: a purge states what it will destroy and waits. [FR-023, FR-033, SC-009]

Confirm an agent cannot reach any of this: the console is a client of `Operators`, and nothing it
serves is bound into the server an agent's client launches. [FR-021, SC-007]

## Two checks that are not optional

**The console must not open the store.** Grep the project for a SQLite dependency. There should be
none — not a connection string, not a package reference. `Parley.Server` is the only thing that
opens the database, and a second reader is the corruption §4 exists to prevent.

**Message payloads must never be emitted raw.** Grep for raw-output helpers in the rendering path.
Razor encodes by default, so the review rule is simply that nothing opts out of it for a message
body.

## Running the tests

```
dotnet build
tests/Parley.Web.Tests/bin/Debug/net10.0/Parley.Web.Tests.exe
```

Run the executable directly. `dotnet test` reports `Zero tests ran` on at least one machine here
while the suite is fine — a toolchain that discovers nothing and a suite that contains nothing
produce the same summary, and the second reading is the one that gets acted on.
