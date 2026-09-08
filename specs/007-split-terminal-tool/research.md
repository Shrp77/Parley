# Research: 007 — The surfaces are their own tools

Seven decisions. Each was checked against the code rather than assumed.

---

## R1 — The catalogue reader moves to `Parley.Configuration`

**Decision.** Move `NodeCatalogue` from `Parley.Headless` to `Parley.Configuration`.

**Rationale.** It was checked, not guessed. `NodeCatalogue.cs` uses two things:
`System.Text.Json` and `Parley.Core`. It references nothing else in `Parley.Headless` — not
`InvocationCommand`, not `ResidentAgent`. It is in that assembly by placement, not by need.

`Parley.Configuration` references only `Parley.Core` and holds `ProjectManifest` and
`SerialisationOptions`. Its stated reason for existing is per-machine configuration read by two
programs that cannot otherwise share a reader, knowing nothing of brokers. A node catalogue is the
same shape of thing as a project manifest, read by the same kind of caller.

`Parley.Headless` keeps its reference to `Parley.Store` — `AgentFleet` needs it, legitimately.

**This resolves a documented workaround.** `Parley.Client/Views/AgentStanding.cs:107` already says:

> *"`NodeCatalogue` lives in an assembly this one does not reference and must not: the console
> reads this too, and a route from a web page to anything that opens files is the dependency
> `GraphTests` exists to refuse."*

`Standing.Join` therefore takes `IReadOnlySet<string>` instead of a catalogue. That workaround
exists because the reader is in the wrong assembly. After the move, `Parley.Client` — which already
references `Parley.Configuration` — could take the catalogue directly.

**It should not, and the plan does not.** The string set is the better boundary independently of
where the reader lives: `Standing.Join` needs the set of role names, not a file reader, and the
console and terminal reach it from different places. The move removes the *reason* for the
workaround, and the workaround is kept because it is also correct on its own terms. The comment is
rewritten to say that rather than citing a constraint that will no longer be true.

**Alternatives considered.**

- *A new `Parley.Catalogue` project.* Rejected: one type, and `Parley.Configuration` already means
  "per-machine files two programs read".
- *Leave it and have the terminal take strings.* Rejected: it does not fix the leak, it hides it.
  The terminal genuinely needs to read a catalogue file to show what a machine could start.
- *Move `AgentFleet` out of `Headless` instead.* Rejected: larger, and solves a problem nobody has.

---

## R2 — The broker launches the web UI as a child process

**Decision.** The broker's flag starts the web UI executable as a child process, and supervises it
only to the extent of not caring whether it lives.

**Rationale.** Three options were considered.

| Option | Verdict |
|---|---|
| Broker launches it as a child | **Chosen** |
| A second scheduled task | Rejected |
| Nothing; the operator starts it | Rejected |

*A second scheduled task* was the obvious answer and is wrong for one reason: the address. The
broker binds a port that may be `0`, and today the web UI is told the address the broker
**actually** bound, not the one it was asked for — `Program.cs` carries that note. A separately
scheduled web UI would have to discover it, which means either a fixed port (losing what the note
protects) or a discovery mechanism this feature does not need to invent.

*Nothing* fails FR-018 and the §17 question 15 reasoning behind it.

Launching a child keeps the address flowing on the command line, exactly as it flows into
`ConsoleHost.RunAsync` today, and keeps one scheduled entry.

**FR-015 is not violated.** The broker's tool needs the web UI's *command name*, not its assembly.
Starting a process by name is not a dependency on what that process contains.

**FR-017 is satisfied structurally.** The broker starts the child and does not wait on it. If it
cannot start — not installed, port held, missing runtime — the broker reports it and carries on.
This is what the existing in-process containment does by hand today, and it becomes true by the
process boundary rather than by a `catch` that has to be maintained. The one thing the broker must
do is not leave an orphan: the child ends when the broker does.

**Alternatives for supervision.** Restart-on-crash was considered and rejected as scope: a web UI
that dies is an inconvenience, and a restart loop against a permanently held port is worse than
staying down and saying so.

---

## R3 — Project and package layout

**Decision.**

| Project | New? | References | Tool package |
|---|---|---|---|
| `Parley.Core` | no | — | no |
| `Parley.Configuration` | no | Core (**+ `NodeCatalogue` moves in**) | no |
| `Parley.Transport` | no | — | no |
| `Parley.Client` | no | Configuration, Transport | no |
| `Parley.Store` | no | Core | no |
| `Parley.Broker` | no | Core, Store | no |
| `Parley.Server` | no | Broker, Transport | no |
| `Parley.Headless` | no | Core, Store | no |
| `Parley.Web` | no | Client, Configuration, Transport | no |
| `Parley.Cli` | no | Broker, Client, Configuration, Headless, Server, Transport — **not Web** | `parley` |
| `Parley.Mcp` | no | unchanged | `parley-mcp` |
| **`Parley.Top`** | **yes** | Client, Configuration, Transport | `parley-top` |
| **`Parley.WebUi`** | **yes** | Web, Client, Configuration, Transport | `parley-web-ui` |

`Parley.Cli/Top/*` moves wholesale to `Parley.Top`. `Parley.WebUi` is a thin host: `Parley.Web`
already holds the pages and `ConsoleHost`; the new project exists only to make it a tool.

**`ConsoleHost.LoopbackAddress` stays where it is, in `Parley.Web`.** It is used by `Program.cs`
(three places) and `Shell` (one). After the split, `Parley.Cli` may not reference `Parley.Web`, so
the constant must be reachable without it.

*Decision: the address constant moves to `Parley.Configuration`*, and `Parley.Web` references it
from there. That keeps a single source, which FR-008 and SC-006 require, and puts it in the
assembly every tool already has. `Parley.Web` keeps the reasoning comment about §16 with the
constant; the constant is configuration, and the reason it is loopback is configuration's business
too.

**Alternative rejected:** duplicating the literal in each tool. It is the exact defect
`ConsoleAddressTests` was written for — a hand-copied `5045` against a real `5280` — and that test
would have to be deleted to allow it.

---

## R4 — The rename keeps the old spelling for one minor version

**Decision.** `parley web-ui` and `--with-web-ui` are the names. `parley console` and
`--with-console` keep working, print a one-line notice naming the new spelling, and are removed at
the next minor version. `OperatorConsole` and `ConsolePicker` do not change.

**Rationale.** The old spelling is in a scheduled task on every machine that has run
`autostart install`, including the owner's, which is registered with `--with-console` right now.
Removing it in the same change that renames it means an upgrade that leaves a scheduled task
invoking a flag the new binary rejects — the broker would fail at logon, silently, where nobody
looks. That failure mode is named in `install.ps1`'s own comments about ordering.

The deprecation is recorded in `CLAUDE.md` with the version it is removed at, so it is a decision
with a date rather than a kindness that becomes permanent.

**`install.ps1` re-registers the autostart entry on every run**, so a machine that installs picks up
the new spelling immediately. The compatibility window covers machines that upgrade the binary
without re-running the installer.

---

## R5 — `GraphTests` walks five roots, and the web stack is checked by absence

**Decision.** `GraphTests` walks transitively from `Parley.Client`, `Parley.Web`, **`Parley.Top`**,
**`Parley.Configuration`** and **`Parley.Cli`**, asserting:

| Root | Must not reach |
|---|---|
| `Parley.Client` | a store |
| `Parley.Web` | a store |
| `Parley.Top` | a store |
| `Parley.Configuration` | a store, and anything that opens a socket |
| `Parley.Cli` | **`Parley.Web`** |

The last is FR-015 turned into a check. It is the one that would otherwise be re-broken by somebody
adding a reference for a constant, which is precisely how the current leak happened.

`Parley.Configuration` gains a rule because `NodeCatalogue` is moving into it and it is about to be
referenced by every tool. A configuration assembly that acquired a store or a transport would put
one in all four.

**SC-005 is checked by packing, not by reading.** A test packs the `parley` tool and asserts no
ASP.NET Core assembly is in the output. Reading the project file proves the direct reference is
absent; packing proves the transitive one is too, which is the property SC-005 actually claims.

**Probe first, per the constitution.** Before the fix: add `Parley.Top` to the walk and watch it
fail on the existing `NodeCatalogue` route. A rule whose failure has never been seen is a rule
nobody has tested.

---

## R6 — Install takes a tool set, and asks the broker what is in flight

**Decision.**

*Which tools.* `install.ps1` gains `-Surfaces` taking `terminal`, `web-ui`, `both` or `none`,
defaulting to `both` on Windows and `web-ui` on Linux. The tool table grows two rows, each marked
with which surface it belongs to; nothing else in the script learns what a surface is.

The Linux default follows the spec's own reasoning: a headless machine reaches a web UI through an
SSH tunnel more naturally than it holds a terminal session open. It is a default, not a rule.

*What it reports.* The script already compares the version on PATH against the one built. It gains
a per-tool line saying replaced, unchanged, or skipped. FR-010 is then satisfied by the same
mechanism that makes a silent no-op visible today.

*A terminal holding its own executable.* The existing adapter check generalises: the table already
knows each tool's command name, so the holder check becomes "any process named after a tool being
replaced". `-Force` ends them with the same wording. A terminal is a person's window, so the
message names what will be closed rather than just counting it.

*The interruption warning (FR-012).* Only the broker knows what is open. The script asks it, using
the operator surface that already exists — the same query the terminal's conversation list uses,
filtered to open exchanges. If the broker cannot be reached, the script says it could not ask
rather than claiming nothing is in flight. **A guard that fails open must say so**; this repository
has one recorded instance of the opposite being a finding.

The warning is a warning, not a refusal: `-Force` already means "the decision has been made by the
person it belongs to", and stopping an operator from upgrading is not this script's job.

**Alternative rejected:** having the script read the store directly. It would be quicker and it
breaks the boundary that only the broker opens the store — for a progress message.

---

## R7 — Sequencing: the leak first, then the terminal, then the web UI

**Decision.** Four changes, each independently shippable and verifiable.

| Step | Content | Verified by |
|---|---|---|
| **1** | Move `NodeCatalogue` to `Parley.Configuration`. Extend `GraphTests`. No packaging change. | The extended walk fails before the move and passes after |
| **2** | `Parley.Top`, the `parley-top` package, `install.ps1` tool set, the forwarding wrapper | A terminal-only change installs with the broker untouched (SC-001) |
| **3** | `Parley.WebUi`, the rename, the child-process launch, `--with-console` deprecation | Logout and logon reaches the web UI (SC-009); a held port leaves the broker serving (SC-008) |
| **4** | FR-012's open-exchange warning | Warned before a broker stop with an exchange open (SC-003) |

**Step 1 ships alone and first because it is a defect today**, not part of the packaging change. The
terminal reaches SQLite now, in a shipped build, and that is true whether or not the rest of this
feature happens. It also de-risks everything after it: step 2 is a file move once the leak is gone,
and would otherwise be a file move plus an untangling.

**Step 4 is last and is separable.** It is the only part that could be dropped without making the
rest incoherent, so it goes where dropping it is cheapest.

Each step bumps `VersionPrefix` — steps 1 and 4 the patch, steps 2 and 3 the minor, since the tool
set a machine installs is a breaking change to how it is installed.

---

## R8 — The terminal needs the machine-setup half of the CLI, so that becomes `Parley.Setup`

**Found while implementing step 2, not while planning it. R3 said `Parley.Top` would reference
`Parley.Client`, `Parley.Configuration` and `Parley.Transport` and nothing else. That is not
achievable, and the reason is a design decision the terminal already made.**

The terminal does not reimplement enrolling a repository or creating a project. It runs the same
commands a shell runs, in process, and shows what they said — `Onboarding.RunAsync` captures
`EnrollCommand.RunAsync`'s two writers and prints its words verbatim, with a remark saying why: a
machine configured by environment or by project gets the same answer here as from a shell.

Measured rather than estimated. Taking every `Parley.Cli` type the terminal names in non-comment
code and closing over what those name in turn gives **11 files and 4,153 lines**:

```
AutostartPlan  ConsolePicker  EnrollCommand  EnrollmentEntry  EnrollmentPlan  Enrolment
InstalledEntry  ProjectCommand  ProjectResolution  RepositorySelection  ScheduledTasks
```

The closure is clean: every one of them imports `Parley.Configuration`, `Parley.Core` and the BCL,
and nothing else. None reaches a store, a broker, a server or the web stack. None refers back to
anything staying in `Parley.Cli`, so there is no cycle to break — this cluster is already a
separable layer that has never been separated.

**Three homes were possible.**

*Leave it in `Parley.Cli` and have `Parley.Top` reference it.* Rejected: `Parley.Cli` references
`Parley.Broker`, `Parley.Headless` and `Parley.Server`, so the terminal would reach a store
transitively — which is precisely the leak step 1 just closed, reopened by a wider route.

*Move it into `Parley.Configuration`.* Rejected, and this is the interesting one. The charter fits
almost exactly — per-machine configuration that two programs need and cannot otherwise share. But
`Parley.Mcp` references `Parley.Configuration`, and CLAUDE.md records that the MCP server
"deliberately pulls in neither" the broker nor the store. Putting 4,153 lines of enrolment and
scheduled-task code there would hand every agent's server the code that writes its own launch
configuration. The dependency would be harmless at runtime and wrong as a surface, and this
repository's position is that a boundary nothing enforces decays.

*A project of its own.* **Chosen.** `src/Parley.Setup` holds what a person does to a machine so
Parley can run on it: enrol a repository as an agent, name a project, resolve where the per-machine
files live, and install or read the autostart entry. It references `Parley.Configuration` and
nothing else. `Parley.Cli` and `Parley.Top` both reference it; `Parley.Mcp` does not.

**What is preserved is the constraint, not the list.** FR-004 asks that the terminal have no route
to a store, checked transitively. `Parley.Setup` has none, so the walk added in step 1 still holds
and now covers a fourth root. The enumerated reference list in R3 is amended; the property it was
protecting is unchanged.

**What this also fixes.** `Parley.Cli` has been two things in one assembly — the operator's commands
and the machine's setup — and nothing said so. Splitting the terminal out is what forced the
question, but the answer was overdue independently: the setup half is what `parley`, `parley-top`
and any future surface all need, and it is the half with no broker in it.
