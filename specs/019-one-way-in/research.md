# Research: One place, and one way in

Phase 0 for [spec.md](./spec.md). One numbered decision per question the spec left open, each with the
argument and what was rejected. Everything here was read out of the repository rather than recalled.

---

## R1. How a port is claimed

**Decision.** A file at the root, `ports.json`, holding one entry per project: the project's name and the
port handed to it. A claim is made by opening that file with **no sharing**, reading it, choosing a port,
writing it back, and closing — the whole read-modify-write inside one hold. The hold is taken only by
`project add` and `project remove`, never by anything that reads, starts or serves.

**The mechanism already exists in this repository and is not invented here.** `StoreLock.Acquire` opens
`<store>.lock` with `FileShare.None` and says why in its own remarks: *"A file opened with no sharing
belongs to its process: a crash releases it with every other handle, so there is no stale lock to clear
and no timeout to choose. On Windows that is a sharing refusal; on Linux and macOS .NET takes an
exclusive `flock`, which holds between separate opens even inside one process."* That is exactly the
property a claim needs, on both platforms, with no new primitive.

**Two things carried across from `StoreFiles.Lock`, both load-bearing.**

- **The file is never deleted.** Its remark records why: *"On Linux an exclusive lock belongs to an open
  file rather than to a name, so a broker unlinking the file on its way out would let the next two
  starters each create and lock a different file of the same name — two owners, each holding a lock that
  says it is the only one."* `ports.json` is created once and never removed, including when the last
  project goes.
- **A failed open is read as *held* only when the file exists afterwards.** `StoreLock` does this so that
  a missing directory is not reported as contention. The same check applies here.

**Where this differs from `StoreLock`, and it is the only difference:** the hold is transient, so
contention is ordinary rather than fatal. `StoreLock` refuses outright because two brokers on one store is
a defect. Two people running `project add` at the same moment is not. So the claim **retries** — a bounded
loop, short backoff, and a refusal that names the contention if the budget runs out. That refusal is
FR-015's "fail saying so", and it must not leave a project folder behind.

**A bind test inside the hold, and this is the part that answers the recorded flake.** Having chosen a
free-looking port from the record, the claim binds it before recording it. Inside the hold, that is safe
against every other Parley claim. It is *not* safe against a non-Parley program taking the port a moment
later — and it does not have to be: FR-012 already says the record states what Parley handed out and
cannot state what the operating system did, and the existing refusal to start on a held port is what
reports that. **What made `FreePort` race was that its bind-and-release had nothing serialising it.** The
hold is what this adds; the bind is kept because it stops a port being handed out that something else
already has.

**The band, and a hazard found while choosing it.** The deployment that prompted this serves on `51707`
and `53283`. Both sit **inside the operating system's ephemeral range** — Windows' dynamic range on this
machine is 49152–65535 (`netsh int ipv4 show dynamicport tcp`), and Linux's default
`ip_local_port_range` is 32768–60999. A port in that band can be transiently held by an *outbound*
connection from any program on the machine, which is a logon-time bind failure that looks like nothing and
happens rarely. Handing ports out automatically from that range would manufacture that flake rather than
inherit it.

So ports are handed out from **19100–19355** by default: above the well-known range, below Linux's
ephemeral floor of 32768, and clear of the ports development tools habitually take (3000, 5000, 5173,
8000, 8080, 9000). Two hundred and fifty-six projects is far beyond what a machine will have; the band is
a constant, not a setting, until somebody needs it to be one.

**Exhaustion** is reported and nothing is created — the same shape as a failed claim. A machine with 256
projects has a problem this command should not paper over.

**The band is for what is handed out, and everything handed out is loopback.** SPEC.md §16 confines the
transport to *"a loopback connection, a private network somebody controls, or a tunnel authenticated
outside this protocol"* — so a broker reachable from another machine is supported, and an early draft of
this plan assumed it away. It cannot be chosen automatically: nothing here knows which interface a person
means to expose, and guessing would put a broker on a network by accident.

**Amended 2026-09-23, the owner's call.** This paragraph concluded that a non-loopback endpoint is
*stated* on the command line (FR-020), bypassing the band, with the availability check binding the address
named (FR-021). Both requirements are withdrawn and `--listen` is gone. The premise above survives intact
— what changed is where the remedy lives: a deployment that must be reachable elsewhere has its endpoint
edited in `projects/<name>/project.json`, which is one place rather than a flag plus a precedence rule.
**The cost is real and is recorded rather than glossed: no command makes a broker reachable off this
machine.**

**Windows reserves ranges, and they move.** Besides the ephemeral range there are *excluded* port ranges,
claimed by Hyper-V and WSL and **reallocated at boot**. On the machine that prompted this,
`netsh int ipv4 show excludedportrange protocol=tcp` reports exclusions at 50000–50059 and seven blocks
between 53116 and 54235 — and **that deployment's second project serves on 53283, one port below the
excluded block beginning at 53284.** A reallocation that shifts down by one takes the broker out with
*"an attempt was made to access a socket in a way forbidden by its access permissions"*, which reads as a
permissions fault rather than a taken port. 19100–19355 is clear of every exclusion on that machine, and
the claim's bind test is what handles a range that moves later.

**There are no existing projects to keep ports for.** This paragraph planned what a migration would
report about an endpoint reachable from off the machine. Nothing has been released, so there is nothing to
migrate: `reset.ps1` clears what the old layout left on a developer's machine and every project is created
fresh. See R6, withdrawn on the same grounds.

**Reconciliation (FR-013).** The record is bookkeeping and the projects are the truth. On every claim,
after taking the hold: read every project's configuration, and treat any endpoint found there as spoken
for whether or not the record lists it. A record that has lost entries costs one directory listing; a
record deleted entirely costs the same and is rebuilt. This is what makes SC-009 true — deleting
`ports.json` must cost nothing but a rebuild.

**Rejected:**

- *A high-water mark alone.* Cheaper, and wrong after the first removal: FR-014 requires a removed
  project's port to be released, which a counter cannot express.
- *No record at all, deriving everything from the projects each time.* Tempting, and it fails FR-010: two
  adds can both list the projects, both see the same port free, and both take it. Something must serialise
  the decision, and the record is the only thing that can be held.
- *A lock file separate from the record.* Two files where one will do, and a second thing to leave behind.
  The record is its own lock, which is what `StoreLock` does.
- *Binding and holding the socket until the broker starts.* Solves the race completely, and means an
  `add` must stay running until a broker takes over, which no command here does.

---

## R2. The directory watcher

**Decision.** There is no watcher, because there never was one. `ServeCommand.WatchAsync` **polls**, every
`WatchInterval` — two seconds — by calling a `reread` function and comparing what it gets. The change is
to `reread`, and to nothing else.

**This was the largest risk in the plan and reading the code removed it.** The spec worried about what
feature 015 recorded: a rename in a watched folder raises a creation and a change that each replace the
pending look, and the fix was to keep the operating system's rename report apart from the pending looks.
**None of that applies.** No `FileSystemWatcher` is involved in serving; the loop wakes on a timer and
reads.

So `reread` changes from *parse one manifest file* to *list the projects directory, and parse each
project's configuration*, and every consequence is already handled:

- **Added, removed and changed projects** are already derived by comparing the new list against the
  running one. `SameDeployment` compares name, store and endpoint; a changed entry is stopped and started.
- **A project that will not start** is already held in a `failing` set and retried only when its entry
  changes, *"because a store that is a directory does not become a file by being asked again every two
  seconds, and the failure line would fill the log."*
- **An unreadable configuration** is already survivable: the loop's `catch` sets `current = null` and
  stops nothing, *"because nothing is stopped on the strength of a file somebody is halfway through
  editing."* That generalises to one unreadable project among many, which is FR-029.

**What the change does cost** is N directory entries and N small file reads every two seconds instead of
one. At the scale this runs at — a machine with a handful of projects — that is not worth engineering
around, and the alternative costs a great deal.

**Rejected:**

- *A `FileSystemWatcher` over the projects directory.* It would be strictly worse: it buys latency this
  feature does not need (FR-034 asks for seconds, and the poll already gives two), and it buys back every
  hazard 015 wrote down. Adopting it would be choosing a known-difficult mechanism to improve a number
  nobody is complaining about.
- *A shorter interval.* Two seconds already satisfies "within seconds" and SC-010's ten.

---

## R4. What `--config` becomes

**Decision.** `--config` and `PARLEY_CONFIG` name **the root directory**, and nothing else.

**Amended 2026-09-23, the owner's call.** This decision originally accepted a value naming the old
manifest *file* and resolved it to the directory containing it, "for at least this release", because the
owner's live scheduled task held that path verbatim. That accommodation is withdrawn: **nothing has been
released**, the only machine carrying the old layout is the one this was written on, and `reset.ps1`
clears it. A compatibility rule kept "for one release" on a product with no releases has no expiry, and
this feature exists because a deployment was arrived at one accommodation at a time.

**What happens instead is a refusal, and a loud one.** A value whose final segment is `projects.json` is
reported — naming the directory to point at, and naming `reset.ps1` — rather than silently resolved.
Recognised by name, never by asking the filesystem: a machine being set up has neither the file nor the
directory, and a rule that behaves differently before and after first run is a rule nobody can reason
about.

**Stated once, in `ProjectManifest`.** Nothing else learns the old spelling, so there is one
implementation rather than one per caller — which is the lesson `LaunchKey` cost during 018, and which
this feature then paid for again in `LockFor`.

**Rejected:**

- *Accepting a file path silently.* It would work and teach nobody, and the entry is only rewritten when
  somebody knows it needs rewriting.
- *Treating the old path as a root.* Worse than either: it would look for `projects.json/projects/`, find
  nothing, and report a well-configured machine as having no projects — a green-looking answer to a
  question nobody asked.
- *A second variable for the root.* Two names for one thing, and every document then has to explain which
  is current.

---

## R6. The migration command

**Withdrawn 2026-09-23, the owner's call: there is no migration.**

This decision designed a command that took each store's lock first as the check that nothing was serving,
moved the store and its companions into the project's folder, and rewrote the configuration — resumable,
because it worked one project at a time.

**None of it is needed, and the premise it rested on was never examined.** The plan assumed deployments
existed to migrate. Nothing has been released. The only machine carrying the old layout is the one this
was written on, and what it wants is a clean start.

**What replaces it is `reset.ps1`**, which is smaller and honest about what it does: it stops the broker,
removes the autostart entry, reads the old manifest *before* deleting it so every store it named goes with
its `-wal`, `-shm`, `.lock`, `.owner` and file-location companions, and **keeps `node.json`** so nobody
re-enrols a repository by hand.

**One thing the migration design got right is kept**: the ordering. Take the lock first, because
`StoreLock` holds a store with no sharing and a running broker cannot have its files deleted underneath
it. The script does that by stopping the broker before it reads anything.

**And one thing it got wrong is recorded.** The first version of the script sandboxed its *files* with a
`-Root` parameter while `parley autostart remove` went on acting on the real machine — and removed a live
logon entry on the machine that was writing it. `-FilesOnly` exists because of that. **A parameter that
sandboxes the files and not the machine is not a sandbox**, which is the fourth time this repository has
recorded a test reaching a real deployment through a seam that covered the wrong thing.

## R3. The test rig conversion

**Decision.** Twenty-six files, not the fourteen a grep for `--db` suggested. Two are the harness and must
change first; four lose their subject; eleven are mechanical; nine need thought. And **the rig does not
call `project add`** — it writes a project folder directly, for a reason that only appeared once the port
band was decided.

### How the harness works today

`BrokerProcess.Start(databasePath, …)` hard-codes one command line:

```csharp
ToolProcess.Launch(environment, "serve", "--db", databasePath, "--listen", "http://127.0.0.1:0");
```

Port **zero** — the operating system assigns one and the broker prints it, and the harness reads it back
off stdout with a regex that scans every line, *"specifically because a version banner added before the
address line once broke every caller at once."* Readiness is that line appearing. Shutdown is
`Kill(entireProcessTree: true)`.

`ToolProcess` blanks seven configuration variables before every launch, `PARLEY_DB` among them, and runs
the tool from a neutral temporary directory rather than the checkout — *"because the repo's own `.mcp.json`
could name a real registered project."*

### The problem that only appears once ports are handed out

Under 019 `serve` has no `--listen`, and a project's endpoint comes from its own file. So a rig that made
projects with `project add` would take ports **from the band, starting at 19100, in every root**. Test
classes run in parallel; two concurrent roots would each be handed 19100, and the second broker would fail
to bind. **The band is per machine; the root is per test.** Allocation is the wrong mechanism for a rig.

**So the rig writes `<root>/projects/<name>/project.json` itself**, with an endpoint from the existing
`FreePort()`. That is not a workaround — it is the correct division. A test that is not about allocation
should not allocate, and `project add` is exercised by the tests that *are* about it, in roots of their
own, serially.

`FreePort` (`tests/Parley.Cli.Tests/Loopback.cs:28`) binds, reads the assigned port and releases it. Its
own comment admits the race and calls it *"settled in practice"*. **This feature must not make it worse,
and does not**: the rig uses it exactly where it is used today — where a port has to be written into a
configuration before the process that binds it exists. `BrokerProcess` keeps reading the real address off
stdout wherever it can, which is the stronger mechanism and is unaffected.

### The conversion

| Group | Files | Work |
|---|---|---|
| **Harness** | `BrokerProcess.cs`, `ToolProcess.cs` | `Start` takes a root and a project name, not a store path; the variable clear-list loses `PARLEY_DB`. Everything else depends on these |
| **Delete** | `OneStoreIsForTestingTests.cs`, `ForeignDatabaseTests.cs`, `NoManifestParityTests.cs`, plus two of three tests in `ServeSelectionTests.cs` | ~24 tests whose subject is `--db` itself: mistyped paths, precedence over a manifest, refusing a foreign SQLite file. The rule is gone, so the tests describe nothing |
| **Mechanical** | `PruneCommandTests`, `StartupBannerTests`, `WebUiContainmentTests`, `ChildLifetimeTests`, `SecondServeTests`, `InFlightWarningTests`, `SecondSessionTests`, `ProjectDestroyTests`, `DeploymentVariables`, `OwnershipRoutingTests`, `AutostartManifestPlanTests` | A store that was incidental becomes a project in a temp root. `AutostartManifestPlanTests` already asserts the end state |
| **Careful** | `ProjectResolutionTests`, `EnrolmentNextStepsTests`, `AutostartPlanTests`, `ProbeSevenTests`, `ProbeThirtyThreeTests`, `ProjectCommandTests`, `InstallScriptManifestTests`, `ServeSelectionTests`, `Parley.Terminal.Tests/NamingTests` | The store-naming mechanism is what is being asserted |

`ServeSelectionTests` is a split rather than a delete: its third test is about catalogue selection and
moves elsewhere.

**`Parley.Terminal.Tests/NamingTests` is the one nobody would have predicted.** It drives the terminal
flow for adopting a broker somebody started *by hand* and naming it. That scenario presumes a hand-run
broker with an explicit store can exist to be discovered — and after FR-007 one cannot. The flow needs
re-deciding, not just repointing.

### Two scope questions the inventory raised, answered here

**Does `project add --db` go?** Yes. FR-007 forbids a store outside a project, and `project add --db` is
the only way a store path is set today. This makes `ProjectCommandTests` (17 tests) the largest careful
file, and it was not in the original four surfaces named — it is in scope, and the estimate includes it.

**Does `--db` keep a read path?** **Yes, and this is the established pattern rather than a new decision.**
`InstalledEntry.Parse` has a `case "--db":` so `autostart status` can describe an entry a previous version
wrote. 0.22.0 named the rule for `--console`: *stop writing, stop accepting, keep reading.* FR-054 requires
exactly that — an entry carrying options this version no longer offers must go on starting — so the read
path stays, and `ProbeSevenTests` and `ProbeThirtyThreeTests` keep their read-compatibility halves and
lose only the write halves.

**Rejected:**

- *Keeping `--db` "for testing only".* That is what the current mess is made of: an escape hatch
  documented as temporary becomes the ordinary path, and then the layout drifts one flag at a time. It is
  affordable to remove only because a project in a temp root now costs one name.
- *Giving each test root its own band.* Bands would have to be coordinated across parallel test classes,
  which is a second allocator to keep correct. Writing `project.json` directly needs no coordination.
- *Having the rig call `project add`.* It would make every broker test depend on the allocator, so an
  allocator bug would fail the whole suite rather than the tests about allocation.

---

## R5. The command removals

**Decision.** Twelve verbs stay, sixteen go. The list below was produced by sweeping every script, hook
and plugin command in the repository first, and deriving the removals from what was left — not the other
way round.

### What calls the tool without a person typing

| Call | Where | Why it cannot go |
|---|---|---|
| `parley waiting <agent> [--with-files]`, with `--broker` or `--project` | `plugins/parley/hooks/parley_waiting.py` L308/L316 | Wired to `SessionStart`, `UserPromptSubmit`, `PostToolBatch` and `Stop`. **It runs on nearly every turn of every session in every enrolled repository.** The hook swallows errors by design, so breaking it fails silently everywhere. |
| `parley project list` | `install.ps1` L538/767/814/868, `install.sh` L524/773/817/872 | Names the projects, then **polls until the broker answers** to prove the install worked. |
| `parley open`, `parley --project <n> open` | `install.ps1` L541/548, `install.sh` L527/534 | What stopping the broker would interrupt. |
| `parley autostart install` | `install.ps1` L790, `install.sh` L793 | The install script's core purpose. |
| `parley autostart remove` | `install.ps1` L308, `install.sh` L309 | The uninstall path. |
| `<tool> --version` | `install.ps1` L723, `install.sh` L726 | The only way to tell a real install from `dotnet tool update`'s silent no-op. |

**And a category the spec did not anticipate: the plugin's own slash commands.** Five of them are executed
by the coding agent, deterministically, from a fenced command line in the command file:

| Slash command | Runs |
|---|---|
| `/parley:files-list` | `parley files list $ARGUMENTS` |
| `/parley:files-open` | `parley files open $ARGUMENTS` |
| `/parley:files-folder` | `parley files folder $ARGUMENTS` |
| `/parley:files-remove` | `parley files remove $ARGUMENTS`, then again with `--yes` |
| `/parley:files-prune` | `parley files prune $ARGUMENTS`, then with `--yes-remove-every-file` |

**So `parley files` stays**, and this is a correction to the spec's reasoning rather than an exception to
it. FR-040 keeps what a program calls; a slash command *is* a program calling — and it cannot be pointed
at a terminal, because a slash command has no terminal to drive. `/parley:setup-many` also runs
`parley enroll scan|plan|apply`, which 019 is replacing anyway, so those three command files are rewritten
with the verb rather than left pointing at something gone.

### The list

**Stay** (12): `serve`, `up`, `autostart`, `terminal`, `web-ui`, `use`, `project`, `enroll`, `waiting`,
`open`, `registry`, `files` — plus `--version` and `--help`.

**Go** (16): `retire`, `reinstate`, `purge`, `export`, `force-conclude`, `prune`, `instruct`, `start`,
`archive`, `unarchive`, `destroy`, `refusals`, `clarifications`, `results`, `flags`, `instructions`.

Every one of the sixteen either takes an identifier a person has no way to obtain from this tool — there
is no `parley conversations` — or is a read the surfaces already show better. The terminal's Health dialog
has bound `flags`, `results`, `refusals` and `clarifications` since 0.26.0, and both surfaces bind every
operator operation, so nothing leaves the product. **`prune` is the one debatable removal**: it takes no
identifier and a person might reasonably cron it. Nothing in this repository calls it, and it deletes text
permanently, so it goes with the rest — recorded here so that reinstating it is a decision rather than a
rediscovery.

### The constraint that will break the build if this is done carelessly

`tests/Parley.Mcp.Tests/PluginTests.cs`, `The_operator_skill_documents_every_operator_command`, asserts the
verbs in `src/Parley.Cli/Program.cs` and in `plugins/parley/skills/parley-operator/SKILL.md` are **exactly
equal in both directions**. It extracts them by regex:

```csharp
Regex.Matches(text, @"parley ([a-z][a-z-]*)")
```

over the **whole source file**, comments and message strings included. So:

- Removing a verb means removing it from `SKILL.md` in the same change, or the test fails.
- **FR-051's stub is a trap.** A removal that says *"`parley export` moved to the terminal"* puts the
  string `parley export` back into `Program.cs`, and the test then demands `SKILL.md` name it too — which
  would document a command that no longer exists. The stubs must name the surface **without spelling the
  old verb after the word `parley`**, or `SKILL.md` must carry a removed-commands section and the test be
  taught the difference. The plan takes the first, because it needs no test change.

A companion test, `The_agent_facing_command_reaches_for_no_operator_command`, asserts `write-peers.md` and
`status.md` contain no operator verb at all — so the same phrasing discipline applies there.

### What else has to move with it

`README.md` (about 90 mentions), `plugins/parley/README.md`, `plugins/parley/skills/parley-operator/SKILL.md`
(a full command listing at L122–178), and `plugins/parley/commands/*.md`. `SPEC.md` §10.2 describes the
*operations*, which do not change — only their command-line spelling does, so the document needs a sweep
rather than a rewrite.

**Rejected:**

- *Keeping the sixteen and merely hiding them from `--help`.* A hidden command is one nobody can find and
  everybody can still run into; the complaint was that the surface is unusable, not that it is long.
- *Removing `files` with the rest.* It would break five slash commands that no terminal can replace.
- *Deleting the verbs outright with no stub.* FR-051 requires a script to be able to detect the failure
  and a person to be told where the thing went.
