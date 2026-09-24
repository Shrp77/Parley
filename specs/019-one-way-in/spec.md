# Feature Specification: One place, and one way in

**Feature Branch**: `015-file-store` (this repository builds features on the working branch; see Assumptions)
**Created**: 2026-09-22
**Status**: Draft
**Input**: User description: "One place for a deployment's files, and a project is named rather than pathed." — extended the same day, after reading the command table, to "the commands need to be overhauled properly".

## The friction this starts from

Two complaints, taken together because they have one cause.

**Paths, one flag at a time.** `parley project add <name>` requires `--db <path>` and `--listen <url>`, and
defaults neither. A person naming a project must invent a filesystem location and pick a free port, then
remember both. What that produces is a layout nobody designed: on the machine that prompted this, the
project manifest, the machine catalogue and the broker log sit together under the platform's per-user
configuration directory, while the two stores sit in an unrelated folder on another part of the disk,
because that is what was typed the day each project was named.

**A command surface nobody can use.** Reading the commands out as a table made the second complaint
obvious. Roughly seventeen of them act on a conversation or an agent by identifier, and **there is no
command that lists conversations** — so every one of them takes an argument the person has no way to
obtain. Several more offer options that write values nothing reads. The tool has grown two ways to serve,
two ways to say where a store is, and a three-step enrolment whose middle step plans a change to a file
the person was asked to author.

**The cause is the same.** Parley has two things a person does — *set up a machine* and *take part in a
deployment* — and the command line has been asked to do both. The second is what the terminal and the web
UI are for, and they do it far better, because taking part needs a list in front of you.

**So this feature does two things and they are one thing.** Files get one place, and the way in gets one
door per job: the command line brings a machine up and is what a program calls; the terminal and the web
UI are where a person reads and acts.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Name a project and nothing else (Priority: P1)

An operator wants a deployment. They type its name. The tool decides where its files go and which port it
listens on, creates them, and says what it chose. They never type a path and never choose a port.

**Why this priority**: It is the first thing anyone does on a new machine, and everything else here
assumes it. It is independently useful with nothing else built.

**Independent Test**: On a machine with an empty root, add a project by name, start the broker, connect.
Nothing but the name is typed at any point.

**Acceptance Scenarios**:

1. **Given** a root holding no projects, **When** an operator adds one by name alone, **Then** it exists,
   has a store location and an endpoint, and the command reports both.
2. **Given** a project just added by name alone, **When** the broker starts, **Then** it serves that
   project at the endpoint the add reported.
3. **Given** a machine that already has projects, **When** another is added by name alone, **Then** its
   endpoint differs from every endpoint already spoken for on that machine.
4. **Given** two adds at the same moment, **When** both succeed, **Then** they hold different endpoints;
   and one that cannot be given an endpoint fails saying so rather than being given a taken one.

---

### User Story 2 - Everything Parley keeps is inside a project (Priority: P1)

An operator cannot point Parley at a store or a configuration outside a project, because there is nowhere
else for one to be. A store belongs to the project it serves; the machine's own two files belong to the
machine.

**Why this priority**: It is what makes US1's promise true rather than merely convenient. While a store
can live anywhere, "the name is the address" is a default somebody can quietly stop using, and the layout
goes back to being whatever was typed.

**Independent Test**: Try every documented way to name a store or a configuration outside a project, and
find that none exists. Then confirm a test rig can still isolate itself.

**Acceptance Scenarios**:

1. **Given** any command, **When** an operator tries to name a store outside a project, **Then** there is
   no option that does so.
2. **Given** a machine, **When** anything asks where a project's files are, **Then** the answer is derived
   from the project's name and the machine's root, and from nothing else.
3. **Given** a test rig or a second account that must not touch the real deployment, **When** it names a
   different root, **Then** it gets a wholly separate set of projects, and needs no path beyond that root.

---

### User Story 3 - Enrol a folder of repositories in one act (Priority: P1)

An operator points Parley at the folder their checkouts live in. It finds the repositories, configures
each one as an agent, starts them, and reports which ones answered. There is no file to author and no
separate step to apply.

**Why this priority**: It is the second thing anyone does, and the current three-step form is the clearest
case of the tool asking a person to do its job. The middle step plans changes to a file the person was
asked to write.

**Independent Test**: Point it at a folder of repositories on a machine with a running broker; confirm
each is configured and each appears in the registry afterwards.

**Acceptance Scenarios**:

1. **Given** a folder of checkouts, **When** an operator enrols it, **Then** every git repository under it
   is offered, and the ones chosen are configured as agents of the named project.
2. **Given** those repositories just configured, **When** the command finishes, **Then** each has been
   started and the ones that registered are reported as answerable.
3. **Given** a repository that was started and did not register within the time allowed, **When** the
   command reports, **Then** it says it does not know rather than that the repository failed.
4. **Given** a repository already enrolled in another project, **When** it is enrolled in this one,
   **Then** it gains this project's name and keeps the other, and its identity does not change.
5. **Given** a run an operator wants to inspect first, **When** they ask for it to be described rather than
   done, **Then** it reports what it would configure and changes nothing.

---

### User Story 4 - Parley serves everything, or it is not running (Priority: P2)

An operator starts Parley. Every project on the machine becomes available. There is no way to serve one
project here and another there, and no way to start something that serves only part of what the machine
has.

**Why this priority**: It removes a whole class of confusion — "it is running but that project is not
there" — and it is what makes `autostart` a decision nobody has to make. Below the first three because a
machine with one project cannot tell the difference.

**Independent Test**: Start Parley on a machine with three projects and reach all three. Then look for an
option that would have served fewer, and find none.

**Acceptance Scenarios**:

1. **Given** a machine with several projects, **When** Parley is started, **Then** every project is
   reachable.
2. **Given** Parley running, **When** an operator looks for a way to serve a subset, **Then** no such
   option exists.
3. **Given** a project whose store cannot be opened, **When** Parley starts, **Then** the others are served
   and that one is reported by name — one broken project does not cost the machine its deployment.
4. **Given** a machine being set up, **When** the tools are installed, **Then** starting at logon is
   arranged without being asked for, and an operator can ask what was arranged, and undo it.

---

### User Story 5 - The command line stops offering what it cannot do (Priority: P2)

A person looking for a conversation, an agent's mail, or anything they would act on is sent to a surface
that can show them a list. The command line keeps what a program calls and what brings a machine up.

**Why this priority**: It is the complaint that prompted this, and it removes about seventeen commands —
but it is below the layout work because those commands are already unusable, so removing them takes away
nothing anybody has.

**Independent Test**: Take each removed command, run it, and be told in one line where to do that instead.
Then confirm every scripted caller in this repository still works.

**Acceptance Scenarios**:

1. **Given** a command that acted on a conversation or an agent by identifier, **When** an operator runs
   it, **Then** they are told which surface does that, and nothing is done.
2. **Given** the installers and the editor plugin, **When** they run, **Then** every command they call
   still exists and still answers.
3. **Given** an operator reading the manual, **When** they look for how to take part in a deployment,
   **Then** it names the terminal and the web UI rather than listing commands.
4. **Given** a machine with no terminal available, **When** an operator needs to read a deployment,
   **Then** the manual names the way in that works without one.

---

### User Story 6 - An existing deployment moves without losing anything (Priority: P2)

An operator has projects whose files are where earlier versions made them type paths. They run one
command. Afterwards everything is in the standard layout, every conversation is still there, and the
deployment works.

**Why this priority**: Without it the feature is only for machines with nothing on them — but it is below
the layout itself, which has to exist before anything can move into it.

**Independent Test**: Take a machine with projects in the old arrangement, run the move, read back a
conversation that existed before it.

**Acceptance Scenarios**:

1. **Given** a deployment whose stores sit outside the root, **When** the move runs, **Then** every
   project's files are under that project's folder afterwards.
2. **Given** that deployment, **When** the move finishes, **Then** every conversation, agent, message and
   shared file that existed before is readable after.
3. **Given** a running broker, **When** the move is asked for, **Then** the operator is told first that it
   must stop the broker and what that costs, and it does not proceed until they accept.
4. **Given** a move that cannot complete, **When** it stops, **Then** it names the project it stopped on
   and leaves a deployment that still starts.
5. **Given** a move that stopped part way, **When** it is run again, **Then** it finishes the job.

---

### User Story 7 - A machine that has not been reinstalled still starts (Priority: P2)

An operator upgrades the tools and does not re-register the thing that starts the broker at logon. That
entry names the old configuration file and the old options. The machine comes up and serves everything.

**Why this priority**: About not breaking rather than about doing something new — but it must ship in the
same release, because the alternative is a machine that stops serving at its next logon with nothing on
screen to connect it to an upgrade.

**Independent Test**: Write an entry in the old spelling, upgrade, start the broker through it untouched.

**Acceptance Scenarios**:

1. **Given** a supervisor entry naming the old configuration file, **When** the broker starts from it,
   **Then** it serves every project the machine has.
2. **Given** an entry carrying options this version no longer offers, **When** the broker starts from it,
   **Then** it starts, and ignores them.
3. **Given** such an entry, **When** the operator asks what is installed, **Then** they are told it is
   written in a form this version no longer produces, and what to run to replace it.

---

### User Story 8 - A project named while Parley runs is served within seconds (Priority: P3)

An operator adds a project without stopping anything. It becomes available, and every agent already
attached to every other project stays attached.

**Why this priority**: Existing behaviour that this change could silently break. Third because somebody
who loses it can restart — but losing it quietly is how a deployment comes to be restarted for every
change, which costs every resident agent its context.

**Independent Test**: With Parley serving one project, add a second and wait. Confirm the first project's
agents never disconnected.

**Acceptance Scenarios**:

1. **Given** Parley running, **When** a project is added, **Then** it is served within seconds without a
   restart.
2. **Given** Parley serving two projects, **When** one is added or changed, **Then** agents attached to the
   other are not disconnected.
3. **Given** Parley running, **When** a project's folder is removed by hand, **Then** it stops being served
   and the rest go on being served.

---

### Edge Cases

**Ports**

- **Two adds at the same instant.** Neither may receive a port the other has. A port must be *claimed*
  rather than *found to be free*: between the finding and the recording, another add can find the same
  one. This repository already has a recorded intermittent failure of exactly that shape in its own test
  helper; making it part of the product would make it a defect operators meet.
- **The record of handed-out ports is missing, empty or unreadable.** It is bookkeeping, not the truth
  about what a project listens on — that is in the project's own file. Its absence must cost an add a
  reconciliation and never cost a deployment its endpoints. A bookkeeping file must not be able to hold a
  deployment hostage.
- **A port claimed for an add that then failed.** It must not be lost to the machine for ever.
- **A port recorded as free that something else on the machine has since bound.** The record says what
  Parley handed out; it cannot say what the operating system did. Starting is where that is found, and the
  existing refusal to start on a held port is what reports it.

**The layout**

- **A folder under the projects directory with no configuration in it.** Half-created, or somebody's
  scratch directory. Not a project; must not stop the others being read.
- **A configuration that cannot be parsed.** That project is unusable; the rest are not. Reported naming
  the project and the file, never as the machine having no projects.
- **A folder whose name is not a valid project name.** The folder name *is* the name, so it cannot be a
  project. Reported, skipped, left alone.
- **Two projects claiming one endpoint**, by hand-editing. Detected when the projects are read together
  and reported naming both; not silently resolved in favour of either.
- **A machine with no root at all**, on its first run. The root is created and nothing is reported broken.

**Serving**

- **One project's store cannot be opened.** Every other project is served. The machine does not lose its
  deployment to one bad file.
- **Every project's store cannot be opened.** Parley reports each and stays up, because a project named
  next will work and a process that exited could not serve it.

**Enrolling**

- **A repository that is already an agent of this project.** Reported as already enrolled; not duplicated,
  and its identity unchanged.
- **A repository that is a git worktree rather than a checkout of its own.** Its launch configuration is
  shared with the checkout it belongs to; must be said rather than silently written.
- **A repository started that does not register in time.** Reported as not known rather than as failed.
- **No broker running when enrolment starts.** Configuring needs none; starting does. What can be done is
  done, and what could not is said.

**Moving**

- **A destination that already holds a file.** Never overwritten. That is somebody's data.
- **A store held open by a running broker.** Cannot proceed; must name the process holding it rather than
  failing with a message about a file in use.
- **A project already in the standard place.** Left alone, reported as already correct.

**The command surface**

- **A script calling a removed command.** It must fail in a way a script can detect, and say what to do.
- **A person on a machine with no terminal.** The manual must name a way in that works, rather than
  assuming a TUI.

## Requirements *(mandatory)*

### Functional Requirements

**The layout**

- **FR-001**: There MUST be one root directory per machine holding everything Parley keeps there.
- **FR-002**: The machine's agent catalogue MUST sit at that root, because what this machine can start is
  a fact about the machine and not about any project.
- **FR-003**: Every project MUST be a folder under a single projects directory beneath the root, and the
  folder's name MUST be the project's name.
- **FR-004**: A project's configuration MUST live in a file inside that project's own folder, and MUST NOT
  restate the project's name — the folder is the name, so there is nothing for the two to disagree about.
- **FR-005**: The set of projects on a machine MUST be derived from the contents of the projects
  directory, and from no list kept elsewhere.
- **FR-006**: A project's store MUST live inside that project's folder.
- **FR-007**: There MUST be no way to name a store outside the project it belongs to, and no way to name a
  configuration file outside the root. **The only path an operator may give is a root**, which selects a
  whole separate set of projects.
- **FR-008**: The machine's two files — the catalogue and the record of handed-out ports — MUST be the
  only things at the root that are not a project.

**Handing out ports**

*Which ports are spoken for is a fact about the machine, not about any project. A project cares only about
its own port; deciding which port is next is the machine's business, so it is recorded once, at the root,
beside the machine catalogue — and not inferred by reading every project.*

- **FR-009**: A machine MUST keep a record, at its root, of which ports it has handed out.
- **FR-010**: An endpoint MUST be claimed by writing it into that record under an exclusive hold, so that
  two claims made at the same moment cannot be granted one port. **Observing a port to be free is not a
  claim.**
- **FR-011**: The hold on that record MUST cover only that record, MUST be released as soon as a port has
  been handed out, and MUST NOT be taken by anything on the path that serves a deployment.
- **FR-012**: A project's own configuration MUST record the endpoint it listens on, and that MUST be what
  is bound. The machine's record is what stops a port being handed out twice; it is not consulted in order
  to serve anything.
- **FR-013**: An endpoint in use by a project but absent from the machine's record MUST NOT be handed out
  again. A record that has lost entries MUST be reconciled from the projects rather than believed over
  them.
- **FR-014**: Removing a project MUST release its port.
- **FR-015**: When no port can be claimed, the add MUST fail saying so and MUST NOT leave a half-created
  project behind.

**Naming a project**

- **FR-016**: An operator MUST be able to create a project by supplying only its name.
- **FR-017**: Creating a project MUST report the endpoint and the store location it chose.
- **FR-018**: Creating a project MUST NOT require a running broker.
- **FR-019**: An operator MUST be able to give a description, and to change it later.
> **FR-020 and FR-021 are withdrawn — amended 2026-09-23, the owner's call.** They required an operator
> to be able to state an endpoint on the command line, including a non-loopback one, with the
> availability check binding the address named.
>
> - ~~**FR-020**: An operator MUST be able to state a project's endpoint explicitly.~~
> - ~~**FR-021**: A stated endpoint MUST be checked on the address it names.~~
>
> **The reasoning behind them survives and is worth keeping, because it is nearly right.** SPEC.md §16
> does permit a broker on a private network somebody controls, and this feature genuinely cannot know
> which interface a person means — so it never chooses one, and what it hands out is always loopback.
>
> What was wrong is the conclusion that the remedy belongs on a command line. `--listen` bought one more
> thing a person supplies and two rules about which source wins, on a feature whose whole purpose is that
> **a project is named rather than pathed**.
>
> The cost is recorded rather than hidden: **there is no command that makes a broker reachable off this
> machine.** That is a real reduction in what the tool does, taken deliberately.
>
> **Reopened on 2026-09-24 and closed the same way, on stronger grounds.** The owner asked for the
> capability back as `--private`/`--public` — two words rather than an address, so that nothing on a command
> line is a bind address — and for both operator surfaces to offer it. It was withdrawn again once the
> exposure was costed. **All three gRPC services share one port**: `ParleyServer` binds a single URL and maps
> `Agents`, `Operators` and `Nodes` onto it, so reaching the port is reaching the control plane —
> `Operators.Export` reads every conversation, `Operators.Start` launches a process, and
> `Operators.Instruct` hands work to an agent that executes code in a repository. With no credentials on the
> wire, a bind address is unauthenticated remote code execution by proxy, and no arrangement of flags
> changes that.
>
> **So the supported path is an authenticated tunnel**, which is what §16 already named, and the README
> gives the SSH recipe where somebody is about to need it. A hand-edited non-loopback endpoint still works,
> because §16 permits a private network the operator controls — and the broker now warns about one on every
> start, in the log, naming what reaching it grants.
>
> Three designs were costed and are recorded so the next person does not re-derive them: binding `0.0.0.0`
> (fail-open — a decision taken once on a trusted network follows the machine onto every network it later
> joins, with no event and no prompt); binding one pinned interface, re-resolved at start so a DHCP lease
> can move under it (fails closed, defensible, still exposes the control plane); and splitting `Operators`
> and `Nodes` onto a loopback-only bind while `Agents` is exposed — **the strongest of the three, and the
> one to revisit if this is ever wanted again**, since it bounds the worst case to agent impersonation
> within one project. None of the three is authentication, which is what §16 says the real fix is.

**Saying what listens where**

*Parley does not own firewall rules, tunnels or routes, and should not try to. What it owes the person
writing them is one legible, authoritative answer to "what listens where, and can anything outside this
machine reach it?"*

- **FR-022**: Where a project's endpoint is reported, it MUST be distinguishable whether that endpoint can
  be reached **from another machine** or only from this one.
- **FR-023**: A report MUST NOT describe a loopback endpoint with a word that reads as network
  reachability. *Today's listing prints `reachable` beside `http://127.0.0.1:51707`, meaning only that
  this process connected to it. Somebody planning a firewall rule reads that as reachable from the
  network — and no rule they write can make it so, because nothing off the machine can address
  `127.0.0.1`. The word is doing the opposite of its job.*
- **FR-024**: Where an endpoint is loopback, the report MUST say what to change to make it reachable, so
  that the answer is in the same place as the question.
- **FR-025**: A project's endpoint, its store and whether it is reachable MUST be readable in one place,
  without consulting a second source.

**Reading a machine's projects**

- **FR-026**: A folder without a readable project configuration MUST NOT be treated as a project and MUST
  NOT prevent others being read.
- **FR-027**: A folder whose name is not a valid project name MUST NOT be treated as a project, and MUST be
  reported.
- **FR-028**: Two projects claiming one endpoint MUST both be reported by name, and MUST NOT be silently
  resolved in favour of either.
- **FR-029**: What cannot be read MUST be reported in terms of the project and the file, never as the
  machine having no projects.

**Serving**

- **FR-030**: Starting Parley MUST serve every project the machine has.
- **FR-031**: There MUST be no option that serves a subset, and no option that serves a store by path.
- **FR-032**: A project whose store cannot be opened MUST be reported by name, and MUST NOT prevent the
  others being served.
- **FR-033**: A machine whose projects can none of them be opened, or which has no projects, MUST stay up
  and report why, so that a project named next is served without anything being restarted.
- **FR-034**: A project created while Parley runs MUST be served within seconds, without a restart.
- **FR-035**: A change to one project MUST NOT disconnect agents attached to another.
- **FR-036**: A project removed while Parley runs MUST stop being served, and the rest MUST go on.

**Starting at logon**

- **FR-037**: Installing the tools MUST arrange for Parley to start at logon without being asked.
- **FR-038**: An operator MUST be able to ask what was arranged, and to undo it.
- **FR-039**: There MUST be no options choosing what the logon entry serves or whether it starts a web UI.
  It serves everything and starts the surfaces, because that is what starting Parley means.

**Enrolling repositories**

- **FR-040**: An operator MUST be able to enrol a folder of repositories with **one verb and no
  subcommands**. There MUST be no file for a person to author, no step that plans a change to such a file,
  and no separate step that applies it. *The three-step form named its first step `scan`, which was honest
  while that step only looked; a verb that finds, configures and starts is not a scan, and is not called
  one.*
- **FR-041**: That command MUST offer the git repositories it finds and let an operator choose among them,
  and MUST support choosing all or none without a prompt, for scripts.
- **FR-042**: It MUST be able to describe what it would configure without configuring it.
- **FR-043**: Every repository it enrols MUST be catalogued, so that a peer's request can start it.
  **Enrolling a repository is the consent.** *This reverses a per-repository opt-in that defaulted to off.
  The opt-in existed because a catalogue entry is standing permission to start a checkout with nobody
  watching, spending the owner's tokens — but it produced, on a real machine, nineteen repositories that
  were enrolled, addressable, and unstartable. An agent that cannot be started cannot answer, and an
  enrolment that leaves one that way has not enrolled anything.*
- **FR-044**: The command MUST state, before it configures anything, that enrolling makes each repository
  startable by a peer's request, and MUST name how many it is about to start.
- **FR-045**: After configuring, it MUST start each repository so that each **registers itself**. Nothing
  may write a registration on a repository's behalf.
- **FR-046**: It MUST report, per repository, whether it registered — and MUST report a repository that did
  not register in time as *not known*, never as failed.
- **FR-047**: Enrolling a repository already enrolled elsewhere MUST add this project's name and leave its
  identity and its other projects alone.
- **FR-048**: Enrolment MUST carry forward a statement of what a repository owns where its launch
  configuration already holds one, and MUST NOT ask a person to compose one. *What a repository owns is
  something the repository can be asked, and a person may not remember; that is its own feature and is out
  of scope here. What is in scope is not losing the lines that exist.*

**The command surface**

- **FR-049**: The command line MUST keep the commands that bring a machine up and the commands a program
  calls, and MUST NOT offer commands whose arguments a person has no way to obtain.
- **FR-050**: Every command this repository's own installers and editor plugin call MUST go on existing and
  answering.
- **FR-051**: A removed command MUST, when run, name the surface that does that thing, and MUST fail in a
  way a script can detect.
- **FR-052**: No command may offer an option that writes a value nothing reads.
- **FR-053**: An operator operation MUST NOT be renamed to match a word an agent-facing surface uses for
  sending a message. The distinction between instructing an agent and speaking to one is what the naming
  convention of the protocol's operator section exists to protect.
- **FR-054**: The manual MUST direct a person who wants to take part in a deployment to the terminal or the
  web UI, and MUST name a way in that works on a machine with no terminal.

**Moving an existing deployment**

- **FR-055**: An operator MUST be able to move an existing deployment into the layout with one command.
- **FR-056**: That command MUST state, before changing anything, that it has to stop the broker and what
  stopping it costs, and MUST NOT proceed without acceptance.
- **FR-057**: The move MUST carry every file belonging to a store, not only its principal file.
- **FR-058**: The move MUST NOT overwrite anything at a destination.
- **FR-059**: A project already in the standard place MUST be left alone and reported as such.
- **FR-060**: A move that stops part way MUST name the project it stopped on, MUST leave a deployment that
  still starts, and MUST be safe to run again.
- **FR-061**: Everything readable before a move MUST be readable after it.

**Nothing to strand — amended 2026-09-23, the owner's call**

> **These three requirements are withdrawn.** They existed to keep an un-reinstalled machine serving
> across the change, and rested on a premise that is not true: **nothing has been released.** The only
> machine carrying the old layout is the one this was written on, and what it needs is a clean start
> rather than a compatibility rule that would outlive every machine it was for.
>
> A rule kept "for at least one release" on a product with no releases is a rule with no expiry, and this
> feature exists because a deployment was arrived at one accommodation at a time.
>
> - ~~**FR-062**: A configuration location given in the previous form MUST go on being understood.~~
>   **Withdrawn.** The old spelling is *refused*, naming the directory to point at instead — loud, and
>   at the one moment somebody can act on it. Silently resolving a path nothing writes any more is how a
>   rule survives past the reason for it.
> - ~~**FR-063**: A supervisor entry written in the previous form MUST go on starting a Parley.~~
>   **Withdrawn.** Such an entry names `--db` and `--listen`, which no longer exist; it is replaced by
>   re-running `parley autostart install`.
> - ~~**FR-064**: The command that reports what is installed MUST say a previous form is installed.~~
>   **Withdrawn with FR-063** — there is no previous form to report.
>
> **What replaces all three is `reset.ps1`**: it stops the broker, removes the autostart entry, reads the
> old manifest *before* deleting it so each store it named can be removed with its companions, and keeps
> `node.json` so nobody re-enrols a repository by hand.

**What must not change**

- **FR-065**: The machine catalogue MUST remain one per machine, keyed by agent identity.
- **FR-066**: Registration MUST remain the adapter's own act on attach. Nothing may write a registration
  row on a repository's behalf.
- **FR-067**: The domain vocabulary describing a project MUST remain free of filesystem rules and
  filesystem access, because a broker compares deployments belonging to machines it is not on.
- **FR-068**: A project's configuration MUST NOT carry a per-project agent catalogue.
- **FR-069**: There MUST be no change to how an agent addresses a message, and no change to the storage
  schema.

### Key Entities

- **Root**: The one directory on a machine holding everything Parley keeps there. The only path an
  operator may name.
- **Projects directory**: The single directory beneath the root whose contents *are* the machine's
  projects.
- **Project folder**: One per project, named by the project. Holds that project's configuration and its
  store.
- **Project configuration**: What a project is apart from its name: where it listens, and an optional
  description.
- **Port record**: At the root, beside the catalogue. Which ports this machine has handed out, and nothing
  else. Belongs to the machine, not to any project. Held exclusively only while a port is handed out, and
  never read in order to serve anything.
- **Machine catalogue**: Unchanged. What this machine may start, keyed by agent identity, at the root.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator brings up a new deployment by typing one name, and types no filesystem path and
  no port number at any point.
- **SC-002**: An operator makes a folder of repositories into answering agents with one command, and
  authors no file.
- **SC-003**: There is no way, through any command or variable, to make Parley open a store outside a
  project or a configuration outside a root.
- **SC-004**: Starting Parley on a machine with several projects makes every one of them reachable, and no
  option exists that would have made fewer reachable.
- **SC-005**: Every command that survives is either called by a program in this repository or brings a
  machine up. No surviving command takes an identifier a person cannot obtain from the same tool.
- **SC-006**: One command puts an existing deployment's files into the layout, and every conversation
  readable before is readable after.
- **SC-007**: A machine upgraded without re-registering its startup entry still serves every project at its
  next start.
- **SC-008**: Two adds issued simultaneously never produce two projects sharing one endpoint, measured by
  repeating the race enough times that a one-in-many failure would be seen.
- **SC-009**: Deleting the machine's port record costs nothing but a rebuild: every project keeps its
  endpoint and the next add still receives a port nobody is using.
- **SC-010**: A project added while Parley runs is reachable within ten seconds, and no agent attached to
  any other project loses its connection.

## Assumptions

- **The root is where the project manifest lives today** — the platform's per-user configuration
  directory. This feature moves what is under it, not where it is.
- **The configuration variable and flag name the root, and only the root.** An earlier draft had the
  previous spelling understood as naming the root that file sits in; that was withdrawn with FR-062 when
  the owner settled that nothing is released and there is nothing to strand.
- **Naming a root is how a test rig isolates itself**, and it is what makes FR-007 affordable. Today
  fourteen test files start a broker by store path, through one shared helper — removing that option is a
  real cost, and it is payable only because creating a project in a throwaway root now costs one name and
  no paths. The plan must carry that conversion; it is not incidental.
- **A handed-out endpoint is loopback; an endpoint is not always loopback.** SPEC.md §16 confines the
  transport to *"a loopback connection, a private network somebody controls, or a tunnel authenticated
  outside this protocol"* — so a project reachable from another machine on a trusted network is a
  supported deployment, not an edge case. What this feature hands out automatically is a **loopback**
  endpoint, because that is the only kind it can choose safely without knowing a person's network. An
  operator who wants any other kind states it (FR-026), and stating it bypasses allocation.
- **The range ports are handed out from is the machine's to decide**, and the existing deployment's ports
  are within it. The range, and what happens when it is exhausted, are for the plan.
- **"Everything" means every project on this machine.** Parley has never served another machine's projects
  and this does not change that.
- **Removed commands are removed rather than hidden.** A stub that names the right surface is kept for at
  least one release, because a person whose script breaks deserves to be told where the thing went.
- **The operator operations do not leave the protocol.** They remain available on the terminal and the web
  UI, which already bind every one of them; what is removed is their command-line spelling.
- **An operator running the move has the deployment to themselves** for its duration.
- **The version moves to 0.32.0.** The layout and the command surface are both breaking, and the primary
  document's illustration of a project manifest moves with them.
