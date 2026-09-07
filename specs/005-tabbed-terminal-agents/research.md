# Phase 0 research — a tabbed terminal, and agents added or removed from it

Ten questions the plan could not settle by asserting. Each is answered from this repository, from
the package as downloaded, or marked open with the first task that closes it — and each names what
would change if the answer turned out wrong. Two of them (R3, R4) found the specification asking
for something the protocol does not provide, and the amendment is recorded here and in `spec.md`.

## R1 — Terminal.Gui 2.4.17: what taking the dependency costs

**Decision: `Terminal.Gui` 2.4.17, pinned exactly under central package management, replacing
`Spectre.Console` and `Spectre.Console.Testing` outright.**

Checked rather than assumed, on 2026-09-04, by restoring a scratch project against nuget.org:

| Fact | Value |
|---|---|
| Stable line | 2.4.17 is the newest release that is not a `-develop` or `-beta` build; 2.5.0-beta.1 and 2.6.0-develop.61 exist and are not stable |
| Target | `net10.0` only, which is what this repository builds |
| Direct dependencies | 12: ColorHelper, JetBrains.Annotations, Markdig, five `Microsoft.Extensions.*` at 10.0.7–10.0.9, System.IO.Abstractions, TextMateSharp (+Grammars), Wcwidth |
| Transitive graph | 24 packages in all |
| Advisories | none at any severity (`dotnet list package --vulnerable --include-transitive`) |
| Native payload | Onigwrap 1.0.11 ships `libonigwrap` for thirteen runtime identifiers, via TextMateSharp — a syntax highlighter this surface never uses |

Two things follow for `Directory.Packages.props`:

- **No transitive pin is needed today.** The `Microsoft.Extensions.*` packages arrive at 10.0.7 and
  10.0.9, above the 10.0.0 that `Microsoft.Extensions.Hosting` brings to `Parley.Mcp`; central
  transitive pinning only binds packages that have a `PackageVersion` entry, and none of these do,
  so NuGet takes the higher version in the one project that sees both. The comment above the
  Terminal group must say this, because the Spectre comment it replaces said "brings nothing with
  it" and that stops being true.
- **The graph grows from one assembly to twenty-four**, and `NuGetAudit` fails the build on a high
  or critical advisory in any of them. That is the price of the owner's decision and it is paid
  knowingly; the `Complexity Tracking` table in `plan.md` records it. Spectre.Console was the
  first non-Microsoft, non-gRPC dependency on the tool and had no dependencies of its own.

`Spectre.Console` goes entirely: `grep` finds no `using Spectre` outside `src/Parley.Cli/Top`, and
`ConsolePicker` (the enrolment scan's picker) draws with `System.Console`. Two toolkits in one
process is the fight `Onboarding.cs` already describes and refuses to have.

**If wrong**: an advisory against one of the twenty-four with no patched version is the same
situation `Directory.Packages.props` already documents for SQLitePCLRaw, and is answered the same
way — a justified transitive pin, or a wait. Nothing about the surface depends on which.

## R2 — How is the surface driven by a test?

**Decision: every screen is built against an `IApplication` instance the caller supplies, never
the static `Application` class, and tests supply one whose input is injected and whose screen is
read back as text.**

What 2.4.17 provides, read from its XML documentation rather than its README:

- `Application.Create(ITimeProvider?)` returns an `IApplication`; the documentation calls this
  "the recommended pattern" and says the static model, once used in a process, makes `Create`
  throw. The static `DefaultKeyBindings` carries the warning *"Do not set in parallelizable unit
  tests."* xUnit runs test classes in parallel, and this is exactly the process-global trap
  `TopCommand.DriveAsync` was split to avoid for `AnsiConsole.Console`.
- `ApplicationImpl(IComponentFactory, ITimeProvider)` is documented as *"for testing"*: the
  component factory supplies the driver's input, output and size monitor, and `TestInputSource`
  / `ITestableInput<T>.InjectInput` are the library's own pre-programmed input. `IApplication`
  exposes `GetInputInjector()` — *"programmatic input injection in tests"* — `RaiseKeyDownEvent`,
  `RaiseMouseEvent`, `LayoutAndDraw`, `StopAfterFirstIteration`, and `ToString()` returning the
  application *"as rendered by the driver"*. `IDriver.Contents` is the cell buffer.
- `VirtualTimeProvider` exists for deterministic timeouts, which matters for the seat's idle rule
  and the log tail's tick.

What it does not provide: a named headless driver. `DriverRegistry.Names` holds `WINDOWS`,
`DOTNET` and `ANSI` only. So the test-side `IComponentFactory` is either one the library ships
under another name or one `tests/Parley.Cli.Tests` writes, and which of those is true is **the
first implementation task and a gate on every other one**: a spike that injects a key, sees a view
receive it, and reads a rendered string back — run under podman as well as on Windows, because
drivers are where platforms differ and this repository's record of flakes says so.

The consequence for design is the one 004 already made for a different reason: `Driving.Pick`
chose menu entries by name against the list the surface builds, never by counted arrow keys. Here
the same rule is that **tests find a view by its title and a row by its text**, never by
coordinates — a region that moves when the layout stacks must not move a test.

**If wrong**: if no factory can run without a console, the fallback is `Init(DOTNET)` under a
pseudo-terminal, which is a slower and platform-bound test. That would be recorded as a low
finding, not a reason to skip the journeys: SC-002 replays every 004 scenario and is the parity
contract.

### Answered 2026-09-04 by T005 and T006 — neither, and no component factory is supplied at all

The spike was run and **two of the assumptions above are wrong**, both in the same direction: the
testing surface the XML documentation describes is not reachable from outside the assembly.

- **`ApplicationImpl` is internal.** Its constructor taking `(IComponentFactory, ITimeProvider)` is
  documented *"INTERNAL: ... for testing"*, and the word to read there is the first one. A test
  assembly cannot construct it.
- **A custom factory cannot be registered either.** `DriverRegistry.Register` is public and takes a
  `DriverDescriptor` carrying a `Func<IComponentFactory>`, and registering one appears to work:
  `IsRegistered` and `GetSupportedDrivers` both then name it. But `ApplicationImpl.CreateDriver`
  looks the name up in the registry *and then switches on the string* against `windows`, `dotnet`
  and `ansi` — read from its IL, not inferred — so a fourth name passes the registry check and
  falls through to `Unknown driver name`, and the descriptor's factory is never called.

**What works is a recipe rather than an API, and it needs no factory**: `Application.Create` with a
`VirtualTimeProvider`, `Init("ansi")`, the screen size *set* rather than queried, `Begin` rather
than `Run`, then `LayoutAndDraw`, `InjectKey` and `ToString`.

Three things make it sound, each measured rather than assumed:

| Worry | What was measured |
|---|---|
| The ANSI driver writes escape sequences to somebody's terminal | A session that draws, takes a key and ends writes **zero bytes to stdout** — every terminal write happens in the main loop, and `Begin` does not run it |
| It reads the console and steals keystrokes | Stdin was **left unread**: `HELLO` piped in was still there afterwards. `Init` starts five threads and none of them consumes input until the loop runs |
| The screen never renders | It stays 0×0 because the ANSI size monitor asks the terminal with a cursor-position query and nothing answers. Setting `Driver.SetScreenSize` and `Screen` fixes it — and is also what lets a test ask for forty columns without a terminal to resize |

Two smaller findings, both now written into the harness:

- **`Application.Mouse.RaiseMouseEvent` does not route to a view.** It raises the application-level
  event; the routing lives in the main loop. `Driving.Click` therefore hit-tests the point itself
  with `View.GetViewsUnderLocation` and delivers to `View.NewMouseEvent`, which does reach the
  view's own handling — a click still lands on whatever is under the point rather than on the view
  the test named, so a covered region fails as it should.
- **Hit testing returns adornments.** The deepest view under a button is its `ShadowView`, drawn
  outside the view and handling nothing, with `MarginView` above it. `Driving.Click` skips
  adornments and shadows and takes the deepest ordinary view; without that every click lands on
  decoration and nothing happens, which is exactly how the spike first failed.

`tests/Parley.Cli.Tests/Top/HarnessTests.cs` asserts every line of the recipe, so a package
upgrade that breaks one fails there with an obvious cause rather than across twenty screen tests
with an obscure one. **Eight tests, green on Windows and under podman on Linux (T006).** The
`Init(DOTNET)` fallback was not needed.

## R3 — Does an added agent become `startable` within ten seconds?

**No, and not on one count but two. One is fixed here; the other is a specification defect and
the specification is amended.**

The spec's US3 scenario 4 and SC-003 say the new agent is *listed as startable* within ten
seconds of adding it. SPEC.md §14 defines `startable` as *node connected, agent not connected*,
read from two connection columns, for an agent the registry holds. Two things stand in the way:

1. **The node announces its roles once.** `NodeCommand.RunAsync` reads the catalogue, puts its
   roles in the `JoinRequest`, and holds the stream. A role added to the file afterwards is never
   announced, and §17 question 13 records that the broker refuses to send a start for a role the
   node did not name. So even a registered agent, added to a catalogue under a running node, would
   read `startable` and never start — the exact case question 13 calls misleading.

   **Decision: the node watches its own catalogue and rejoins when it changes.** A stamp poll at
   `ServeCommand.WatchInterval`, the same two seconds the manifest watcher uses; on change the
   node ends its `Join` stream and joins again with the new roles, keeping its resident agents.
   The gap between the two joins reads as `stranded` for under a second, which is honest — the
   node is not connected for that moment. It lives in `NodeCommand` rather than in the serve
   watcher because the node is the thing that knows its file, and `parley node` run on its own
   must behave the same as one hosted by `serve --with-node`. The serve watcher keeps comparing
   the catalogue *path* and nothing about it changes.

2. **The registry lists only agents that have registered**, and registration is the adapter's own
   act on attach (SPEC.md §12.3). Nothing on an operator's surface registers an agent, and nothing
   should: principle III of the constitution says identity is fixed by the launch and never by a
   call, and a surface that inserted a registry row for a repository would be a caller-supplied
   identity by another name. Starting the agent once to make it register would spend somebody's
   tokens with nobody watching, which is the consent `unattended` exists to record and this
   feature must not take by implication.

   **Decision: the agents region shows the project's catalogue beside the registry, and an agent
   that is in one and not the other says so in words.** Three states, each named: *registered
   and catalogued* (the ordinary row, with §14's presence); *catalogued, not yet registered*
   (what adding produces — the node can start it, and it will register the first time its server
   attaches); *registered, not in this catalogue* (question 13's misleading `startable`, now said
   out loud). SC-003 and scenario 4 are amended to ask for the second state within ten seconds,
   and for `startable` once the server has attached once.

This makes the catalogue the second file the surface reads, where FR-006 allowed one. It is
amended too, on the argument that the catalogue is a file the broker never sees (§17 q13 keeps
what a machine can launch local on purpose), so reading it is not a second answer to a question
the broker answers — it is the only answer to one the broker cannot.

**If wrong**: if the owner would rather the surface not read the catalogue, adding still works
and the added agent simply does not appear until it registers; the words "catalogued, not yet
registered" become a one-line report after adding instead of a row. That is a smaller feature,
not a different one.

## R4 — What does "the launch configuration names the project" mean?

**Decision: `PARLEY_PROJECT=<name>` in the `parley` server's `env` in `.mcp.json`, and
`EnrollmentPlan.Describe` learns to write it.**

`Parley.Mcp` already reads it: `ServerOptions.Where` resolves `PARLEY_PROJECT` through the manifest
to the project's endpoint and refuses to guess when the name is unknown — *"connecting to another
project's broker would put this agent in a roster it does not belong to"*. What the enroll command
writes today is `PARLEY_AGENT`, `PARLEY_OWNS` and `PARLEY_WORKSPACE`, and no project: it predates
the manifest and targets whatever `PARLEY_BROKER` or loopback says.

So `Describe` gains an optional project name. When given, `PARLEY_PROJECT` is written; when the
entry already carries a different `PARLEY_PROJECT`, or a `PARLEY_BROKER` naming another endpoint,
that is FR-009's *"already carries a launch configuration for another project"* and is refused
with the project's name. An entry with neither is unpinned, and re-pointing it is allowed and
said. The enroll command passes the project it resolved when it has one, so a manifest-mode
machine's `enroll apply` writes the same thing the surface does — one writer, as FR-008 asks.

**If wrong**: the alternative is `PARLEY_BROKER=<endpoint>`, which pins the repository to an
address rather than a name and goes stale the first time `project set` moves a port. The name is
the stable thing, and `Parley.Mcp` already prefers it.

## R5 — Where does the per-repository write live, so both writers are one?

**Decision: the body of `EnrollCommand.ApplyAsync`'s per-entry loop is extracted into
`Enrolment`, an internal type in `Parley.Cli` with `Add` and `Withdraw`, and both the command
and the surface call it.**

`ApplyAsync` is private and driven by a manifest file (`parley-agents.json`); the surface adds
one repository at a time and has no manifest. Reimplementing the writes would be the second
writer FR-008 forbids. The extraction is mechanical: `EnrollmentPlan.Describe` already returns
what would change and the contents to write, and `ApplyAsync` writes them and the catalogue
entry. `Add` does that for one entry; `Withdraw` is its inverse and is written beside it so the
two cannot drift:

- remove `mcpServers.parley` from `.mcp.json`, leaving every other server and the file itself —
  a file left with an empty `mcpServers` is a smaller diff than a deleted one and is what the
  person would revert to;
- remove the agent's entry from the catalogue;
- **never touch the instructions file.** The Peers frame is the project's own, the roster block
  in it is written by `/parley:write-peers`, and `Describe` already refuses to rewrite a section
  it cannot tell from the project's additions. Withdrawing leaves it for the same reason.

The `Onboarding` screen — scan, plan, apply of a manifest — survives as a dialog on the new
toolkit and keeps calling the command, as it does today.

**If wrong**: nothing in the protocol depends on where this code sits. The cost of getting it
wrong is one more copy of a file write, which is the defect being avoided.

## R6 — Retire, reinstate and purge: what is called, and what is said first

**Decision: the three operations are called on the Operators client through `BrokerClient`,
which already has `RetireAsync`, `ReinstateAsync` and `PurgeAsync`, and nothing is added to the
protocol.** The description before them is composed from two reads that exist:

| Fact shown | Read |
|---|---|
| role, workspace, adapter, status, presence | `DescribeRegistry` |
| what it owes in an open exchange | `Waiting` — edges whose `WaitedOn` is the agent |

The consequence shown before a purge is SPEC.md §14's own sentence, not a paraphrase: *it deletes
every conversation it took part in, including the halves belonging to peers who did nothing
wrong, and cannot be undone.* When the agent owes an answer, FR-014's second confirmation names
the asker from the waiting edge and says what they will see — the exchange gone rather than
timed out — because retirement's outcome (*"those exchanges run out of time on the ordinary
schedule and each asker is told nobody answered"*, from `OperatorCommands.RetireAsync`) is the
kinder one and the person should be choosing between them knowingly.

The order is fixed: describe → choose (keep first) → for a purge with a debt, the consequence
and an explicit acceptance → the name typed back → the operation → the files withdrawn → a list of
exactly what changed. The files are withdrawn *after* the broker answered, so a refused or failed
operation leaves the repository as it was.

`SurfaceTests` changes shape rather than being weakened. Today it forbids six operation names
anywhere under `Top`. After this, `RetireAsync`, `ReinstateAsync` and `PurgeAsync` may appear in
exactly one file, which must contain the typed-name prompt — the same rule `Removing.cs` lives
under for "destroy" — and `ForceConcludeAsync`, `InstructAsync` and `PruneAsync` stay forbidden
everywhere. FR-017's enumerability is that test.

**If wrong**: if the owner wants `Instruct` on the surface too, it is a decision to argue in
`contracts/operations.md` first, exactly as the existing test's failure message says.

## R7 — The person's own seat, and other people's

**Decision: any registry row whose adapter is `Seat.Adapter` is a seat and is never offered for
retiring or purging, whether or not it is this person's.** FR-016 names the person's own; the
reason it gives — a person leaves a seat, they are not retired — applies to a seat held from a
browser by somebody else just as well. R2 of 004 made both surfaces one adapter for exactly
this kind of reason. Leaving one's own seat stays on the agents region, as today.

## R8 — Creating a catalogue for a project that has none

**Decision: `<store name>-node.json` beside the store, recorded on the entry through the same
code `parley project set <name> --node-config <path>` runs, and the person is told the project's
broker will restart to host it.**

The serve watcher compares the catalogue *path* as part of `SameDeployment`, so recording one on
an entry restarts that project (broker and node) on its changed entry within two seconds. Agents
attached to it drop and reattach, which the seat book already survives. That is a consequence
worth a sentence on the screen and not worth a new mechanism: it happens once per project, the
first time an agent is added, and it is what `project set` does from a shell today.

## R9 — Interactivity

**Decision: `ConsolePicker.Available` is checked before the toolkit is initialised, as today.**
Terminal.Gui's own behaviour on a redirected console is not relied on; FR-018's refusal names
what to run instead and is unchanged.

## R10 — Layout when the four regions do not fit

**Decision: side by side at eighty columns and above; stacked as tabs below.** Wide, the projects
list takes the left, agents and conversations share the middle, the transcript the right, and a
status line names the focused region in words. Narrow, the same four views become the pages of a
`TabView`, Tab still visits every one, and the status line still names it. The threshold is a
constant with a test at seventy-nine and eighty columns, because the 004 narrow-terminal defect
(a recipient column silently dropped) was found by a width test and not by looking.

## What is deliberately not researched

**Whether the web console changes.** It does not; SC-005 holds because both surfaces read
`Parley.Client`, and this feature adds nothing to that library except what R5 puts in `Parley.Cli`.

**Mouse support beyond what the toolkit gives.** Every view is a toolkit view; the mouse reaches
it through the driver. FR-003's test is that the same journey completes by mouse, not that
anything is drawn for it.
