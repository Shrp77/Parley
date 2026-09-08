# Feature Specification: The surfaces are their own tools

**Feature Branch**: `007-split-terminal-tool`
**Created**: 2026-09-08
**Status**: Draft
**Input**: User description: "Split the terminal operator surface (`parley top`) out of the `parley` tool into its own tool package, so that changing the terminal does not force the broker to restart." Extended by the owner: split the web console out as well — "for a UI-less Linux install, the Web UI component is meaningless."

## Why this exists

Changing the thing an operator looks at destroys the work of every agent on the machine.

One executable carries the broker, the node, the web console, the terminal and every one-shot
operator command. An executable cannot be replaced while it is running, so installing a change to
the terminal requires stopping the broker — and SPEC.md §12 defines what stopping it costs:

> *Three things end a resident agent, and nothing else may: 1. **The node stops**, and tells every
> agent it started to quit.*

The process is the memory. §12 again: *"The process holds everything the agent has learned."* So a
change to a status line ends nineteen conversations.

**The churn is concentrated exactly where the cost is.** Of the source files changed on `main`
since `c85a271`: **29 in the terminal**, 1 in the broker, 2 in the server, 2 in the store, 0 in the
agent adapter. The most-changed component in the repository is the one whose change is most
expensive.

**And in-flight work is at risk, not merely interrupted.** A question already drained into an
agent's inbox is marked delivered, so it sits in §4.2's four-hour Working window rather than the
fifteen-minute Delivery one. Ending the agent that holds it leaves the exchange open and owed with
nothing running to answer it, closing `ghosted` — which §5.3 defines as *"nobody replied"* —
recorded against an agent that was killed rather than one that ignored anything.

## The decision this reverses

`CLAUDE.md` records the opposite call, made deliberately, for the web console:

> *a second tool would have kept ASP.NET Core off the machines that only run an agent, and one
> command is worth more than that saving.*

That is reversed here, and the reasoning under it is recorded as no longer holding:

1. **The scope has outgrown it.** What one console application is responsible for now exceeds what
   one console application should be responsible for. The decision was right for the surface that
   existed when it was made.
2. **The version-drift objection does not apply.** Everything is one solution, built together on
   every change. Two packages cut from one build cannot drift.
3. **The dependency chain does not shift.** The terminal is downstream of the protocol and the
   server; nothing upstream depends on it. Splitting it does not create a boundary — it makes the
   packaging match a graph that already points that way.

The saving named in the original decision is unchanged and still real: a machine that only runs an
agent need not carry the surfaces. This feature collects that saving rather than spending it.

## Three tools, not two

The console goes out with the terminal, and the argument is sharper than the terminal's.

**On a machine that only hosts agents, the console is not merely unused — it is unusable.** §16
confines the transport to a trusted path, and the console listens on loopback; a headless Linux
agent host has nobody at that loopback and no browser on it. It is the heaviest dependency in the
tree — the whole ASP.NET Core stack — carried by the machines least able to use it. That is the
exact saving the original decision named and declined to take.

**And it makes an existing safety property structural rather than careful.** `CLAUDE.md` records
the console being hosted in the broker's process, and the containment that pays for it: *"a console
that cannot bind reports it and the broker keeps serving, because a web page must never cost a
deployment."* Separate processes make that true by construction; there is no longer a way for a
web page to cost a deployment, and no code that has to remember not to let it.

**What it costs, and must not lose.** `serve --with-console` hosts the console in the broker's
process, and `autostart install` turns it on by default. The default exists for SPEC.md §17
question 15: an agent attached through somebody's editor window is the one thing nothing can hand
a turn to, so a console *remembered separately* is the half of a deployment that works for agents
and not for people. Moving the console out must not quietly turn that default off — see FR-018.

## What the split already exposes

**The terminal reaches SQLite today, and nothing could have told us.** `Parley.Cli/Top` uses
`NodeCatalogue`, which lives in `Parley.Headless`, which references `Parley.Store`.

Three members are used: `NodeCatalogue`, `NodeCatalogue.Empty`, `NodeCatalogue.TryReadFrom`.

`GraphTests` walks the project graph transitively from `Parley.Client` and `Parley.Web` and fails
when either can reach a store. The terminal is not walked, because it sits inside `Parley.Cli`,
which legitimately owns the store — so the terminal's store-freedom has never been a checkable
property, only an intended one. Splitting it both exposes this and makes it enforceable.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Update the terminal without ending a conversation (Priority: P1)

An operator has agents mid-exchange. A change to the terminal is ready. They install it, and the
broker, the node and every resident agent carry on untouched.

**Why this priority**: This is the whole feature. Every other story is a consequence of it.

**Independent Test**: Start a conversation and leave an exchange open and delivered. Install a
terminal-only change. The broker's uptime is unbroken, every resident agent is the same process it
was, and the exchange is still open, still owed, and not ghosted.

**Acceptance Scenarios**

1. **Given** a broker running with resident agents and an open delivered exchange, **When** a
   terminal-only change is installed, **Then** the broker process is not restarted, no agent is
   told to quit, and no exchange changes state.
2. **Given** the same, **When** the installed terminal is then started, **Then** it shows the same
   deployment it did before, including the open exchange.

---

### User Story 2 - Install a broker change and be told what it costs (Priority: P2)

An operator installs a change that does touch the broker. The restart is unavoidable, so the
install says so before doing it, and says what is currently in flight.

**Why this priority**: The split removes the cost from most changes; it cannot remove it from
broker changes. What it can do is stop that cost being paid by surprise, which is what happens
today.

**Independent Test**: With an open delivered exchange, install a broker change and confirm the
operator is told, before anything stops, that the restart will end resident agents and that an
exchange is open.

**Acceptance Scenarios**

1. **Given** a broker change and an open exchange, **When** the install runs, **Then** it names the
   consequence and the open exchange before stopping anything.
2. **Given** a broker change and nothing in flight, **When** the install runs, **Then** it proceeds
   without ceremony.

---

### User Story 3 - Run a machine that only hosts agents (Priority: P3)

A headless Linux machine hosts agents and nobody operates it. It installs what a broker and a node
need, and carries neither surface — no terminal, and no web stack at all.

**Why this priority**: This is the saving the original decision named and chose not to take. It is
now available at no cost to the single-command experience on machines that do want a surface.

**Independent Test**: Install without the terminal on a machine, and confirm agents register,
receive turns and answer, with no terminal present.

**Acceptance Scenarios**

1. **Given** a machine with only the broker-side tool installed, **When** an agent is asked a
   question, **Then** it is started, answers, and the exchange concludes.
2. **Given** the same machine, **When** somebody looks for either surface, **Then** its absence is
   stated plainly rather than failing obscurely.
3. **Given** the same machine, **When** its installed files are inspected, **Then** no part of the
   web stack is present.

---

### Edge Cases

- **A terminal older or newer than the broker it points at.** §10.2 is a versioned contract, so a
  mismatch is possible for the first time. The terminal must say so in words naming both versions,
  the way an unreachable project already does, rather than failing at the first call that needs
  a field the other end does not have.
- **The terminal is not installed** when something tries to start it — the broker's startup block
  names it, and `parley top` may still be typed out of habit.
- **A catalogue path that the terminal can read but the node cannot**, once the reader has moved.
- **Installing while a terminal is open**, which is the mirror of today's adapter problem: the
  terminal holds its own executable.
- **An in-flight exchange during a broker install** — see User Story 2; the exchange must not be
  silently ghosted.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The terminal operator surface and the web console MUST each ship as a tool package
  separate from the one that contains the broker and the node, and separate from each other.
- **FR-002**: Installing a change confined to a surface MUST NOT stop, restart, or otherwise
  interrupt a running broker, node, or resident agent.
- **FR-003**: Both surfaces MUST remain clients: they open no store, and reach every fact about a
  deployment over the transport.
- **FR-004**: Each surface's package MUST have no route to a store, checked transitively across the
  project graph, under the same rule `Parley.Client` and `Parley.Web` are held to.
- **FR-005**: The catalogue reader the terminal needs MUST be available to it without a dependency
  on the store, and the node MUST continue to read the same catalogue by the same rules.
- **FR-006**: The agent-facing path MUST be untouched: the agent adapter's package, its contents,
  and the operations of §10.1 do not change.
- **FR-007**: The broker MUST remain the only component that opens the store.
- **FR-008**: An operator MUST be able to start the terminal by a documented command, and every
  place that names it — README, the broker's startup block, the plugin's prose, help text — MUST
  name the same thing.
- **FR-009**: The install path MUST handle the tools as a set rather than a hard-coded pair, so
  that adding one is data and not code.
- **FR-010**: The install path MUST report which tools it changed, so that "nothing was replaced"
  is distinguishable from "the terminal was replaced and the broker was not".
- **FR-011**: When the terminal and the broker disagree about the §10.2 contract version, the
  terminal MUST say so, naming both versions, and MUST NOT present a partial or wrong view as a
  complete one.
- **FR-012**: Installing a change that requires stopping the broker MUST say so before stopping it,
  and MUST name any exchange that is open at that moment.
- **FR-013**: A machine MUST be able to install the broker-side tools without the terminal, and the
  absence MUST be stated plainly if the terminal is then invoked.
- **FR-014**: Uninstalling MUST remove every tool it installed, leaving no scheduled task pointing
  at a path that no longer exists.
- **FR-015**: The tool containing the broker MUST NOT carry the web stack. A machine that installs
  only the broker-side tool MUST NOT acquire ASP.NET Core or its dependencies.
- **FR-016**: A machine MUST be able to install any combination of the surfaces alongside the
  broker, including neither, without the absent ones affecting the broker's behaviour.
- **FR-017**: A surface that cannot start — a port already held, a missing dependency, a
  terminal that will not draw — MUST NOT stop, delay, or degrade the broker.
- **FR-018**: On a machine where the console is installed, the console MUST start by default and
  start again at logon, preserving the behaviour `autostart install` gives today and the reason
  §17 question 15 gives for it.
- **FR-019**: The way to start the console MUST be resolved for anything that names the current
  in-process form, including the flag that requests it, the broker's startup block, `autostart`,
  the README and the plugin's prose. Nothing may name a way that no longer works.
- **FR-020**: Each surface MUST be startable and stoppable without reference to the other; neither
  is a prerequisite for the other.

### Key Entities

- **Tool package**: a separately installable executable cut from this solution. Today: the
  broker-and-commands tool and the agent adapter. After this: those two, the terminal, and the
  console — four, of which a machine installs the ones it needs.
- **Catalogue reader**: the component that reads a machine's node catalogue. Needed by the node
  (which starts agents from it) and by the terminal (which shows what a machine could start).
  Currently reachable only through a project that carries the store.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A terminal-only change is installed with **zero** broker restarts, **zero** agents
  ended, and **zero** exchanges changing state.
- **SC-002**: Of the changes made to this repository over the previous release cycle, the
  proportion that would have required a broker restart falls from **100%** to under **20%**, judged
  by replaying which packages each change touched.
- **SC-003**: An operator interrupting a delivered exchange by installing is warned before it
  happens, in **100%** of cases where an exchange is open.
- **SC-004**: The terminal cannot reach a store, and this is established by a check that fails when
  it can, not by inspection.
- **SC-005**: A machine hosting only agents installs and runs with neither surface present, and
  carries no part of the web stack.
- **SC-008**: A console that cannot start leaves the broker serving, demonstrated by starting a
  broker with the console's port already held and confirming agents are unaffected.
- **SC-009**: On a machine where the console is installed, it is reachable after a logout and
  logon without anybody starting it by hand.
- **SC-006**: Everything that names how to start the terminal names the same command, verified
  mechanically rather than by reading.
- **SC-007**: A terminal pointed at a broker speaking a different §10.2 version reports that fact
  naming both versions, and shows nothing it cannot vouch for.

## Assumptions

- **The one-command experience is preserved for operators who want it.** Today everything is
  `parley <something>`: one name on PATH, one thing to install, one help text. With three tools an
  operator could have to know that two of those subcommands are separate executables. How that is
  softened — the broker-side tool forwarding to a surface when it is installed, an alias, or simply
  documenting it — is a design decision for the plan. The requirement is FR-008: whatever it is,
  everything names the same thing.
- **The console starts by default where it is installed.** `autostart install` adds
  `--with-console` today, for the reason §17 question 15 gives, and that reason is unaffected by
  which process hosts it. How the default survives the move is a plan decision; that it survives
  is FR-018.
- **Version matching between terminal and broker is expected to be the common case**, because both
  come from one solution built together and are installed by one path. FR-011 covers the case
  where somebody has done otherwise, which is now possible for the first time.
- **The catalogue file format does not change.** Where its reader lives is an implementation
  question; what it reads is not in scope.
- The number of resident agents on the owner's machine (**21 registered in one project**) is the
  scale this is judged at.

## Out of scope

- Any change to the protocol, the wire contract, or the schema.
- Any change to the agent adapter or to how agents are launched, invoked, or ended.
- Removing the broker restart for changes that genuinely touch the broker. That cost is inherent;
  this feature confines it and makes it visible, and does not pretend to remove it.
- Hot-reloading, in-place upgrade, or draining a broker without stopping it.
- Any change to what either surface *shows*. This moves where they live, not what they do.
- Exposing the console beyond loopback. §16 is unchanged, and a headless machine wanting a console
  still needs a tunnel it authenticates itself.
