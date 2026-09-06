# Parley

Cross-session agent-to-agent messaging with a durable store and a registry. **`SPEC.md` is
the primary artifact of this repository; the code is a reference implementation of it.** If
code and spec disagree, that is a defect in one of them, and which one is a decision, not an
assumption — do not silently change code to match spec or spec to match code.

Read `SPEC.md` before changing protocol behaviour. Open design questions live in `SPEC.md`
§17.

## Layout

```
src/Parley.Core     Domain. No I/O, no dependencies. The vocabulary — AgentName, PairKey,
                    Exchange, WaitForGraph, BrokerEndpoint, MachineName, Responder,
                    ProtocolVersion, WorkspacePath, the enums — plus the two contracts of
                    broker V1 and the shapes its operations return. WorkspacePath is
                    string comparison rather than path handling on purpose: the broker
                    compares workspaces from machines it is not on, so touching a
                    filesystem here would both break the no-I/O rule and answer the
                    wrong question.

                    The contracts are IParleyBrokerClient (SPEC.md §10.2, what a broker
                    owes an operator's console) and IAcceptsOperatorInstructions (§11.5,
                    what an agent must be able to receive from that operator). They are
                    two interfaces rather than one because a single one would hand
                    whoever bound the agent's half every operator operation beside it —
                    the structural version of the naming convention §10.2 opens by
                    admitting is thin. Here rather than beside either implementation
                    because a client and a server both see this project and it drags in
                    nothing: an implementation over gRPC and one over anything else are
                    both implementations of the same declaration.
src/Parley.Store    Persistence. SQLite via Dapper. ParleyDatabase (connection
                    factory), SchemaMigrator, WriteScope, and the repositories:
                    Exchange, Message, Agent, Conversation.
src/Parley.Broker   Orchestration. Decides when the other two run. Broker holds the
                    agent operations of SPEC.md §10.1; OperatorConsole holds §10.2.
                    Knows nothing of transports or adapters, and must not learn.
src/Parley.Transport The wire contract, generated from proto/parley.proto. No
                    hand-written code at all, which is why its warnings are silenced.
src/Parley.Configuration The project manifest of SPEC.md §4.1: where it lives, what it
                    holds, what it refuses. A project of its own because two programs
                    read it and cannot otherwise share one reader — Parley.Cli pulls in
                    the broker and the store, Parley.Mcp deliberately pulls in neither,
                    and Parley.Core has no I/O. Read by both; written only by Parley.Cli.
                    Nothing here knows what a broker is.
src/Parley.Client    What a client of a broker needs in order to show a deployment to a person
                    and to seat one. References Transport and Configuration and nothing else.

                    A project of its own for the reason Parley.Configuration is one, and in nearly
                    the same words: two programs read it and cannot otherwise share one reader.
                    The console reads it to render a page and `parley top` reads it to draw a
                    terminal, and leaving it in an assembly called Web would mean a terminal
                    depending on ASP.NET Core to find out who is in a conversation.

                    The split was measured, not guessed. Of the fifteen types the console held
                    outside its Razor pages, exactly one touches HTTP -- checked by type name
                    rather than by `using` directive, because Microsoft.NET.Sdk.Web supplies
                    implicit global usings and an absent using proves nothing. SeatCookie stayed;
                    the other fourteen moved.

                    **There is no SQLite here and there must never be.** GraphTests walks the
                    project graph transitively from both this and Parley.Web, because the
                    console's own one-hop check stopped being sufficient the moment it referenced
                    this one.

                    The drive belongs here too. SeatVoice.TakeDeliveryAsync drains and records in
                    one act, and its AnswerAsync overload settles the seat when the broker says
                    the exchange closed. Two surfaces each writing that themselves is how they
                    come to disagree about what a person has seen.

src/Parley.Web      The operator's console of SPEC.md §10.2, and a client of the broker
                    rather than part of it. References Transport and Configuration and
                    nothing else: there is no SQLite on its graph and there must never be,
                    because §4 gives the store to one process. Not inside Parley.Server for
                    the same reason.

                    `parley console` starts it, so Parley.Cli references this and the tool
                    carries Kestrel. `parley top` shows the same deployment in a terminal, built
                    on Parley.Client and **Terminal.Gui v2**, and needs no browser -- the machine
                    running a broker is frequently not the one somebody has one open on. Four
                    regions on one screen, Tab between them, and the toolkit's cost is recorded
                    beside its pin in `Directory.Packages.props`: twenty-four packages where
                    Spectre.Console brought none.

                    **The surface rule was amended twice, on the same terms both times.** It read:
                    a purge behind a keystroke is a different risk from one behind a page that
                    describes it first. What the rule actually asks for is a description and a
                    deliberate act, and a typed name is a deliberate act -- so destroying a
                    project was admitted in 004 (2026-09-03), and retiring, reinstating and
                    purging an agent in 005 (2026-09-05), both by the owner and both on the
                    grounds that a surface which can create a thing must be able to remove it.

                    Neither is behind a keystroke. `Removing` describes a project, offers
                    forgetting (deletes nothing) beside destroying, and takes the project's own
                    name typed back. `Withdrawing` describes an agent -- state, workspace, what it
                    owns, and every open exchange it owes -- offers keeping first, and takes the
                    agent's name typed back for retiring and for purging; a purge that would
                    orphan an exchange names the asker and takes that acceptance before the name.

                    **SurfaceTests is what makes both properties of the code rather than habits.**
                    `Retire`, `Reinstate` and `Purge` may appear in exactly one file, which must
                    contain the typed-name prompt; the `destroy` argument in exactly one other;
                    and `ForceConclude`, `Instruct` and `Prune` in none. A helper whose *name*
                    merely contains one of those words fails it, which has already happened once
                    and is the point.
 That was decided deliberately and against the argument
                    recorded here before it: a second tool would have kept ASP.NET Core off
                    the machines that only run an agent, and one command is worth more than
                    that saving.

                    **`serve --with-console` hosts it inside the broker's process, and
                    `autostart install` turns that on by default.** The project boundary is
                    unchanged — Parley.Web still references no store — but the runtime one
                    is: Kestrel now shares a process with the thing that owns the database.
                    That is the trade `--with-node` already made, and it is paid for by
                    containment: a console that cannot bind reports it and the broker keeps
                    serving, because a web page must never cost a deployment. On by default
                    because of SPEC.md §17 question 15 — an agent attached through somebody's
                    editor window is the one thing nothing can hand a turn to, so a console
                    remembered separately is the half of a deployment that works for agents
                    and not for people.

                    The project stays separate anyway — it is what keeps the
                    store off the console's dependency graph, which is the constraint that
                    actually matters, and `ConsoleHost` is the single place the host is
                    built so `parley console` and `dotnet run` cannot drift apart.

                    Razor Pages are found by walking the entry assembly, which is `parley`
                    when the subcommand starts it, so `ConsoleHost` names its own assembly
                    as an application part. Without that every route 404s while the
                    stylesheet still serves, because a file beside the binary is found by a
                    different mechanism than a compiled page.

                    **The console has a second identity, and the rule above was amended
                    rather than broken.** It binds `Operators` as the operator, and binds
                    `Agents` for the seat a person holds — so somebody can take part in a
                    conversation instead of only instructing the agents in it. SPEC.md §17
                    question 2 carries the argument; `Seating` holds the code.

                    A person joins by typing a name and leaves by pressing Leave. **An
                    earlier draft of this file required the name to come from the command
                    line and forbade a form. That was wrong, and the reasoning it rested on
                    is worth keeping because it is nearly right.** §13 does rest on a sender
                    being unable to forge the agent it claims to be — but §16 already
                    concedes that anything able to reach a broker can claim to be any agent,
                    and whoever can reach this console can already retire and purge every
                    agent in the deployment through §10.2. A login form is a lock on a door
                    standing in open ground, and pretending otherwise would have bought
                    nothing but a worse console.

                    What the seat genuinely adds is the ability to *speak as* somebody, which
                    no operator operation has ever granted, and a transcript recording words
                    an agent never said is a different kind of wrong from an operator visibly
                    instructing it. **So that is the one thing guarded: `SeatBook` refuses a
                    name the registry holds for anything whose adapter is not
                    `parley-console`, and refuses outright when the registry could not be
                    read** — a guard that skipped the check on an unreachable broker would
                    pass hardest exactly when the deployment is least well known.

                    The seat is an ordinary attachment, which is the part worth not losing.
                    §4.0 makes an open connection the evidence an agent is reachable, so
                    joining and leaving are the same events every other agent's arrival and
                    departure are — not a session flag the console keeps to itself. It
                    follows that a seat must not outlive the person: a tab left open is not
                    somebody at a desk, so `SeatBook.IdleAfter` releases one that nothing has
                    touched, and the frame touches it on every page render.

                    Without a seat the console has no agent identity at all and the composer
                    sends instructions, which is what it did before any of this.
src/Parley.Server   The broker process of SPEC.md §4: the only thing that opens the
                    database. Joins Parley.Broker to Parley.Transport, and exists so
                    that Parley.Broker does not have to learn about either.
tests/Parley.Core.Tests   Domain tests. Deliberately does NOT reference Parley.Store —
                          the absent dependency is the point. Keep it that way.
tests/Parley.Store.Tests  Repository and schema-constraint tests.
tests/Parley.Broker.Tests Lifecycle, the agent operations, and the surface boundary
                          below.
tests/Parley.Server.Tests Whole exchanges over a real loopback socket, and the adversarial
                          probes. Nothing in it touches a Broker or a SqliteConnection.
tests/Parley.Client.Tests What both surfaces share: seating's guards, the transcript and
                          visibility derivations, and a graph walk asserting no route to a store
                          from either this or the console. Deliberately does NOT reference
                          Microsoft.AspNetCore.Mvc.Testing — a web host reachable from here is how
                          a test of client behaviour quietly becomes a test of the console again.
tests/Parley.Web.Tests    The console: the visibility derivation, what it refuses to render
                          raw, and that its project file references nothing that could open
                          a store.
tests/Parley.Spec.Tests   Checks SPEC.md: references resolve, tables are well formed, and
                          the schema it documents executes. References neither Core nor
                          Store, so a check cannot drift into asserting what the code does.
```

`plugins/parley` is the Claude Code integration and `.claude-plugin/marketplace.json` is the
catalogue pointing at it, so the integration versions with the protocol it configures. It ships
no MCP server configuration on purpose: Claude Code keeps a plugin's configuration in user
scope, so a bundled server would hand every repository the same `PARLEY_AGENT` — and taking the
agent's name from the launch environment is the guarantee the whole trust model rests on. An
integration for another client belongs beside it, not inside `src`.

`Parley.Core` having no I/O and no package dependencies is a design constraint, not an
accident of the current code. A `using` that pulls I/O into Core is a review finding.

**Agent operations and operator operations live on different types.** SPEC.md §10.2 warns
that an adapter surfacing an operator operation into an agent's tool set would breach §10.1,
and the protection the document offers is a naming convention — `force_conclude` is
deliberately not `conclude`. That is thin: it depends on whoever writes the adapter reading
carefully. So the split is made twice, structurally.

Inside the process, an adapter is handed a `Broker`, and a `Broker` has no operator operation
to bind. `SurfaceBoundaryTests` enumerates the broker's public surface and fails when it grows,
which makes adding an operation a decision about which side it belongs on rather than a
question of which class the connection was already in.

On the wire, they are separate gRPC services. An adapter binds `Agents`; `Agents` has no method
that retires anybody, and `Operators` is a different service on a different client. That matters
more than the type split now, because the type split protects a process that no longer holds
the operator's console at all -- `Parley.Mcp` does not reference `Parley.Broker`.

## Build and test

```
dotnet build
dotnet test
```

Tests run on Microsoft.Testing.Platform, selected in `global.json` under a `"test"` section
beside `"sdk"`. A green build is the whole lint gate: warnings are errors, documentation is
enforced, and NuGetAudit fails on high or critical advisories, so there is no separate lint
step. CI runs on Windows and Linux. It used to include macOS, justified by the transport
claiming parity across named pipes and Unix domain sockets — a claim nothing in this repository
makes: three processes hold gRPC connections to each other, and sockets, keep-alives and
graceful shutdown are where platforms differ. The macOS leg was deferred while that claim was
still hypothetical and is back in the matrix now that it is not.

## Versioning

**Bump `VersionPrefix` in `Directory.Build.props` in the same change that alters shipped
behaviour.** Not afterwards, not at release time — in the commit, so the version and the code
it names travel together. Semantic versioning, and while the major is zero the minor moves on
a breaking change and the patch on everything else.

Applies to a change in `src`. Documentation, tests, CI and the plugin's prose do not ship
behaviour and do not move the number on their own.

The reason is mechanical rather than ceremonial. `dotnet tool update` compares versions, so a
rebuilt tool at a version already on PATH is *not installed* — the command reports success at
some length and replaces nothing. Every change that leaves the number alone is therefore a
change that cannot be delivered by the documented upgrade path, and the failure is silent on
the machine that most needs it: the one already running the old build. Uninstall-then-install
is the workaround, and it is a workaround for this.

Nothing enforces this. It is the one convention in this file that the build cannot catch, so
it is written here rather than left to the comment in `Directory.Build.props` that only
somebody already editing the version would read.

## Conventions the build enforces

Breaking any of these fails the build. That is intentional: a rule the compiler cannot
enforce is a rule that decays.

| Rule | Mechanism |
|---|---|
| Every public member documented | `GenerateDocumentationFile` + `TreatWarningsAsErrors`, so CS1591 is an error |
| No culture-sensitive string comparison | CA1305/CA1307/CA1310 promoted to error in `.editorconfig` |
| `ConfigureAwait(false)` in library code | CA2007 as error |
| Parameterised SQL | CA2100 as error. One suppression exists, in `SchemaMigrator.Migrate`, with a written justification. Do not add a second without one. |
| Package versions centralised | CPM; a `Version=` attribute in a csproj is a restore error |
| High/critical advisories fatal | `NuGetAudit` with NU1903/NU1904 as errors; NU1901/NU1902/NU1905 stay warnings |

Low and moderate advisories are deliberately non-fatal: a new advisory can land against a
package with no patched version yet, and if every severity blocked all work the predictable
response is that someone switches auditing off entirely.

**Documentation style:** `<remarks>` carries *why*, never a restatement of the signature.
The reasoning behind a non-obvious choice is the part that cannot be recovered by reading the
code, so it lives next to the code. Match this.

**Transitive pins:** every entry in the `Transitive pins` group of
`Directory.Packages.props` must record why it exists and when it can be removed, in the
format documented in that file. Remove a pin once the parent package's own floor has caught
up; `dotnet nuget why` shows whether it still binds. A pin with no justification becomes
permanent by default.

## Environment gotchas

Each of these cost a build cycle. Do not rediscover them.

- **A double hyphen cannot appear inside an XML comment.** A CLI flag written into a
  `.props` comment makes MSBuild reject the entire file, with an error that does not say why.
  Put CLI flags in markdown, not in `.props`/`.csproj` comments.
- **Test runner selection lives in `global.json`**, under `"test"` beside `"sdk"`. The
  `dotnet.config` INI format was dropped in .NET 10 RC2. MTP mode is all-or-nothing: every
  test project must be MTP-based.
- **xUnit v3 test projects need `<OutputType>Exe</OutputType>`.** Without it they build
  cleanly and discover zero tests.
- **`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` must stay absent.** Both are
  VSTest-era, and the .NET 10 SDK refuses to run MTP tests through the VSTest target.
  xunit.v3 hosts its own runner and needs neither.
- **IDE0005 only runs when `GenerateDocumentationFile` is true**, which is why the test
  projects generate docs and suppress CS1591 rather than turning generation off.
- **xUnit1051** requires `TestContext.Current.CancellationToken` passed explicitly to every
  awaited call that accepts one.
- **`.slnx` solution folders need a real name**, not `/`. `<Folder Name="/">` throws an
  unhelpful `ArgumentOutOfRangeException`.
- **Package deprecation produces no build warning at all.** The CI `dependency health` job
  exists because the xUnit v2 deprecation was only caught by looking for it.
- **`dotnet test` can report `Zero tests ran` on a machine where the tests are fine.** Every
  project returns exit code 5 in a fraction of a second, `total: 0`, no failure named. The
  suite is not empty and nothing is misconfigured: running the host executables directly,
  `tests/Parley.*.Tests/bin/*/net10.0/Parley.*.Tests.exe`, discovers and passes all of them,
  and CI runs the identical command and reports 859 passing on all three platforms. Seen so
  far only on one Windows machine, on SDK 10.0.301 and 10.0.400 alike, in both Debug and
  Release, from both PowerShell and Git Bash — so it is not the SDK feature band, the
  configuration, or the shell, and the cause is not yet known. `dotnet test` launches each
  host with `--server dotnettestcli --dotnet-test-pipe <name>`; the host's own diagnostic log
  (`TESTINGPLATFORM_DIAGNOSTIC=1`) stops mid-startup, and since MTP logs asynchronously a
  truncated log means the process went away rather than that it idled.

  **Run the executables before concluding anything about the tests.** A toolchain that
  discovers nothing and a suite that contains nothing produce the same summary, and the second
  reading is the one that gets acted on. This is the same trap as reading the dependency graph
  before restoring, and as `gh pr checks` returning the previous run: a green-looking answer to
  a question that was never asked.

## Review gate

Two parts: a mechanical floor that runs automatically, and three human-judgement lenses that
do not.

### The floor runs as tests

`tests/Parley.Spec.Tests` checks SPEC.md the way the other projects check the code: every
section reference resolves, no markdown table is ragged, no operation name spans the §10.1 /
§10.2 boundary, the rules of §8 are numbered contiguously and all appear in its enforcement
table, and **every schema fragment in the document executes against SQLite, in the order
written**. `dotnet test` runs them; CI runs them on all three platforms.

That last one is the valuable one. §9.3 argues that invariants belong in storage because
application code can route around them, and each of those arguments is worth exactly what the
DDL carrying it is worth. Do not hand-verify a constraint and move on — add the case, so the
next edit is checked too.

Two traps this suite has already hit, both of which made checks pass while testing nothing:

- **Line endings.** SPEC.md is CRLF on Windows and LF on the Linux runner, and
  .NET anchors `$` before a bare line feed only. `SpecDocument` normalises on load. A pattern
  anchored to end-of-line otherwise matches everything on two runners and nothing on the third.
- **Trigger bodies contain semicolons.** A pattern ending at the first `;` truncates a trigger
  into something that still parses as SQL. Triggers are matched to `END;` separately, and
  `The_document_contains_a_schema` guards the extraction itself, because a regex that silently
  matches nothing makes every other check pass by vacuity.

### The lenses are for what the floor cannot check

Each finding nothing above low severity:

1. Correctness and concurrency
2. Security and data integrity
3. API surface and maintainability

Rotating the lens matters — three runs of the same review are correlated and mostly re-derive
the first. After fixing findings, **re-run the same lens** before moving to the next; fixes
have introduced new problems that only the re-run caught.

**The gate is three consecutive runs per lens with no finding above low. A finding at any
severity above low resets that lens to zero.** The lenses are independent and all three must
reach three. Nine clean runs is the floor, not the expectation — a branch that keeps finding
things keeps paying.

Each run must probe **something the previous runs did not**. Three re-readings of the same code
are one run repeated, and the counter is not a formality to be satisfied by rephrasing.

An earlier version of this file asked instead for a *rotation to convergence* — passes repeated
until one full rotation found nothing. **That is a weaker bar and it was measured to be one.**
On `004-terminal-surface`, five rotation rounds reported convergence; the counter's first six
runs then found four more defects, including a seat that held its gRPC call open after giving
up and a truncated log that stopped being followed while the screen still said *Following …*.
The rotation's fifth round had been clean because it re-read what the fourth had already
looked at. Consecutive-clean-per-lens is what forces a new probe each time.

The failure both versions correct is specific and was repeated: running each lens once, fixing
what it found, verifying the build still passes, and reporting green. A build that passes is not
a review. The verification sweep at the end confirms the fixes did not break anything; it does
not look for what the fixes introduced.

**The running count lives in `REVIEW-LEDGER.md`, which is untracked** — it is the state of one
person's review of one checkout, not a fact about the protocol, and it spans sessions and
machines, so it is written down rather than remembered. Open a section per branch, keep a
counter table, and log every run with what it probed and what it found. **Log the clean runs
too**: a run recorded only when it finds something turns the ledger into a bug list, and the
next reader cannot tell an area that was probed and held from one nobody looked at.

Record findings that are real but below the bar as *low, recorded not acted on*, with the
reasoning. They do not reset a lens; leaving them unwritten is how the same one gets
rediscovered every branch.

The highest-value findings in this repository's history came from adversarially probing a
guard rather than reading it: a self-referral that asserted both halves of a contradiction, a
gate cycle that no sub-question could ever resolve, a forged `parley:broker` responder, an
agent able to write `ghosted` over its own answered exchanges. None were visible by
inspection; all were found by writing the malicious case and watching it be admitted. **Probe;
do not reason.**

### Two rules about running them

**Run the lenses after the last edit, not alongside it.** Reviewing while authoring and then
continuing to change things means reporting passes that never covered the final state. This
has been caught by the user three separate times.

**Verify CI ran against `HEAD`.** `gh pr checks --watch` returns the *previous* run when a new
one has not yet registered, which reports green for a commit that was never built — the same
shape as reading the dependency graph before restoring. Compare the run's commit against
`git rev-parse HEAD`.

## Answering as an agent

A working copy of this repository can register itself with Parley like any other, and doing so
is worth the trouble: it is the only way the protocol gets used the way its users use it. The
registration is a local fact -- `.mcp.json` is gitignored here, for the reasons given in that
file's entry -- so nothing about a particular deployment belongs in this file.

What does belong is the standing instruction. When a question arrives asking how Parley behaves,
**answer from `SPEC.md`, and quote the section**. The spec is the primary artifact and the code
is a reference implementation of it, so an answer derived by reading the implementation is
evidence about this build rather than about the protocol, and the asker cannot tell the
difference from the other side of a message.

If the two disagree, that is a defect in one of them and which one is a decision, not an
assumption. Say so in the answer rather than picking the convenient half: a peer acting on
"the code does X" when the spec says Y will be wrong the moment the defect is fixed the other
way. `unknown` is a legitimate answer and a cheap one; a confident wrong ruling about the
protocol propagates into every repository that asked.

## Surveying the peers

SPEC.md §17 question 1 leaves Notify uncapped on purpose and decides the question from evidence.
Half that evidence is counted — `parley notifications` reports volume, size and what each agent
has received, marking anyone past ten. The other half cannot be counted at all: whether any of it
was worth reading. Only the agent receiving it knows, so somebody has to ask.

**The person runs the report; this agent does the asking.** That division is not ceremony. The
report reads across every pair in the deployment, which is exactly the disclosure §10.1 withholds
from an agent — so this agent must not run `parley notifications`, `parley referrals` or any
other operator command, and does not need to. It is told which peers are due and asks them
through Parley like any other question.

Ask it as a real question, because it is one, and rule 8.1 applies here as much as anywhere:

> *Intent:* deciding whether Parley should bound how many notifications one agent may send.
> *q1:* Of the notifications you have received, roughly how many were worth reading?
> *Consequences:* if most were useful I will argue against a cap and record that; if most were
> noise I will propose a per-sender limit and cite your answer; if you cannot tell, that is
> evidence the volume is already past what is readable, and I will say so.

Ask at ten, then again at twenty and thirty. The same agent's answer changing as the count rises
is the finding — a stream that was useful at ten and noise at thirty says the limit is somewhere
between, which no counter can tell you.

Record what comes back in §17 question 1 rather than acting on it. One agent's opinion is not a
protocol change, and the question is the durable place for evidence to accumulate.

## Repository hygiene

This repository is public and its git history goes with it. No trace of the originating
private project may appear in any commit, in code, comments, tests, or fixtures. Placeholder
agents are `orders`, `billing`, and `inventory` throughout.

Before committing, run a case-insensitive `git grep` for the originating project's names,
excluding `.gitignore`. The literal term list is deliberately not written here: this file is
committed, so inlining the terms would make it fail its own check. Keep the list out of
tracked files.

Squash-merge only, so the **PR title becomes the commit message** on `main`. Conventional
Commits. Design rationale goes in the PR body — that is the durable record.

## Standing constraints

These are the premises. A change that contradicts one is a design change, not an
implementation detail, and needs to be argued rather than assumed.

- **The protocol is the product; the transport is disposable.** If a platform ships native
  cross-session messaging, Parley should run over it rather than compete with it. Keep the
  transport behind an interface.
- **Terseness is a security control.** The message cap is a context-poisoning defence, not a
  style preference. It lives in the schema so no code path can route around it, which is why
  moving it is a table rebuild rather than a constant — see `003_message_cap.sql`, and note the
  ordering hazards written down in it. The number is 8,000, and is the first value here set
  from `parley refusals` rather than from judgement: SPEC.md §17 question 16 carries the
  evidence. It stays provisional on the same terms. Move it on that record, not because a
  message did not fit.
- **No agent has authority over another.** There is no orchestrator and no lead. Any design
  that introduces one contradicts the premise.
- **Credentials are references, never values.** `env:`, `keychain:`, `file:`. The broker
  resolves them at invocation and never persists or logs them. This is what makes the
  manifest safe to commit.

<!-- SPECKIT START -->
Design work for a protocol change runs through Spec Kit: `/speckit-specify` writes
`specs/<n>-<slug>/spec.md`, `/speckit-plan` writes `plan.md` beside it, `/speckit-tasks`
writes `tasks.md`. Read the plan for the feature being worked on before implementing it.
The constitution in `.specify/memory/constitution.md` restates the standing constraints
below in the form Spec Kit's checks read; this file remains the authority when they differ.

- **Active plan**: `specs/006-three-view-operator/` — three drill-down views (projects, one project,
  one conversation) replacing 005's four regions on one screen, on **both** surfaces; and the four
  operator operations the protocol did not have: `archive`, `unarchive` and `destroy` for a
  conversation, and `start` for a catalogued agent. The protocol work landed before the views,
  because the surfaces are clients of it. 0.18.2 — the minor moved to 0.18.0 because §10.2 is a
  versioned contract and four new operations change what a V1 broker must implement.

  **Both patches are about the broker's log, and both were found by reading a real one.** 0.18.1
  is the line it prints before it opens anything, naming its build, the commit it came from, the
  protocol it speaks and the schema it expects: a log recording a store it could not open, or a
  port it could not bind, said nothing about which build said it — and the version on PATH was the
  fact that had just been wrong. 0.18.2 gives the console the log floor `ParleyServer` has always
  had. That asymmetry was invisible while they were two processes and obvious once
  `serve --with-console` made them one writing a single `--log`: three lines about an implicit
  handler method per page render, between every pair of lines somebody started the broker to read.
  `Logging__LogLevel__Default=Information` puts them back, which is a floor rather than silence.

  **`plan.md` Q1 was right, and T039 is what says so rather than the argument.** A node joins
  declaring a role nothing has ever registered, an operator asks for a start, the node receives the
  command — and the registry is still empty afterwards, which is the half that would make the
  operation a forgery if it were false. The test was written with its stop rule attached: had it
  failed, 005's amendment went back and nobody added a registration path to rescue it.

  Three things learned building it, each written where it happened:

  - **Reading a region's selection from another view is a defect that appeared three times.** The
    conversation-opening handler, the archive action and the destroy action all read
    `Projects.Selected` for the project to act on — which is off screen in every case that matters
    and null in any fixture that never filled it. `Shell.Current` reads the view's own project. One
    helper now.
  - **The transcript and the export are different code, and the reason is a forgery.** The export
    flattens every payload onto one line because a text file has no defence against a payload
    carrying a newline and then a line shaped like an annotation. On screen the whole point is that
    the breaks survive, so the defence is the indent instead: a header sits at column zero and
    nothing else does. That also satisfies FR-018, since it survives colour being stripped.
  - **A test that drove the thing under test could not see that nothing else drove it.** Eighteen
    terminal tests failed on the rearrangement and they were not all one kind: some were guards
    that had to keep their property through a new route, and some were descriptions of the design
    the owner rejected. `All_four_regions_are_on_screen_at_once` was the second kind and was
    replaced rather than carried.

  One test could not be driven and says so rather than being made to pass: 005's mouse route
  clicked the middle of a pane nineteen rows tall, and a project view gives it the full height. It
  is split into the two halves that can be checked, with the gap written into the test.

  The owner ran 005's built terminal and said the four regions were "confusing and too busy". That is
  a verdict on its central design decision from the only test that could produce it, so the
  arrangement is replaced rather than tuned. Everything under it is kept: seating's guards, the
  enrolment writer, the withdrawal doors with a name typed back, the containment of a broker that
  does not answer.

  **`plan.md` Q1 reverses 005's amendment, and 005's record is kept rather than edited.** 005 refused
  to promise a newly added agent would be startable, on the grounds that a surface registering on a
  repository's behalf is a caller-supplied identity. That reasoning is right about the thing it
  examined and answers a different question. §12.3's closing paragraph already routes spawn-on-demand
  through §12's manifest — the node catalogue — and `NodesService.RequestStart`'s only guard is that
  the node's catalogue names the role, with **no registry lookup on that path at all**. So the new
  `start` operation asks the node to launch the role once and the adapter registers the agent it
  attaches, which is exactly what §12.3 permits. Nothing bends. `research.md` R1.

  Two things the spec got wrong and `research.md` corrects rather than implements: colour is derived
  from an agent's name and is the same everywhere (`Palette` already argues why position-derived
  colour is actively misleading), not per conversation; and a conversation's "name" is its
  identifier, which `001_initial.sql:342` already records — no naming scheme is added.

  Archived is a nullable column, not a fourth `state` value, because `state` is the closure lifecycle
  and a conversation may be both closed and archived. An archived conversation that receives a
  message **stays archived**: the alternative lets an agent reverse an operator's decision, which is
  the one direction of authority Principle II does not have.

- Previous: `specs/005-tabbed-terminal-agents/` — the terminal rewritten on Terminal.Gui v2
  (the owner's choice, recorded under the spec's "Decisions already made") so a person sees
  projects, agents, conversations and a transcript at once and moves between them with Tab; and
  agents added to a project, retired, reinstated or purged from it, each destructive act behind a
  description and the agent's name typed back. `Parley.Client` did not move. Started from `main` at
  `c85a271`; 0.17.0. **Built: Phases 1 to 7, with the review gate outstanding.** Its "Q1 settled"
  records the one place the spec asked for what the protocol cannot give — an added agent listed as
  `startable` before anything has registered it — and the amendment that replaced it: the agents
  region shows the project's catalogue beside the registry, and the node rejoins with new roles
  when its catalogue changes.

  Four findings from building it, each written where it happened:

  - **Terminal.Gui 2.4.17 has no headless driver and no supported way to supply one.**
    `ApplicationImpl` is internal and `DriverRegistry.Register` records a factory that
    `CreateDriver` never calls — it switches on the name against three literals. What works is the
    ANSI driver initialised and never pumped, with the screen size set rather than queried;
    `HarnessTests` asserts every line of that recipe, including that a driven session writes zero
    bytes to stdout and leaves stdin unread. `research.md` R2 carries it.
  - **A node announced its roles once.** A role added to a running node's catalogue was announced
    never, so an agent enrolled from the terminal read as `startable` and could not be started —
    §17 question 13's case, reached by a route nobody had noticed. `parley node` now compares its
    roles every watch interval and rejoins.
  - **The toolkit's tab strip truncates.** Four region titles across forty columns became
    `Conversat`, silently, which is what the narrow-terminal rule forbids by name. Stacked mode
    shows one region at a time instead.
  - **`Safe` defends against a different hazard now.** There is no markup to escape; what is left
    is control characters in a payload, a project description or a log line, and they are replaced
    one for one rather than dropped — because a name typed back is what authorises a purge.

  Three more the review gate found, none of them visible by reading:

  - **A withdrawal deleted a launch configuration belonging to a different agent.** The workspace
    comes from the registry, which is what the agent said about itself and may be long out of date,
    so a repository re-enrolled under a second name has one `.mcp.json` and two rows pointing at it.
    Withdrawing the first row removed the second agent's `PARLEY_AGENT` and reported success. The
    name is read back before the entry goes now. **A test named
    `Withdrawing_one_agent_leaves_another_agents_launch_configuration_alone` had passed throughout,
    because it put the two agents in two workspaces** — a test named for a property it does not
    check is worse than no test.
  - **The terminal's work queue was not one queue.** `_work = _work.ContinueWith(...)` is three
    steps on a plain field; sixty-four callers at once overlapped twenty-three times and left
    forty-five outside the task `Settled` hands back. Every call site today is on the main loop, so
    it was latent — but `Settled` is what makes every driven test in the suite deterministic, and a
    guarantee that holds only because of where its callers happen to sit is one nothing enforces.
    The chain is extended and read under a lock.
  - **The two surfaces had drifted and no contract said so.** The console binds `Refusals`,
    `Clarifications`, `Referrals` and `Notifications`; the terminal binds none, which made 004's
    promise that absences are *listed so their absence is deliberate* untrue for four of them.
    Written down in 005's `contracts/operations.md`, and `DriftTests` now compares every operation
    on `BrokerClient` against both trees so the next one-sided one fails the build.

- Previous: `specs/004-terminal-surface/` — merged 2026-09-03 as `c85a271`, 0.16.9, gate met on
  all three lenses. What use added after its plan is recorded at the end of its `spec.md`:
  pre-filled, validated naming; the manifest-serving autostart entry with a node per catalogued
  project; the broker watching the manifest; `project set` and `remove --destroy`; destroying a
  project from the terminal. `parley top`, a terminal application a person
  takes part from, because SPEC.md §17 question 15's editor-window agent is the one thing
  nothing can hand a turn to. `plan.md` settles the question the spec raised and did not
  assume: **the transport-facing half of `Parley.Web` moves to a new `src/Parley.Client`, and
  both surfaces read it.** That is a move rather than a rewrite — of the fifteen types outside
  the Razor pages, exactly one (`SeatCookie`) touches HTTP — and it costs no graph, because
  `Parley.Client` takes the same two references `Parley.Web` has today. Two surfaces that
  implemented seating and transcripts separately would disagree about who was in a conversation,
  which SC-003 forbids and which this repository shipped twice in one week. `research.md` R2
  records why both surfaces keep one `Seat.Adapter`, and what that makes ordinary: a person
  seated in a browser and a terminal at once is two attachments for one name, which is the case
  `connected = 1` got wrong until #77.
- Previous: `specs/003-parley-web-console/` — a read-only operator web console, merged. Its
  gate was discharged: `ListConversations` and `Export` were specified before being built, and
  004 is their second reader rather than a reason to add a third.
- Previous: `specs/002-project-manifest/` — a per-machine manifest of named projects, merged.
  Its obligation was discharged: SPEC.md §4.1 now illustrates `projects.json` rather than
  `parley.yaml`. `tasks.md` carries the review record, including what five rounds turned up.
- Previous: `specs/001-agent-routing-hints/`.
<!-- SPECKIT END -->
