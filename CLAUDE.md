# Parley

Cross-session agent-to-agent messaging with a durable store and a registry. **`SPEC.md` is
the primary artifact of this repository; the code is a reference implementation of it.** If
code and spec disagree, that is a defect in one of them, and which one is a decision, not an
assumption — do not silently change code to match spec or spec to match code.

Read `SPEC.md` before changing protocol behaviour. Open design questions live in `SPEC.md`
§17. Design history lives in `specs/*/`, `SPEC.md` §18 and git, not in this file.

## State

- Version 0.52.0, protocol 0.12, schema 6. Nothing has been released, but **live stores hold work
  in flight and are kept**: schema 1 is fixed (`SchemaOneIsFrozenTests` pins what
  `001_initial.sql` creates), and every schema change is a new migration, `002_…` onwards, that
  upgrades a store in place and keeps its rows. A new build is installed with `install.ps1 -Force
  -Hidden`, never `-Reset`. `-Force` ends the broker and every `parley-mcp`, so every tool on a
  machine is one build: **the wire, enums and protocol still change freely**, with no
  compatibility caveats.
- Features 019–026 and 028–032 are merged; the latest is `specs/032-sub-conversations-and-moves/`.
  Feature 027, session-owned delivery, is not committed yet; its plan predates the rule above and must move its schema
  change into migration 007 (003 holds file problems, 004 when an agent was first asked what it owns and first refreshed,
  005 a message's limit and the record of its length, 006 conversation links, moves and move notices).
- A person speaks to an agent only with a Request. An agent's row menu in the terminal has one
  ownership entry, *What this agent is responsible for* (`Alt+n`).
- Design work runs through Spec Kit: `/speckit-specify`, `/speckit-plan`, `/speckit-tasks`
  write `specs/<n>-<slug>/`. Read a feature's plan before implementing it. The constitution in
  `.specify/memory/constitution.md` restates the standing constraints below; this file wins
  where they differ.

## Layout

```
src/Parley.Core          Domain vocabulary and the broker contracts. No I/O, no package
                         dependencies — a `using` that pulls I/O in is a review finding.
                         Holds IParleyBrokerClient (SPEC §10.2, what a broker owes an
                         operator). WorkspacePath is string comparison on
                         purpose: the broker compares workspaces from machines it is not on.
src/Parley.Store         SQLite via Dapper: ParleyDatabase, SchemaMigrator, WriteScope and
                         the repositories.
src/Parley.Broker        Orchestration. Broker holds the agent operations (§10.1);
                         OperatorConsole holds the operator operations (§10.2). Knows
                         nothing of transports or adapters.
src/Parley.Transport     gRPC contract generated from proto/parley.proto. No hand-written code.
src/Parley.Server        The broker process (§4): the only thing that opens the database.
src/Parley.Configuration The project manifest (§4.1) and the node catalogue: where they live
                         and what they hold. Read by both Parley.Cli and Parley.Mcp. Written
                         only through Parley.Setup's ProjectCommand. Also where a path may
                         be looked up: OffMachineLinks (no link off this machine) and
                         LocalStorage (only this machine's own disks), and where a JSON
                         file somebody else wrote is parsed: StrictJson, which refuses a
                         member named twice and text that is not valid Unicode.
src/Parley.Setup         What a person does to a machine: enrolling a repository, naming
                         projects, resolving per-machine paths, the autostart entry. Shared
                         by `parley` and `parley-terminal`, which run the same commands in
                         process. A broker client (Transport), never a store.
src/Parley.Client        What a client needs to show a deployment and seat a person: views,
                         transcript and visibility derivations, seating, SeatVoice (drains
                         and records delivery in one act). References Transport and
                         Configuration only.
src/Parley.Terminal      `parley-terminal`, the terminal surface, on Terminal.Gui v2. Its own
                         tool because Windows cannot replace a running executable and the
                         terminal changes far more often than the broker. `parley terminal`
                         forwards the raw command line to it. Its BuildIdentity is its own.
src/Parley.Web           The operator web console (§10.2), a client of the broker. Razor
                         Pages. ConsoleHost is the single place the host is built and names
                         its own assembly as an application part.
src/Parley.WebUi         `parley-web-ui`: only a way to start ConsoleHost. `serve
                         --with-web-ui` starts it as a child process, does not wait on it or
                         restart it, and ends it with the broker. A web UI that cannot start
                         leaves the broker serving and says so.
src/Parley.Cli           `parley`: the broker's tool and the command line.
src/Parley.Headless      The node: starts and supervises agents (ResidentAgent, InvocationCommand).
src/Parley.Mcp           `parley-mcp`, the agent's adapter.
src/FilePlacement.cs     Source files linked into several projects (Parley.Client, Parley.Mcp
src/InboxCollection.cs   and tests), not a project: the transport holds no hand-written code.
tests/                   One project per source project; notes below.
plugins/parley           The Claude Code integration. .claude-plugin/marketplace.json is
                         its catalogue.
```

**Dependency rules, each enforced by a graph walk (`GraphTests`) or a source scan:**

- No route to SQLite from Parley.Client, Parley.Web, Parley.Terminal or Parley.Setup, walked
  transitively. Parley.Core has no dependencies at all.
- Parley.Configuration reaches neither a store nor the transport.
- Parley.Mcp references neither Parley.Broker nor Parley.Setup.
- `Parley.Core.Tests` does not reference Parley.Store. `Parley.Spec.Tests` references neither
  Core nor Store. `Parley.Client.Tests` does not reference Microsoft.AspNetCore.Mvc.Testing.
  The absent dependency is the point in each.

**Agent operations and operator operations live on different types.** The naming convention
in §10.2 (`force_conclude` is not `conclude`) is too thin alone.

- In process, an adapter is handed a `Broker`, which has no operator operation.
  `SurfaceBoundaryTests` enumerates its public surface and fails when it grows.
- On the wire they are separate gRPC services: adapters bind `Agents`; `Operators` and `Nodes`
  are others. `AgentsCannotSetTheirOwnLineTests` matches by word, not exact name, so an
  operation added under a new name is still caught.
- `Agents` replies never carry turn information. `AgentsCannotSeeTurnsTests` holds it.

**Surfaces.** Every operator operation is available on the CLI, the terminal and the web UI,
and `DriftTests` asserts both screens bind every `BrokerClient` operation. Everything a person
does to a deployment is the terminal's, except that a project is also created in the web console;
a project is removed or renamed only in the terminal. Renaming is two steps, removing it and naming the new one, which
leave the conversations in the old store and every enrolled repository naming the old project until it is enrolled again.
The command line keeps `serve`, `up`, `autostart`,
`use`, `project list`, `enroll`, `registry`, `open`, `waiting`, `files`, `web-ui` and `terminal`
(the last two start their own tools); every other operator verb exits 64 and names the terminal.

- Destructive acts need a description plus a deliberate act, usually a typed name. `Retire`,
  `Reinstate` and `Purge` appear in exactly one file, which contains the typed-name prompt;
  the `destroy` argument in one other; `ForceConclude` and `Prune` each in one
  file carrying its door. `SurfaceTests` enforces it, and a helper whose name merely contains
  one of those words fails it.
- `EveryActionHasADoorTests` fails when an action is performed and unlinked, linked and not
  performed, or done on a POST and described on no GET.
- The web UI answers only loopback host names and the host it binds (DNS rebinding defence).
- No page can be framed, except the conversation page the console's own roadmap page frames (`embed` and
  `Sec-Fetch-Dest: iframe`, same origin only; `ConsoleHost.Embedded`). Embedding hides the project's name, so no
  other page embeds.
- A POST-only `[BindProperty]` is `[FromForm]`, and an `OnPost` parameter names its source (`[FromQuery]` only on
  purpose, as Files' Open). Forms post back to their own page and an agent's message renders as live links, so a
  field a form left out would otherwise come from the link. `FormBindingTests` holds it.
- A repository may be enrolled in several projects. Its launch key in `.mcp.json` is `parley`
  (always read) or `parley-<project>`; `LaunchKey` in Parley.Configuration is the one
  implementation. An agent's identity is `parleyAgentId` in `.mcp.json`, and the per-machine
  catalogue (`node.json`) is keyed by it. The name is only what is shown: the adapter reports the
  identity on attach as a label, never authority, and an attach under a new name from the same
  repository on the same machine renames the agent (§4.1.1).
- No surface writes a launch configuration for an agent whose machine is not this one.

**Seats.** The broker provides two participants in every project, neither with a machine:
`human-user@<project>` (the person; never retired, purged or released) and
`parley-facilitator@<project>`.

- The role `human-user` and the adapter `parley-console` are tied in both directions: `Attach`
  refuses either without the other, enrolment refuses the role, and storage refuses the pair
  apart. Surfaces act as the person with no join and no typed name. No operator operation acts
  on the seat.
- Presence is an open connection (§4.0), never a flag a surface keeps. A surface drops its
  stream after 20 idle minutes; the seat itself persists. A message to an away person is
  answered `WITH_THE_PERSON`.
- An agent a node starts carries `PARLEY_LAUNCH_TOKEN`. One attached without it is *open
  outside Parley*: it cannot be woken or started (§11.7). The token is not a credential.

**Test project notes.**

- `Parley.Server.Tests`: whole exchanges over a loopback socket; may not open a
  `SqliteConnection` or touch a `Broker`.
- `Parley.Terminal.Tests`: `TerminalProcess` must run the tool out of its own build
  directory. A copy beside the tests dies with a TypeInitializationException because the
  project also references Parley.Cli. `Loopback`, `LoopbackOperator`, `ToolProcess` and
  `TestMachine` are linked from Parley.Cli.Tests, not copied.
- `Parley.WebUi.Tests` exists because Razor Pages are found by walking the entry assembly, so
  the failure only shows when the entry assembly is not Parley.Web.
- Tests that start a tool as a child clear every `PARLEY_` variable and run from a directory
  in no repository. Paths such as the catalogue are passed in as seams, never read from the
  environment at the bottom of a call stack; a test that inherits the developer's
  `PARLEY_NODE_CONFIG` writes into the live catalogue.

## Build and test

```
dotnet build
pwsh scripts/test.ps1                 # unit tests, seconds
pwsh scripts/test.ps1 -Category Smoke # all but [Slow], under 5 minutes
pwsh scripts/test.ps1 -Category All  # everything, ~25 minutes
```

Every test class is `[Unit]` or `[Integration]` (`tests/TestCategories.cs`), and `[Slow]` leaves an integration test out of the
smoke run. Pushes run the unit tests; a pull request must pass the `smoke` workflow; the `nightly` workflow runs everything against `main`. Every run is
recorded in the git-ignored `.test-metrics/`.

Tests run on Microsoft.Testing.Platform, selected in `global.json` under `"test"` beside
`"sdk"`. A green build is the whole lint gate: warnings are errors, documentation is
enforced, and NuGetAudit fails on high or critical advisories. CI runs Windows, Linux and
macOS.

## Versioning

**Bump `VersionPrefix` in `Directory.Build.props` in the same change that alters shipped
behaviour** — in the commit, not at release. Semantic versioning; while the major is zero the
minor moves on a breaking change and the patch on everything else. It applies to a change in
`src`; documentation, tests, CI and the plugin's prose do not move it.

`dotnet tool update` compares versions, so a rebuild at an installed version is silently not
installed. Nothing enforces this convention.

## Conventions the build enforces

| Rule | Mechanism |
|---|---|
| Every public member documented | `GenerateDocumentationFile` + `TreatWarningsAsErrors` (CS1591) |
| No culture-sensitive string comparison | CA1305/CA1307/CA1310 as error in `.editorconfig` |
| `ConfigureAwait(false)` in library code | CA2007 as error |
| Parameterised SQL | CA2100 as error. One suppression, in `SchemaMigrator.Migrate`, with a written justification; do not add a second without one |
| Package versions centralised | CPM; a `Version=` in a csproj is a restore error |
| High/critical advisories fatal | `NuGetAudit`, NU1903/NU1904 as errors; NU1901/NU1902/NU1905 stay warnings |

Low and moderate advisories are non-fatal on purpose: if every severity blocked, someone
would switch auditing off.

**Documentation style:** `<remarks>` carries *why*, never a restatement of the signature.

**Transitive pins:** every entry in the `Transitive pins` group of `Directory.Packages.props`
records why it exists and when it can be removed, in the format documented there. Remove a pin
once the parent's own floor catches up (`dotnet nuget why`).

## Environment gotchas

- A double hyphen cannot appear inside an XML comment. A CLI flag in a `.props`/`.csproj`
  comment makes MSBuild reject the file without saying why.
- Test runner selection lives in `global.json`. MTP is all-or-nothing: every test project
  must be MTP-based.
- xUnit v3 test projects need `<OutputType>Exe</OutputType>`, or they build and discover zero
  tests.
- `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` must stay absent; the .NET 10 SDK
  refuses to run MTP tests through VSTest.
- IDE0005 only runs when `GenerateDocumentationFile` is true, so test projects generate docs
  and suppress CS1591.
- xUnit1051: pass `TestContext.Current.CancellationToken` to every awaited call that accepts one.
- `.slnx` solution folders need a real name, not `/`.
- Package deprecation produces no build warning; the CI `dependency health` job exists for it.
- **`dotnet test` can report `Zero tests ran` on a machine where the tests are fine** (exit
  code 5, `total: 0`). Seen on one Windows machine; cause unknown. Run
  `tests/Parley.*.Tests/bin/*/net10.0/Parley.*.Tests.exe` directly before concluding anything.
  A count is evidence only about the binary that produced it, so check the run included the
  new files.
- Never `git checkout` over uncommitted work. Copy first, restore from the copy.
- A mutation that does not compile proves nothing, because the tests run the previous binary.
  Mutate with something the compiler accepts, and watch each new guard fail first.
- SPEC.md is CRLF on Windows and LF on Linux. A raw ` ` in a C# literal breaks the
  build; use `(char)0x2028`.
- Windows and Linux differ on sockets, keep-alives and shutdown. When CI is unavailable,
  verify Linux in a container and loop a flaky host many times under load.

## Review gate

A pull request merges after three clean runs of each of three lenses (correctness and concurrency, security and data
integrity, API surface and maintainability) on the same shipped code, with the smoke run (the `smoke` workflow) green on the commit that merges; a commit
that changes only tests, CI, docs or plugin prose keeps the counts. The rules live in
[`review/`](review/README.md), one file for how the gate runs and one per lens, each with what counts as medium, how to
probe, and what the owner has settled. Read the gate's file and the lens's file before a pass; a lens file, not this
one, is where a new review rule goes.

## Answering as an agent

A working copy of this repository can register itself with Parley like any other (a
gitignored `.mcp.json`). When a question arrives asking how Parley behaves, **answer from
`SPEC.md` and quote the section**. If code and spec disagree, say so in the Result rather than
picking the convenient half. If the spec does not settle it, report the item `blocked` with
`missing_information` and say what is unsettled. Never add a committed Peers block to this
file.

## Surveying the peers

SPEC.md §17 question 1 leaves request volume unbounded and decides it from evidence. Only a
receiving agent can say whether requests were worth the work, so it has to be asked.

**The person runs the reports; this agent does the asking.** The reports read across every
pair, which §10.1 withholds from an agent, so this agent must not run `parley results`,
`parley flags` or any other operator command. It is told which peers are due and sends each a
plain single request through Parley (rule 8.1):

> Of the requests you have received through Parley, roughly how many were worth the work, and
> what made the others not? I am asking because SPEC.md §17 question 1 leaves request volume
> unbounded, and whether that should change can only be decided from what receivers say.

Record what comes back in §17 question 1 and do not act on it; one opinion is not a protocol
change.

## Repository hygiene

This repository is public and its history goes with it. No trace of the originating private
project may appear in any commit, in code, comments, tests or fixtures. Placeholder agents are
`orders`, `billing` and `inventory`.

Before committing, run a case-insensitive `git grep` for the originating project's names,
excluding `.gitignore`, and also check brand-new untracked files (`git grep` sees tracked
files only). The term list is the names of the sibling repositories on disk; it is never
written in a tracked file. Two standing false positives are recorded in `REVIEW-LEDGER.md`.

Squash-merge only, so the **PR title becomes the commit message**. Conventional Commits.
Design rationale goes in the PR body.

## Standing constraints

These are the premises. A change that contradicts one is a design change, not an
implementation detail, and needs to be argued.

- **The protocol is the product; the transport is disposable.** If a platform ships native
  cross-session messaging, Parley should run over it. Keep the transport behind an interface.
- **Terseness is a security control.** A message's hard limit (512,000 characters) is a
  context-poisoning defence and lives in the schema so no code path can route around it;
  changing it is a table rebuild. Past the soft limit (16,000) a message is delivered and its
  sender told. The numbers are the owner's decision and a recorded reduction in the control;
  every message's length is recorded so they can be judged (SPEC.md §17 question 16).
  - **Exception: `agents.owns` has no bound by default**. The constraint
    stays in storage and stands down at its top, so a deployment may reinstate one. The
    character class that stops a roster entry breaking, hiding or reordering its own line is
    not relaxed. This is a recorded reduction in the control: until evidence exists, a roster
    may be the largest thing an agent reads.
  - **Exception: roadmap text has no bound by default**. Nothing in a
    roadmap is read by an agent; an item that is sent travels in a Request and keeps the limit.
    The line-breaking character class is not relaxed. Sizes are recorded so a bound can be
    set from evidence.
- **No agent has authority over another.** No orchestrator, no lead. Two asymmetries exist
  and are bounded:
  - **The operator** (a person) holds the §10.2 operations.
  - **The facilitator** is Parley, a participant the broker provides in
    every project. It may ask a question, send a reply a person wrote (recorded as theirs),
    and propose and agree that its own conversation is done. It has no operator operation, is
    never the person seated, never receives anybody's work, and may not assign, direct,
    prioritise, close or override work. It adds who asks, never what asking can do. **Auto
    Accept** (on by default) makes an agent's answer to the ownership question its ownership
    line with no person in the loop, recorded as `automatic` and reviewable on every surface.
- **A roadmap is the person's, never an agent's plan**. An agent may
  only propose one; a person confirms, edits and declines it. No agent operation reads,
  changes or marks done a roadmap, and the facilitator may not propose or confirm one. Work
  reaches an agent as a request from the person, one item at a time.
- **A person's line to an agent is a new Request, except a reply to a question the person
  chose to answer.** That reply is a Clarify on the question's exchange (SPEC §5.6); no surface
  infers which question. The replying agent says in `settles` which of its own questions to the
  person the words of a new Request settled; the broker closes those `settled` and writes no
  message on them. The agent, never the broker or a surface, decides a question is answered.
- **Nothing times out.** An exchange that stops moving is flagged to the person. A
  conversation ends by consensus of the participants asked, or by a person.
- **Credentials are references, never values.** `env:`, `keychain:`, `file:`. The broker
  resolves them at invocation and never persists or logs them. **Not built yet**: nothing takes a
  credential today (SPEC §12); this is the rule for when something does.
- **No network bind; tunnel only.** All three gRPC services share one port, so a reachable
  port is the control plane (`Export`, `Purge`, `Start`) with no authentication
  on the wire (§16). Brokers bind loopback. The supported remote path is an SSH port-forward
  with `PARLEY_BROKER` pointed at the local end. A hand-edited non-loopback endpoint in
  `project.json` is permitted by §16 and warned about on every broker start.
- **A store has one broker.** `StoreLock` is taken before anything else touches the store; a
  second launcher says so and exits. The lock file is never deleted on release, only by
  `project remove --destroy`.
- **The plugin ships no MCP server configuration.** Claude Code keeps a plugin's configuration
  in user scope, so a bundled server would hand every repository the same `PARLEY_AGENT`. The
  agent's name comes from the launch environment, and the whole trust model rests on that. An
  integration for another client belongs beside `plugins/parley`, not inside `src`.

## Behaviours worth knowing

Decided and built. The reasoning is in `SPEC.md`, the feature's `research.md` and git.

- **Only the person moves an exchange** (`move_exchanges`, SPEC §5.0), in `ConversationMoveRepository` and nowhere else. A
  whole exchange goes, all or none, and a Request to a role goes whole; a surface keeps a send to several whole, since the broker has no record of one. A move wakes nobody, so its handler must not nudge;
  an agent on open work is told by a move notice at its next collection. A spin-off's link (`spin_off`) is recorded before
  its first Request is sent. A list's ` - spinoff from <parent>` is display only: `row.Id` stays the name.
- **A deployment is a root with a folder per project** under `<root>/projects/`, holding
  `project.json` and `parley.db`. Naming a project creates it. There is no `--db`, `--listen`
  or `PARLEY_DB`. Ports come from a band (19100–19355) claimed under an exclusive hold on
  `ports.json`. The serve watcher polls through `reread` and changes nothing until a read is
  clean.
- **Project files are written atomically** (rename), preserving unknown keys, byte-order mark
  and line endings, and refusing when anything was left out of a read. A fix that relaxes a
  reader must be checked against every writer that reads through it.
- **Message payloads are inert data** (§13). Addressing is never parsed out of a payload: an
  `@agent` completion is a chip recorded beside the text and removed at the send. An ownership
  discussion is recognised by its conversation identifier, never by a marker in a payload.
- **Accepting an ownership line writes `PARLEY_OWNS` to the repository's `.mcp.json` first,
  then records it** (`record_ownership`). It never writes `agents.owns`, which is filled from
  the launch environment on attach, so *pending* is a comparison of two observations.
- **Turns.** The node reports each turn's start and end (`Nodes.ReportTurn`). Running turns
  live in the broker's memory, not a table. An older node reads as *unknown*, never *stopped*.
  A permission-refused stop is flagged at once.
- **Terminal keys.** Every action is Alt plus a letter, or a declared chord, and menus open with
  F10. A plain character is never an action: no command or key the surface takes may be a
  printable character pressed without Alt or Ctrl, except Space, and Space only where nothing
  types. Named keys may act unmodified where a place says so (Enter, Escape, the function keys,
  the page and arrow keys in a list of ticks). A focused view is offered a key
  before the surface's own handler, so Enter is taken on the box itself. Enter sends; Shift+Enter,
  Ctrl+Enter or backslash then Enter make a line break. A conversation is tailed only for a reader
  at its end. Every key is declared in one table per type (`CommandManager<T>`, built by the type's
  `IKeyTable<T>.BuildTable` and listed once in `CommandTables`): the surface's in `ShellCommands.cs`,
  each dialog's in the dialog, every dialog a place of its own, read-only ones included. A table holds
  commands (named on screen, with the place they are live in and a condition for whether they are live
  now), the keys views take first (a list's cursor keys, the box's editing keys, a list's own Enter) and
  the keys the surface takes with no label (`KeyHandler<T>`: Tab, Ctrl+Q, F8, F10, a pane's Enter, the
  completion list's arrows). Dispatch, the status line, the menus and a button label that names a key read
  only it; button labels are `Name (Key)`. No key handler is written by hand: every dialog derives from
  `TableDialog<T>`, whose sealed `OnKeyDown` only asks its table, and `Shell.OnKeyDown` is a fixed body
  that does the same. The only key-event subscriptions are in `KeyRouting.cs`. A table refuses to build
  when a command shares a key with another claim in a place, its layers included, or is unreachable,
  plain or has both a letter and a chord; the tool builds every table (`CommandTables.BuildEvery`) before
  it takes the console, so a refusal is a plain message saying which kind and exit 70.
  `CommandTableTests` holds the check. `KeysTakenBeforeTheTableTests` (both unit) holds the rest by
  reflection: every view opened as a session builds a table, is listed and derives from
  `TableDialog<T>` (the surface aside); the only key handler overrides are the dialog base's and the
  surface's, and each calls only what it names; no toolkit hook a command reaches (`OnAccepting`,
  `OnActivating`, `OnHandlingHotKey`, `OnCommandNotBound` and the like) is overridden unless the
  test's `OverridesOfCommands` names it with a reason (none does), and the probe counts such an
  override as answering; the only key-event subscriptions are `KeyRouting`'s four; nothing, anywhere,
  reaches the application's keyboard or bindings, a shortcut's `BindKeyToApplication`, a static
  toolkit member over key bindings (`ListView.DefaultKeyBindings`, `View.ViewKeyBindings`,
  `Application.SetDefaultKeyBinding`) or a static toolkit key (`PopoverMenu.DefaultKey`), or hands out
  hot keys (`AssignHotKeys`); no view the terminal makes reads a hot key from its title: a toolkit
  view that reads one by default is made with `HotKeySpecifier = Plain.NoHotKey` set in its
  initialiser (`new ListView { HotKeySpecifier = Plain.NoHotKey, ... }`) or is `PlainButton` or
  `Plain`, which set it when made, and `HotKeySpecifier` is set to nothing else, so a title or text
  set on one later binds nothing, and every view the probe builds has no hot key. The toolkit's own
  `TextView` context menu (Shift+F10 or a right click in the box) is outside this: the toolkit makes
  it, as a `PopoverMenu` whose items read hot keys from their titles (`_Select all`, `_Delete All`),
  and it is older than the check, a recorded gap rather than something the check holds. And a key or hot-key binding, a
  command, a key set on a toolkit view, or a handler for accepting, activating or a hot key is added
  only while a pressed pane or dialog is built: in its constructor (field initialisers included) or
  a non-virtual method of the same type that only that construction calls, never in a lambda, an
  override such as `EndInit`, a method the surface calls such as a pane's `Show`, or the surface;
  and there unconditionally: no branch, loop or `catch` may skip, repeat or choose it, nor a call to
  a construction method that binds. One call is excepted by name, the toolkit's `MenuItem` constructor in `MenuBarView.Views`, and
  each line it makes is checked: no key, no hot-key binding in it or inside it, and no key binding
  but those a toolkit line made with no key carries. It also presses every pane, as a real `Shell`
  builds and wires it and after each pane's `Show` with a row, and every dialog in memory after
  `BeginInit` and `EndInit`, built through every constructor, private ones included, in every shape
  its own arguments can take (each flag both ways, every value of a choice, null and a value for
  what may be null; an argument's contents are not varied, which is why construction may not
  branch), every list given rows, with every named
  key and its chords, Alt and Ctrl with each letter, and plain letters, digits and a few marks: a
  key taken must be claimed in its place; one a command names must be handed to the table first or
  be listed in the test's `TakenAsNamed`; and a plain character taken by a view that does not type
  fails whatever claims it. Tab is not pressed, and a key a view binds, as a key or a hot key, is
  read from its bindings rather than pressed. `NoTwoActionsShareAKeyTests`
  and `KeysNeedAModifierTests` hold the drawn surface to it.
- **Dialogs open on the thread that draws.** The work queue runs on the thread pool and most dialogs open after a read,
  so `Prompting.Open` hands the open to the loop (`Prompting.Marshal`, set by `TerminalCommand`). Never open one
  from the pool directly. What the loop has not started within five seconds is given up, never done late (a late
  dialog could not be closed). Work off the loop reads the box with `Line.WrittenOnTheLoop` and changes it with
  `Line.Rewrite`, read and written in one turn, because the toolkit's text view throws when it is read or changed
  while the loop draws it or takes a key, and a key typed between a read and a write is lost. The harness never pumps
  a loop and leaves `Marshal` unset (only the `OwnsTheMarshal` collection sets it, alone), so a driven test must not
  press a key that starts queued work that opens a view and then draw on its own thread in the same breath: call the
  handler and `await shell.Settled`.
- **A read can land after the person has moved on.** Enter and Escape read a project off the work queue, so
  `SelectAsync` draws only while its project is still in view, and never clears an open conversation. A conversation's
  id is unique only within a project, so the follow loop compares the project too (`Transcript.OpenIn`). The
  terminal's box belongs to one project and conversation (`_lineFor`). Anything new that draws from a read checks the
  same.
- **Hold screens to what is drawn, not what is held in memory.** Read the laid-out screen or
  `Driver.Contents`, not a field, and press a key rather than calling its seam.
- **Anything drawing somebody else's text** (a payload, a broker detail, a path, a log line)
  neutralises control characters one for one and keeps every header at column zero with
  nothing else there.
- **Names use one grammar.** `ProjectName` and the role half of `AgentName` are the same
  expression, anchored with `\z` (`$` matches before a trailing line feed in .NET).
  `Parley.Core.NameCorrection` is the only place it is corrected as typed; a space becomes a
  hyphen.

## Peers

Other repositories have their own agents, reachable through Parley. They cannot see this
repository and this repository cannot see theirs: only the text of a request and its result
crosses between them. `parley_inbox` is pull rather than push - call it when you start work,
and again when you are waiting on a result.

To hand over a file, place it with `parley_file_place` and name it and its sha256 in your
request; fetch a file you are sent with `parley_file_fetch`. Only `.md` and `.txt` files can be
placed, so code travels as `Name.cs.txt`.

- The roster has not been read yet. Run `/parley:write-peers` to fill this in.

**Do not guess at what these repositories do, and do not edit them.** When you need work done
in one, or something it knows, send its agent a `parley_request` saying why and what done
looks like. Do the work other agents request of this repository.
