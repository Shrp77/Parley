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
                    Exchange, Message, Agent, Conversation, Instruction, SharedFile and
                    FileNotice.
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
                    The console reads it to render a page and `parley terminal` reads it to draw a
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
                    one act, and its ResultAsync overload settles the seat when the broker says
                    the exchange closed. Two surfaces each writing that themselves is how they
                    come to disagree about what a person has seen.

src/Parley.Setup    What a person does to a machine so Parley can run on it: enrolling a
                    repository as an agent, naming projects, resolving where the per-machine files
                    live, and installing or reading the autostart entry. References
                    Parley.Configuration and Parley.Transport.

                    A project of its own for the reason Parley.Configuration is one: two programs
                    do this and cannot otherwise share one implementation. `parley` does it from a
                    shell and `parley-terminal` from a screen, and the terminal does not reimplement
                    it — it runs the same commands in process and prints what they said, so a
                    machine configured from a screen is configured exactly as one configured from
                    a shell.

                    Not folded into Parley.Configuration, which it otherwise resembles, because
                    Parley.Mcp references that one: an agent's own server would then carry the
                    code that writes its launch configuration. Harmless at runtime and wrong as a
                    surface.

                    The transport is here because destroying or listing a project asks its broker
                    whether it is reachable, and what it holds, before touching anything. That is
                    a client of a broker; it is not a route to a store.

                    **There is no store here and there must never be.** GraphTests walks it.

src/Parley.Terminal The terminal operator surface, and its own tool — `parley-terminal` — rather than a
                    subcommand of the broker's. References Parley.Client, Parley.Configuration,
                    Parley.Setup and Parley.Transport.

                    **Windows cannot replace a running executable.** While the terminal lived
                    inside `parley`, every terminal change required stopping the broker, and
                    SPEC.md §12 says what that costs: the node stops, tells every agent it started
                    to quit, and the process is what held everything each had learned. The
                    churniest component in the repository was forcing the most expensive restart —
                    29 of the source files changed on main since c85a271 were the terminal, 1 was
                    the broker.

                    `parley terminal` forwards here, passing the raw command line minus
                    the verb and parsing nothing. One parser per tool rather than one and an
                    imitation of it. When the tool is absent it names it and how to install it,
                    rather than "unknown command", which would be untrue.

                    **There is no store here and there must never be**, checked by the same
                    transitive walk Parley.Client and Parley.Web are held to. That walk replaced
                    the one that found the leak: while the terminal had no project of its own,
                    `GraphTests` could say nothing about it at all, so its store-freedom was an
                    intention rather than a check.

                    `BuildIdentity` here is its own, not the broker's: this opens no store, so a
                    schema number would be a claim about somebody else's build — and the type that
                    reads one references the assembly that owns the store.

src/Parley.WebUi    The web UI as its own tool -- `parley-web-ui` -- and nothing more than a way
                    to start it. Everything it does lives in Parley.Web's ConsoleHost, because two
                    callers each building a host is two hosts that differ the first time one of
                    them is edited.

                    **It takes ASP.NET Core off the broker's tool.** Parley.Cli referenced
                    Parley.Web so that a broker could host the web UI in its own process, which put
                    Kestrel and Razor on every machine that installed a broker -- including the
                    ones that only run agents, which is most of them. The broker starts this as a
                    child process now and knows one thing about it: its command name.

                    `serve --with-web-ui` starts it, does not wait on it, does not restart it, and
                    ends it when the broker ends. A web UI that cannot start -- a held port, a tool
                    not installed -- leaves the broker serving and says so, in a block that still
                    names `parley terminal`. Suppressing the block on that path would take the terminal
                    line with it, on exactly the run where somebody needs to be told there is
                    another way in.

src/Parley.Web      The operator's console of SPEC.md §10.2, and a client of the broker
                    rather than part of it. References Transport and Configuration and
                    nothing else: there is no SQLite on its graph and there must never be,
                    because §4 gives the store to one process. Not inside Parley.Server for
                    the same reason.

                    `parley-web-ui` starts it, as a child process the broker launches and does not
                    wait on -- Parley.Cli stopped referencing this in 007 step 3, which is what took
                    Kestrel's page-serving half off the machines that only run agents. `parley terminal` shows the same deployment in a terminal, built
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
                    and, since 0.26.0 made every operator operation available on every surface,
                    `ForceConclude`, `Prune` and `Instruct` each in exactly one file carrying its
                    door. A helper whose *name*
                    merely contains one of those words fails it, which has already happened once
                    and is the point.
 That was decided deliberately and against the argument
                    recorded here before it: a second tool would have kept ASP.NET Core off
                    the machines that only run an agent, and one command is worth more than
                    that saving.

                    **`serve --with-web-ui` starts it as a child process, and `autostart
                    install` registers one by default.** This paragraph described
                    `serve --with-console` hosting it *inside* the broker's process, with
                    Kestrel sharing a process with the thing that owns the database — which
                    stopped being true in 007 step 3 and stopped being spellable at 0.22.0,
                    and is corrected here rather than deleted because the trade it describes
                    is the one that was reversed. The containment argument survives intact and
                    is the reason the child process is not waited on: a web UI that cannot
                    bind reports it and the broker keeps serving, because a web page must
                    never cost a deployment. On by default because of SPEC.md §17 question
                    15 — an agent attached through somebody's editor window is the one thing
                    nothing can hand a turn to, so a surface remembered separately is the half
                    of a deployment that works for agents and not for people.

                    The project stays separate anyway — it is what keeps the
                    store off the console's dependency graph, which is the constraint that
                    actually matters, and `ConsoleHost` is the single place the host is
                    built so `parley-web-ui` and `dotnet run` cannot drift apart.

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
tests/Parley.Terminal.Tests The terminal. Driving, Walking and Drawn moved here with it; Loopback,
                          LoopbackOperator, ToolProcess and TestMachine are linked from
                          Parley.Cli.Tests rather than copied, because an executable referencing an
                          executable is a shape MTP does not run. It references Parley.Cli for the
                          one test that is genuinely about both tools — a project created from the
                          terminal whose broker comes up, and destroyed there and gone.

                          **`TerminalProcess` runs the tool out of its own build directory and must.**
                          A copy of `parley-terminal` lands beside these tests, and it does not work:
                          this project also references Parley.Cli, which brings a framework
                          reference to ASP.NET Core, so several Microsoft.Extensions.Configuration
                          assemblies resolve from the shared framework and are left out of the
                          output — while the terminal's own runtime configuration names only the
                          base framework, and Terminal.Gui's module initializer scans every loaded
                          assembly on start-up. The co-located copy dies with a
                          TypeInitializationException before its first line. Nothing ships that
                          arrangement; it is written down because the failure looks exactly like a
                          broken tool.

tests/Parley.WebUi.Tests  The web UI tool. Small, and it exists for one reason: Razor Pages are
                          found by walking the entry assembly, so the failure worth catching only
                          happens when the entry assembly is something other than Parley.Web. These
                          tests lived in Parley.Cli.Tests, which was exactly that until the broker's
                          tool stopped referencing the web stack at all.

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
assumption. Say so in the Result rather than picking the convenient half: a peer acting on
"the code does X" when the spec says Y will be wrong the moment the defect is fixed the other
way. When the spec does not settle it, report the item `blocked` with `missing_information`
and say exactly what is unsettled; a confident wrong ruling about the protocol propagates into
every repository that asked.

## Surveying the peers

SPEC.md §17 question 1 leaves request volume unbounded on purpose and decides the question from
evidence. Half that evidence is counted — inbox depth and `invocations` record what each agent
receives and what its turns cost. The other half cannot be counted at all: whether the requests
were worth the work. Only the agent receiving them knows, so somebody has to ask.

**The person runs the reports; this agent does the asking.** That division is not ceremony. The
reports read across every pair in the deployment, which is exactly the disclosure §10.1 withholds
from an agent — so this agent must not run `parley results`, `parley flags` or any other operator
command, and does not need to. It is told which peers are due and sends them a request through
Parley like any other.

Send it as a real request, because it is one, and rule 8.1 applies here as much as anywhere — one
ask, plainly, with what it is for:

> Of the requests you have received through Parley, roughly how many were worth the work, and what
> made the others not? I am asking because SPEC.md §17 question 1 leaves request volume unbounded,
> and whether that should change can only be decided from what receivers say.

Ask again as the volume grows. The same agent's answer changing as the count rises is the finding,
which no counter can tell you.

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

- **Current plan**: `specs/016-request-is-the-ask/` — **0.29.0, protocol 0.7, schema 11. Built, gated on all three
  lenses, walked, installed, and not yet pushed. 0.29.1 and 0.29.2 are what real use found.** A Request is the conversation it belongs to and the ask; `intent`, `items`, item ids,
  work-or-question, gates and `done_when` left the protocol, and a Result answers the exchange whole. The owner's call
  on 2026-09-17, made while composing one in the terminal: the fields were the same thing asked four times, and a
  receiver that cannot tell how far an ask goes has a question — which is what Clarify (§5.2) is. Nothing replaces them.

  **It rested on a path already built**: a Request whose items a Result cannot name was already answered as a whole
  under the item `request` (there for 0.4's carried-forward questions), so a flat `ask` needed no new machinery and
  `result_items`, the flags and the health reports are untouched. **Migration 011 replaces one trigger and rewrites no
  row** — the plan said two, and reading them found `tr_result_is_well_formed` never named an item identifier, so it
  was left exactly as it was. Messages are never updated, so what changed is what may be written next, and every 0.5 and
  0.6 conversation still reads whole. That is FR-009 and the cheapest thing to get wrong.

  **What building it taught that the plan did not know:**

  - **The reader change was one line, and the renderers needed none.** A 0.7 ask slots into the same field a 0.6
    `intent` occupied, and every surface already drew items and a finish line conditionally — so a 0.7 message renders
    with nothing to draw and a 0.6 one renders whole, on all three surfaces, by construction rather than by a branch.
    The tests were written anyway, because "by construction" is a property of today's templates.
  - **`parley_result` lost the argument that made an old exchange answerable, and had to get it back another way.** A
    0.6 exchange is still owed its own item identifiers, nothing times out, and the tool no longer lets a model type
    one. The broker already names what it expected on the refusal, so the adapter answers again with the one answer
    recorded against each identifier — honest for a Request that is now one ask, and invisible to the model.
  - **The terminal's `MessageCapTests` derives the room left for the ask**, which is now nearly the whole cap. Kept
    derived rather than replaced with a literal, for the reason it was derived in the first place.
  - **A pre-existing flake was found and fixed, not caused by this change.** `WakeDuringCollectionTests` counted node
    commands after a fixed 250 ms sleep: one full-suite run in three failed saying nothing had been started, when the
    answer was "not yet". Its neighbour had already worked around exactly this. `CountCommandsAsync` now waits for what
    is owed and then listens for what is not.
  - **`Parley.Server.Tests` may not open a `SqliteConnection`**, which this file says and which a first draft of
    `AnsweringAnOldExchangeTests` broke. It lives in `Parley.Broker.Tests`, where the matching it probes belongs.
  - **Standing in for a pre-migration message needs the trigger stood down, not the row edited afterwards.** A stored
    payload is never rewritten in service — that is the property 011 rests on — so there is no other route to the state
    an upgraded store is actually in, and the two tests that need one say so where they do it.

  **The review gate found three things above low, all in what this change added, and each is now a test.**

  - **The 0.6 retry turned a three-thousand-character answer into a thirty-thousand-character refusal** (lens 1),
    reporting a length the agent never wrote, on an exchange nothing will time out. It is measured before it is
    sent now, and the refusal names what would fit.
  - **And it wrote that length into `size_refusals`** (lens 2), which is what §17 question 16 reads and the one
    value in this protocol set from evidence rather than judgement. Nothing the retry assembles is a message an
    agent composed.
  - **The new trigger admitted an ask of one tab or one no-break space** (lens 3), because SQL's one-argument
    `trim()` removes the space character and nothing else — a storage guard weaker than the code above it, which
    is the wrong way round for a rule §9.3 puts in storage so nothing can route around it. The blank set is spelled
    out now, less U+200B, which is not whitespace by the rule the senders apply.

  Migration 011 was edited rather than followed by a 012, on the one ground that permits it: 0.29.0 is installed
  nowhere, so it had been applied in no store outside a test process.

  **0.29.1: the console could only tell you mail was waiting while you were sitting at it.** Reported by the owner
  within the hour of installing 0.29.0, and it is a defect rather than a missing feature: the inbox said *"You are
  seated nowhere, so nothing can be waiting"* while two Results waited for the name whose seat the upgrade's own
  broker restart had taken. **A seat is how mail is collected, not where it waits** — messages queue against the
  name in the store, and releasing a seat moves none of them. The frame suppressed its count on the same
  reasoning, so the one moment the count exists to serve, having been away while something arrived, was the one
  moment it was withheld.

  The count is asked for whether or not a seat is held, through the Agents service's `Waiting`, which the proto
  already describes as being for a caller that holds no stream: it marks nothing delivered, starts no working
  window and opens no attachment. **The seat mark still needs a live attachment and must** — a dot beside a name
  because a browser remembered one would be the console deciding somebody is present — so the count rides on Join,
  names whose mail it is, and claims nothing about presence. The inbox now tells the three states apart: what is
  waiting, nothing waiting, and the brokers could not be asked, which is the rule the frame's count has been held
  to since it was written. `AwayFromTheSeatTests` drives all three against a broker that answers only `Waiting`;
  five of its six fail against the old pages.

  **0.29.2: the terminal had the same fault, and 0.29.1's reasoning for leaving it alone was wrong.** The owner,
  an hour later: *"where do I go to see what's waiting on me?"* The answer was to take a seat, open a conversation
  — any conversation — and press a key inside the transcript, which reported what it found. Nothing on the screen
  ever said there was anything to find, and what is waiting is frequently in a conversation nobody has opened,
  which is exactly the one somebody would not think to open. 0.29.1 argued the console's fix did not apply here
  because the terminal remembers no name between runs; that is true only until somebody takes a seat, which is the
  whole of the case that matters.

  A held seat asks the broker every `MailEvery` and the count goes on the status line with the key that collects
  it, high enough on the ladder to survive every rung the focus survives — it is the one thing on that line about
  the person rather than the screen. **Taking delivery is offered in the agents pane as well**, where the seat is,
  rather than only inside a transcript. Asked and never accumulated, for the reason `SeatHold` carries a scar
  about: a notice arrives when something lands and never when something is collected, so a count kept from the
  stream only ever climbs. `Held` drops the count in its own setter, because one of the three places that release
  a seat forgot to — and leaving one now redraws, since clearing a number is not the same as taking it off the
  screen.

  **`quickstart.md` was walked on 2026-09-21** against tools packed and installed into a scratch path, with a
  broker on its own store and port, so the live deployment was left alone: 51 checks across its six steps, all
  passed. **The store was built at schema 10 first and holds traffic no 0.29.0 build can write**, so the walk
  performed the upgrade rather than describing it — and the exchange left open under 0.6 was then answered through
  a tool that has no argument for an item identifier, recording `q1:done|w1:done` against the identifiers that
  Request declared. Step 5 was walked with a genuinely old binary: the machine's installed `parley-mcp` 0.28.1
  pointed at the 0.7 broker exited 78 before answering `initialize`, naming both versions and the remedy.
  **Step 1 could not be driven and is recorded rather than claimed**: the terminal refuses a redirected terminal
  by design, and the console resolves the manifest through the platform's known-folder API, so it cannot be
  pointed at a throwaway deployment at all — `parley serve` has `--config` and the console has no equivalent.
  The ledger has each check.

  Also corrected in passing: §18's 0.3 entry named "rule 8.5's cap", which after §8's renumbering pointed at a
  different rule; `ItemOutcome` was left public carrying tool descriptions for a form nobody is shown; and a second
  pre-existing flake went with the first — `WakeDuringCollectionTests` counted node commands after a fixed sleep and
  failed one full run in three. `REVIEW-LEDGER.md` carries every probe, including what the clean runs looked at.
  `research.md` carries ten decisions, `contracts/surfaces.md` what each composer asks for after, and
  `quickstart.md` the walk.

- **Previous plan**: `specs/015-file-store/` — 0.27.0 to 0.28.2, **protocol 0.6**, schema 9, then 10. **0.27.1 built, gated and committed, not yet pushed** (`3cf9e48`, then `1f302bd`); **0.28.0 built and uncommitted, its review gate not yet run.** Files cross
  between agents: an agent places a file from its repository into its conversation's folder in the project's file
  location, the bytes stream through the broker and carry a broker-computed SHA-256, and any agent in the project fetches
  it to disk in its own repository — never into a model's context. The agent that placed a file may replace or remove it;
  the person removes anything, prunes everything, and opens files and folders in place from every surface and five
  `/parley:files-*` plugin commands. A folder watcher re-hashes outside edits, a download holds the file read-only, and
  every agent that fetched a file finds a change notice in its inbox when it changes or goes, waking nothing.

  **It reverses the constitution's "never files"**, argued in `spec.md`. The constitution was amended to **2.0.0** on
  2026-09-14 in its own amendment, before any implementation, after `/speckit-analyze` found changing it inside the
  feature's tasks a blocking conflict; the same bump versions 014's redefinition of Principle II, which shipped as 1.0.0.
  SPEC.md §1, principle 2.2 and the README's opening were brought into line the same day; the rest of SPEC.md follows in
  tasks T002–T007.
  Two things the
  plan found that the spec did not say: opening a file with its registered handler can *execute* it, which the owner closed by accepting only `.md` and `.txt` files (code travels as
  `Name.cs.txt`), with the launcher still refusing anything else, so executable types
  are refused (`research.md` R10); and the read-only hold is enforced by the operating system only on Windows, while every
  platform keeps the no-mixed-file guarantee by hash (R5). Spec Kit was run: `spec.md`, `plan.md`, `research.md`,
  `data-model.md`, `contracts/`, `quickstart.md`.

  **What building it and the review gate found is most of what is worth reading.** Each finding was reproduced before it
  was fixed; the ledger is `REVIEW-LEDGER.md`.

  - **Half of `parley files` was unreachable from a shell.** The tool refuses any argument still starting with a hyphen
    before a command runs, so every option `files` reads, and `waiting --with-files`, was refused on the way in — and the
    waiting hook, reading the refused call as nothing waiting, fell back to two counts and said nothing about notices.
    The in-process command tests could not see it; `FilesCommand.OwnOptions` names what the refusal lets through, and
    the tests that hold it run the real binary. Lens 3 then found the opposite: an option a subcommand has no use for
    was parsed and ignored, so `prune --yes` read like consent and asked anyway. Each subcommand refuses what it does
    not take now.
  - **A rename in a conversation's folder lost its placer, three runs in three.** A move also raises creations and
    changes, and each replaced the pending look that carried the rename. The first fix matched a new file to a missing
    one by hash on live events, which passed — and contradicted SPEC.md §9.5, where matching by hash is the start-up
    scan's rule alone, so a file deleted and copied back is the person's. Replaced by keeping the operating system's
    rename report apart from the pending looks.
  - **A file's name reached another agent's model unmarked.** Up to two hundred and fifty characters the placer
    chooses arrive verbatim in `parley_file_list` and in change notices, where a message's words arrive inside the
    envelope. The tools and the standing instructions now say whose text a name is. Whether a name should carry the
    envelope, or a narrower character set, was left to the owner — who chose the set in 0.28.0, below.
  - **A conversation id the store refuses crashed a placement**, which arrived as `Unknown` — reachable over the wire
    and hidden by the adapter's own check. `FileWork.PlaceAsync` applies the schema's rule first.
  - **Files' times were UTC and unmarked on all three surfaces**, beside consoles whose every other time is local. They
    are written with `LocalTime` now, and the zone is said once per view.
  - **Two tests were wrong before the code was:** the web page tests' scripted broker answered `UNSPECIFIED` where the
    broker says `NONE`, which the client rightly refused, and `MigratedItemIdTests` counted one migration where 009
    makes two.

  Recorded low and not acted on: `FILE_REJECTION_INCOMPLETE` can never be sent, because the header declares no size; the
  size formatter exists once per surface; the terminal opens the Files list off the draw thread, as `ShowHealth` does.
  **`quickstart.md` was walked on 2026-09-15** (T101), against tools packed and installed into a scratch path with a
  broker on its own store and port, so the live deployment was left alone: 45 checks across its seven steps, all
  passed. Steps 3 and 6 were run as another machine, since as written they open an editor and a file browser on the
  desktop. The ledger has each check.

  **Then the installers stopped asking for a store, on the owner's call: projects are the paradigm and the paths had
  not caught up.** `install.ps1` lost `-Store` and `-NodeConfig`, `install.sh` lost `--store`, `--db` and
  `--node-config`, and both always register the entry that serves the manifest. `parley autostart install` serves the
  manifest whether or not it names a project, and `serve --config` with nothing named waits — web UI up, manifest
  watched — instead of falling through to one default store nobody named; the first project named from `parley terminal`
  or with `parley project add` starts within seconds, and the scripts end by saying so. `-Store` over a working
  manifest had also been a way to narrow every project's entry to one. The web UI still cannot name a project, by the
  one-writer rule its naming page records. Found while installing for real: `parley-mcp --version` answered only
  with a launch configuration, so the installer's version check read its silence as a match; it answers now, and
  both scripts count an unreadable version as a failure. `serve --db`, `autostart install --db` and `PARLEY_DB`
  remain, for testing only (below). **These are code edits after the review gate**, so its lenses owe
  confirming runs.

  **`parley top` became `parley terminal`, on the owner's call**: "top" is the name of a process monitor, not of a
  person's view of a deployment. The tool is `parley-terminal` in the package `Parley.Terminal`, and the owner then
  had the internals follow, on consistency being what keeps a codebase maintainable: `src/Parley.Terminal`, the
  `Parley.Terminal` namespace, `TerminalCommand`, `tests/Parley.Terminal.Tests` and `TerminalProcess`, moved with
  `git mv` so their history follows. No alias is kept, since
  nothing was released under the old name, and both installers remove a `Parley.Top` package installed before —
  otherwise nothing ever would. Entries below this one say `parley top`, `Parley.Top` and `TopCommand` because those were the names then.

  **`serve --db`, `autostart install --db` and `PARLEY_DB` are for testing only**, also the owner's call. They run
  one store outside the manifest so a test rig need name no project, which is what the suites use them for; a
  deployment names its stores with `parley project add`. `parley --help`, the README, the operator skill and
  `/parley:setup-many` say so, and the last of those no longer tells anybody to deploy with them.

  **The gate's confirming runs over these edits found nine more, each reproduced before it was fixed.** `serve --config
  <file> --with-web-ui` started a web UI reading this machine's own manifest instead of the one served. `parley serve`
  with nothing named, and `parley enroll apply`'s next steps, still sent people to the one-store options, and `parley
  --help` still said to give `autostart install` a `--node-config`. `parley terminal --version` and `--help` were answered
  by `parley` instead of forwarded. The terminal suggested conversation names beginning `top-`. `serve --db` and
  `PARLEY_DB` were ignored on any machine whose manifest named a project, which no test could see because every test that
  starts a broker by store isolates its manifest — and **the first fix for that let a `PARLEY_DB` left in the environment
  replace the manifest the logon entry serves**, which the next probe caught: the variable never overrides `--config` now.
  And a lone project named while serve waited hosted its node from its entry, where the same machine at its next start
  used `PARLEY_NODE_CONFIG` — which the README and the operator skill did not say either, describing only the
  per-entry rule. The ledger has each probe.

  **0.27.1 is a sweep of every caller that acts on one deployment, on a machine naming several** — asked for by the
  owner after the reinstall's in-flight warning said it could not ask. Every client command already refused to choose
  between several projects when none was named, as documented; the defects were in what calls them.

  - **The waiting hook had been silent in every repository enrolled into a named project.** It read only the `parley`
    key in `.mcp.json`, and enrolment into a project writes `parley-<project>`: on the owner's machine it found an agent
    in none of the twenty-one. It reads every `parley` and `parley-<project>` entry now, asks each by its address or
    its project — the order the adapter resolves them in — and adds the counts, since a repository answering in two
    projects has an inbox in each. **And it could not have spoken even with the right key**: enrolment writes
    `PARLEY_AGENT` as a role and the adapter appends the machine, while `parley waiting` refused a bare role as not an
    agent name. Found only by running the fixed hook against the live repositories — all twenty-one were found and all
    twenty-one refused. `waiting` completes a bare role with this machine now, through the adapter's own
    `MachineName.TryNameHere`, and the hook hands on a `PARLEY_MACHINE` its entry names; commands that act on an agent
    still take the whole name.
  - **`--broker` and `PARLEY_BROKER` were refused on a machine naming several projects**, though they say exactly where
    to connect and the help says explicit flags and variables win. An address given explicitly now satisfies selection
    when no project is named; a project named that the manifest does not describe is still refused.
  - **Both installers asked `parley open` with no project**, so on a machine naming several they always said they
    could not ask what a reinstall would interrupt. They ask each project in turn, and name any that could not be
    asked. Their closing hint names `--project` when several are named.
  - The `/parley:files-*` commands take the project from the repository's own `parley-<project>` entry when the person
    names none; `/parley:setup` says `registry` needs `--project` on such a machine; `/parley:status` recognises the
    per-project key.

  **Then a command's project came to be taken from its context, on the owner's call.** `--project`; then
  `PARLEY_PROJECT`, which the new `parley use <name>` prints the line to set for one terminal, since a process cannot
  change its parent shell's environment; then the repository a command runs in, when the `.mcp.json` at its root
  names exactly one project this machine describes; then the only project. With several and nothing choosing, it
  still refuses and lists them. On a machine naming several, each command says on standard error which project it
  acted on and what chose it. A saved "current project" was offered and declined: chosen in one window, it would
  quietly apply to a purge in another. `RepositoryProjects` finds the repository by its `.git` and reads only that
  `.mcp.json`, so a launch configuration in a home directory cannot choose for a folder that is not a repository; a
  repository naming only projects that live elsewhere falls through, so a machine with one project resolves as it
  did. `ToolProcess` now runs the tool from a directory in no repository, because the suite runs inside this one,
  whose `.mcp.json` on a registered machine names a real project.

  **The review gate was met on the final code, on all three lenses.** Each finding below was reproduced by probe
  before it was fixed; the ledger is `REVIEW-LEDGER.md`.

  - **One project slow to answer silenced the mail in every other project.** Asking once per launch entry made every
    entry's failure the whole run's: a broker that accepts a connection and never answers raised a timeout out of
    `main` and into the hook's blanket `except`, so the entries after it were never asked and the counts already
    gathered went into the traceback with everything else. Every entry is asked independently now, under a timeout
    per question and a budget across all of them. The test that holds it accepts a connection and says nothing,
    because a *refused* connection comes back in a moment and is the shape the existing test already used.
  - **The README, the operator skill and `parley --help` each stated a narrower rule than the code applies**, saying
    the repository chooses when it names exactly one project. What decides is the intersection with this machine's
    manifest, which is what lets a repository enrolled in projects on two machines resolve on each of them — so a
    repository naming one project here and one elsewhere resolves, where all three documents predicted a refusal.
  - **The plugin's own way of checking the hook could only ever answer "nothing waiting".**
    `plugins/parley/README.md` gives a payload carrying no event name, and the script decides everything from the
    event, including whether to speak at all: it printed nothing and never asked a broker, under a sentence saying
    silence means nothing is waiting. The recipe names an event now, and the table row beside it names all four
    events the plugin declares rather than two.

  Two smaller things went with them: the old selection comment left standing above its replacement in `Program.cs`,
  and the clause `--help` was missing about saying which project a command acted on. 3,698 tests pass across every
  suite.

  **0.28.0: a file's name is a set, and a placement is renamed rather than refused; and the web UI follows an open
  conversation.** Both the owner's calls, made on 2026-09-16. Schema 10; **protocol still 0.6**, because 0.6 was never
  released and SPEC.md §18 says so rather than spending a number on it. `spec.md` carries the amendment.

  - **A name is `A`–`Z`, `a`–`z`, `0`–`9`, `.`, `_` and `-`.** The broker replaces anything else with `_`, one character
    for one — a surrogate pair is one — and the reply carries the name asked for beside the name placed. A renamed
    placement whose spelling is already a file's name is refused **whoever placed it, the caller included**, with the
    owner's sentence naming all three names: `a:b.md` and `a?b.md` are both `a_b.md`, and replacing one with the other is a
    rename nobody asked for. Placing under the safe spelling replaces on purpose. A name *looked up* is never renamed,
    since the renamed spelling could be a different file; its refusal offers the spelling instead. The key is now
    SQLite's own `upper(name)`, which retired the combining-mark caveat in §9.5.
  - **A person's file under any other name stays ignored**, and the person's listing counts them —
    `FilesReply.files_not_listed`, on all three surfaces, never the names, which are text nothing checked.
  - **Migration 010 rebuilds `shared_files`**, which nothing references, so the parent-table hazard 006 and 008 record
    does not apply. A row under a name outside the set is dropped, its file left on disk, and each active fetcher told it
    is gone. `FileNameSetMigrationTests` builds a schema-9 store with such rows first, and **found the migration's first
    draft writing a timestamp the store cannot read**: SQL's `%f` is three digits and `ParseTimestamp` takes exactly
    round-trip's seven.
  - **Found while building it, and fixed: a placement destroyed a file the person had just put on disk.** Nothing but
    the watcher looked, and until it had, a placement of the same name committed over the person's bytes and reported
    `Placed`. Reproduced by probe; `FileLocationStore.OnDisk` is asked inside the committing transaction now.
  - **The web UI's transcript follows**, as the terminal's has since 0.25.7, which reverses the `_Layout.cshtml` remark
    recorded there. The page and a new `follow` read render one partial, `_Conversation`; the page polls every three
    seconds, one read at a time, not while hidden, swaps the region only when its digest changed, and does not move a
    reader. **A follow never touches a seat's idle clock** — only the frame does, and a follow returns no frame —
    because a tab left open is not a person at a desk. The script has no test; this project has no browser.
  - **`parley-mcp` is a Claude Code channel**, the owner's call on 2026-09-17: it declares
    `experimental.claude/channel` and sends `notifications/claude/channel` when the broker's pushed count of messages
    and closure polls rises, so an open session collects its mail without anybody typing — the half of §17 question 15
    that concerns a window somebody has open. **Counts and never words**, because the event reaches the model outside
    §13's envelope; file notices never count, because §9.5 wakes nobody. Once per rise: Claude Code queues events that
    arrive during a turn and hands them over together. **The broker posts a notice on arrival and never on a drain**, so
    the count an adapter held only rose and the second arrival was never told — found by the second test written,
    `parley_inbox` now re-reads the counts after collecting and resets the baseline. The first test found the other:
    waiting on `ClientCapabilities` waits for ever on a client that declares none, and `ClientInfo` is the handshake.
    `ChannelNudgeTests` drives the built server over stdio with the SDK's client. **Claude Code acting on the event is
    checked by hand**: custom channels need `claude --dangerously-load-development-channels server:<key>` during the
    research preview, behind a warning dialog, so nothing unattended can start one that way.

  **0.28.1 is two defects in the terminal's add, reported by the owner with a screenshot the first hour 0.28.0 was
  installed.** A repository one project already runs was added to a second; the message line said *"Started 'inventory'
  through 'alpha', which is what starts this repository, and it registered. It can be asked and told now"*, and the
  agents list beside it, redrawn from the second project, did not have it. **The registration check asked the starting
  project about the role the
  repository has there** — which was always yes for a repository that project already ran — rather than asking the
  project added to about the role added. The regression test was run against the old check and fails there, asking the
  wrong project sixty times over the wait. When the name is not seen, the report now also says a session already running
  loads a new launch entry only when it restarts, which is the likeliest reason. **And every line break in the report was
  drawn as `��`**: it was joined with `Environment.NewLine`, and the message line makes every control character visible
  on purpose, since a line break in a broker's text is how 0.25.1's forged second line was made. **Four more screens built
  their reports the same way** — removing a project, withdrawing an agent, enrolling and naming — and the Files dialog
  passed its own layout through `Safe.Text` where `Safe.Lines` keeps it. `Reports.Sentences` is the one join now, and a
  test reads the terminal's sources for `await done(string.Join(`.

  **Both surfaces now say which build they are**, the owner's request in the same hour: the terminal on its status line
  beside every rung that still names the ways out, in the actions menu, and in the window title; the web UI in its footer.
  Each names its own tool (`parley-terminal`, `parley-web-ui`), not the broker, because they are installed apart. Placing
  it found that **a resize crossing no stacking threshold never rewrote the status line**, so a terminal widened after it
  opened kept the rung chosen for its first width; and that the projects view's actions alone are wider than two hundred
  columns, so a build said only on the widest rung was said on no real terminal.

  **The add picker opens in the folder `parley terminal` was started from**, the owner's call, where it opened at the
  home directory. Beside the last repository added still wins once one has been, and home remains for a start folder
  that has gone. `Shell.StartedIn` is read when the shell is built; `parley terminal` forwards without setting a working
  directory, so the tool inherits the shell's.

  **0.28.2: starting a conversation could not be found, reported by the owner with a screenshot of 0.28.1.** They opened the conversations
  menu to start one, found nothing, and reached for Instruct — which is not a message and never becomes a conversation —
  and three things on this surface each made that worse:
  - **The action was hidden without a seat, and nothing said a seat was needed.** `k` is offered either way now: seated,
    it starts one; unseated, it says a request is sent as somebody, asks for a name, and opens the request once seated.
  - **The empty list's sentence was cut at the pane's edge**, at exactly the half saying how to start one. A list row does
    not wrap, so it is four short rows, each held to the narrowest pane.
  - **Undeliverable said the agent's machine was not connected, beside a node that was.** A node gives an instruction only
    to an agent it already runs and reports undeliverable when it runs none — so the owner's agent was merely not
    running. The CLI, the terminal and both web pages each spelled their own sentence; `InstructionWording.Undeliverable`
    names both causes and the remedy for all of them, and **SPEC.md §10.2 was widened to match**, since it named only the
    machine. That was the spec being incomplete rather than the code being wrong: an instruction is a turn, and no
    machine gives a turn to an agent it is not running.
  - And the window title the version went into was overwritten by every dialog: the toolkit writes a modal runnable's
    title to the terminal. `Prompting.Open` clears that from every dialog, and the shell's own title names the build.

- **Previous plan**: `specs/014-work-across-repositories/` — 0.26.0, **protocol 0.5**, schema 8. Parley
  exists to get work done across repositories, and 0.4 said otherwise: every delivery told an agent a
  peer's message could not direct its work, and `out_of_scope` told it to refer rather than do. Agents
  followed that and declined the work they were asked for. SPEC.md was rewritten top down against the
  owner's charter first, and the code followed in one change.

  **Question, Answer and Notify became Request, Clarify and Result**, and every message is answered — an
  announcement is a Request answered `acknowledged`. A Request carries items of work or questions and a
  `done_when` of at least twenty characters; a Result gives each item `done`, `answered`,
  `acknowledged` or `blocked` with a blocker of `missing_information`, `conflict` or `permission`, and
  names its `sources`. `closed` is the broker's alone.

  **Nothing times out.** `ORPHANED`, `ghosted` and the delivery and working windows are gone; an exchange
  that stops moving is *flagged* to the person (`parley flags`), and a returning agent is handed the work
  it had collected again. A conversation ends by consensus of the participants asked — those holding work when closure
  was proposed, including any that have since sent their Result — or by a
  person — `force-conclude` now takes a reason, and each holder is told it.

  **The broker assigns the conversation.** A sender holding requests from one conversation names nothing;
  holding several, it names one; only a sender holding none, or a person, starts a new one.

  **Migration 008 rebuilds `exchanges` and `messages` on a real store**, not only an empty one: an orphan
  becomes open again, a ghosted exchange becomes `closed`, work 0.4 left open in a conversation a person had
  closed is closed with it, a standalone notification is dropped, and size refusals are merged under their
  new types. That third clause was found by the review gate: 0.4's `force_conclude` closed only the
  conversation row, and carried across open, that work locked its receiver out of sending any Request while
  no person could close it. It was proved against a schema-7 store built from the
  embedded migrations, including sequence continuity and the integrity check.

  **Every surface offers every operator operation, and that reverses 005's exclusions on the owner's
  call.** The terminal gained a Health dialog (flags, waits, results, refusals, clarifications),
  force-conclude behind the identifier typed back, prune behind the project's name, instruct, and a
  clarification action for a seat; the web gained prune; the CLI gained `instruct`, `start`, `archive`,
  `unarchive` and `destroy`. `SurfaceTests` holds each terminal call to one file carrying its door, and
  `DriftTests` now asserts both surfaces bind every `BrokerClient` operation with no exemption.

  **The terminal composer's labels lost their examples** where the console keeps them: five fields at one
  row each already fill a twenty-four row terminal, and the examples took five more rows, which opened the
  form with its first field off the screen. done_when kept its reason.

  **The review gate was met on the final code on all three lenses**, and what it found is most of what is worth
  reading about this change. Each finding below was reproduced by probe before it was fixed; the ledger is
  `REVIEW-LEDGER.md`.

  - **A question 0.4 carried forward could never be finished.** 0.4 held sub-question ids only to being distinct,
    migration carries an open question as an open Request, and a Result's recorded item is capped at 64 characters.
    Echoing a longer id was refused by storage and leaving it out was refused for not matching, and nothing in 0.5
    times out. `PayloadShape.ItemIds` now leaves out ids a Result cannot carry, `MessageLimits.LongestItemId` states
    the bound once and `ItemIdBoundTests` pins the schema to it, and a Request left with none is answered under
    `request` — which the broker *requires* and names in its refusal, because the first version merely allowed it
    and an agent was then told a storage constraint rather than what to send.
  - **A message stored while its receiver was collecting lost its wake.** The drain ran, then cleared the bound as a
    separate step, so a Request landing between the two found the bound held, asked for nothing, and lost it to a
    collection that never read it. An attached adapter only records a notice; a turn is what makes it collect.
    `BrokerRuntime.CollectAsync` frees the bound before the drain reads.
  - **A collection its caller limited left the rest with no turn coming.** `parley_inbox` offers a limit, and a drain
    that took one of two freed the bound for both. The drain now asks again when it filled its limit and mail remains.
    `WakeLifecycleTests` had asserted the bound was clear after a partial drain, standing in for its own comment's claim
    that the agent would be woken again; it asserts the wake now.
  - Earlier, before the final-code runs: a silent voter nothing asked again, then two ways to spend another agent's
    turns through the fix for it, bounded in the end to once per poll; the broker's own notice wrapped as the peer's
    words; retention deleting the Request an away requester's Result carries back; a peer's Request text reaching the
    terminal's forms unescaped; work 0.4 left open in a person-closed conversation; and text across the surfaces,
    README, skill, SPEC §18 and the proto that still described 0.4.

  Two tests were added for guards that held but that nothing checked: two turns asked of one resident are taken one
  after the other (`OverlappingTurnsTests`, with a mutation check), and a Request refused for an overlong id leaves no
  half-open exchange behind (`OverlongItemIdTests`). **Spec Kit was not run for this plan**: there is no `spec.md` or
  `tasks.md` beside it.

- **Clarifications are uncapped by default** — 0.25.8, schema 7, no wire change. The owner's call,
  made while SPEC.md was being rewritten: the new §5.2 says depth is not capped unless a deployment
  caps it, and `DocumentedDefaultTests` caught the code still applying 200. The spec leads the rewrite,
  so the code followed rather than the test being loosened.

  **Spelled as the largest integer, and forced the way the pair cap's 50 was.** `exchanges` holds
  `CHECK (clarify_depth <= clarify_depth_cap)`, which SQLite cannot alter and this migrator cannot
  rebuild. The range of caps is held by two triggers instead, and `007_unbounded_clarifications.sql`
  replaces them to admit exactly `2147483647` beyond 0..1000 — at which the CHECK never binds. Zero was
  not available: it already means no clarification at all. `ExchangePolicy.UnboundedClarifications`
  and `BoundsClarifications` name the value; `Validate` rejects anything past 1,000 that is not it.

  **A test was asserting the opposite on purpose.** `ClarifyCapBoundsTests` wrote `int.MaxValue` and
  expected a refusal — "a value that switches the cap off while every constraint still passes". It
  is now split: that one value is admitted and lets depth run past the band, and one short of it is
  still refused. `parley clarifications` prints "Caps in force: none" rather than the number that
  spells it, which would have told somebody a cap was set at a value nobody chose.

- **An open transcript in the terminal follows its conversation** — 0.25.7, no protocol change.
  Reported by the owner on the first question asked after 0.25.6: the answer arrived in forty-seven
  seconds and was not on screen until they closed the transcript and opened it again. `OpenAsync` read
  a conversation once and nothing read it again.

  **Polled every `Shell.FollowEvery` (3s), not driven by the seat's stream.** The stream speaks only to
  a seat about its own mail, and a transcript is as often an operator with no seat reading two agents.
  One read waiting in the queue at a time, so a slow broker does not stack a read per tick in front of
  every action. It stops when the view leaves the conversation or another is opened.

  **The web console recorded the opposite decision, and it is answered rather than overruled.**
  `_Layout.cshtml` says a transcript never refreshes because "a page that changed under a reader
  mid-sentence would be worse than one that is briefly out of date". `TranscriptRegion.Follow` does not
  move a reader: a transcript grows only at the end, so the row under the cursor is the same row after,
  a reader on the last row is kept on the last row, and one who never moved the cursor is left at the
  top. It redraws only when the written text differs, so most ticks draw nothing. **The console is not
  changed** and keeps its "fallen behind" notice; whether it should follow too is the owner's call.

  **A failed read keeps the transcript and says so once** — "may be out of date" — and takes its own
  words back on recovery only if nothing else has been said since. `FollowEvery` is a seam for the
  reason `Opener` is: `Driving.Show` turns it off, because a surface queueing reads on its own schedule
  would make `Settled` meaningless across the suite.

- **A store has one broker, and a second launcher says so and exits** — 0.25.6, no protocol change, SPEC.md §4
  amended to say how its existing rule is made true. Reported by the owner as seventeen agents
  "not running, and its machine is not connected" on the machine running them.

  **The terminal was right to say it; the broker's record was wrong.** At logon a scheduled task and a
  Windows Terminal restoring its last session both ran `parley serve` with identical arguments. Every
  broker clears `nodes.connected` and `agents.connected` when it opens a store, because a broker that
  has just started holds no connections — and it did that *before* binding its port. The loser cleared
  the winner's live node, failed to bind, and exited 69. The winner's node stream never ended, so it
  never rejoined, and nothing set the flag back. Asks still woke agents — waking reads the in-memory
  join, admission reads only retirement — so it was every listing that lied, including the roster
  peers read.

  **Nothing recorded it.** The log was held open for writing, so the loser could not open it and its
  "No broker started" went to a console nobody could see. Found from the task scheduler's history, the
  running broker's parent process (`WindowsTerminal.exe`) and Terminal's `state.json`, then reproduced:
  `Startable` before a second `BrokerRuntime.Open` on the same file, `Stranded` after, with the live
  join still held.

  **`StoreLock` is taken before anything touches the store**, as the first act of `BrokerRuntime.Open`:
  `<store>.lock` opened with no sharing, released by the operating system when the process goes, so
  there is no stale lock and no heartbeat. The holder writes `<store>.owner` (pid, start time, build)
  and a refused broker believes it only after confirming that process is alive and started when it
  says. **The lock file is never deleted on release** — on Linux an exclusive `flock` belongs to an
  open file, so unlinking it would let the next two starters each lock a different file of one name.
  Only `project remove --destroy` deletes it, first, and a lock it cannot delete means a broker it did
  not stop is still serving — so it stops before touching anything else.

  **A second launcher exits, and a standby was built and taken out on the owner's call.** The first
  version waited on a held store and took it over when released, on the argument that the winner at
  logon is arbitrary — that day the terminal tab won — so closing the winning window would leave
  nothing serving until the next logon. The owner judged that over-engineering and counter-intuitive:
  an idle second broker is a process nobody expects. So a held store is `Start.Held`, said once with
  the holder's process, and not a failure. `serve --db` exits 0; a manifest whose every project is
  held exits 0; held projects in a manifest with free ones are skipped while the rest are served, and
  a held project named later is not retried until its entry changes. **The cost is recorded rather than
  hidden:** whichever launcher wins is the one the deployment now depends on staying open.

  **The log is shared.** Each line is appended by open, write, close — a held .NET writer keeps its own
  position and would write its next line over whatever the other process appended.

- **§7.1's open-question cap is off by default, and a question can go to every agent** — 0.25.5,
  schema 6, no wire change. Both the owner's decisions, after the terminal offered *All agents* when
  announcing and not when asking.

  **The asymmetry rested on a misreading.** `Participation.Everyone` said a question to everybody
  "would saturate a small deployment" because §7.1 caps open exchanges per pair. The cap is per
  *peer*, so N questions to N different agents hold one slot each; and "the asker owing answers"
  had the debt backwards. §5.5 already defines the plural ask as N ordinary exchanges and calls
  that "the whole design", so `SeatVoice.AskEveryoneAsync` is that, from a roster instead of a
  role. **The console never had the asymmetry**: its *Everyone in this conversation* box already
  asked or announced, so this was terminal-only.

  **The cap was not doing what its rationale said.** It refuses a *further simultaneous* question
  to one peer and never shortens a conversation — one exchange plus clarifies, and a Clarify takes
  no slot. The two things that end a conversation before consensus are `ClarifyDepthCap` and §6's
  window into §14's ghost sweep. The owner removed it anyway, on the clarify cap's own reasoning: a
  cap censors the evidence that would justify it.

  **Unbounded is `open_pair_cap = 50`, and 0 was impossible — probed, not reasoned.** The band is a
  `CHECK` on `exchanges`, SQLite cannot alter one, and `exchanges` cannot be rebuilt the way 003
  rebuilt `messages`: it is a parent of `messages` and `message_referrals`, `SchemaMigrator` runs
  each migration in a transaction where `PRAGMA foreign_keys` is a no-op, and `DROP TABLE` under an
  enabled key fails on the first child row. **That rebuild passes on every empty fixture and fails
  on every real store.** `006_unbounded_pair_cap.sql` makes both triggers stand down at the ceiling
  and keeps them for 1..49, so a deployment that reinstates a cap still has §9.3's storage guard.
  `ExchangePolicy.Unbounded` and `BoundsOpenQuestions` name the sentinel so nothing compares 50.

  **Three existing tests went vacuous and still passed.** `A_refused_question_does_not_consume_a_pair_slot`,
  `Notifying_takes_no_pair_slot` and `A_dead_peers_slots_are_reclaimed_by_the_sweep_not_by_attaching`
  all used the default cap as their instrument; with no cap there was nothing for them to fail
  against. Each now sets one. The two new storage tests were vacuous in their first draft for the
  same reason — ten rows never reach fifty — and were confirmed to fail with 006 removed.

  **Still open, and a spec rather than a patch:** the owner wants Notify and Ask merged so that
  every message requires an answer, even "received". That changes §5.0's non-membership of an
  announcer, §5.4, §13.1, §15's resolution signals, the message-type enum and a §18 protocol bump,
  so it runs through Spec Kit. Recorded against it: 0.25.3's `collected` mark already says whether
  a message was received, at no model cost; what an answer adds is whether it was understood.

- **The composer's two actions are told apart by what they cost** — 0.25.4, no protocol change.
  Reported as confusion rather than as a defect, which is what made it one: the owner read "Ask
  somebody a question" and "Tell somebody something" and proposed **Tell → "fire and forget"**,
  **Ask → "start a conversation"**.

  **Both of those name a property the two share, and the spec says so in as many words.** §5.4:
  "Every message type is fire-and-forget in the sense that the sender does not wait (§4.2); what
  distinguishes Notify is that *nothing is owed in return*." And §5.0: "A Notify **declares** a
  conversation rather than joining one" — which is why nineteen announcements had produced a
  conversation in the first place. **What separates them is debt**, so that is what the labels say:
  `Owing.Asking` is *opens an exchange they must answer*, `Owing.Announcing` is *no exchange, no
  answer owed*, and both surfaces read those two constants.

  **"Tell" became "Announce", on the owner's call**, because §5.4 calls a Notify "an event. Not
  chit-chat" and an announcer gets no vote in the conversation it declares. The cost is recorded
  rather than hidden: the wire type is `Notify` and the agent's tool is `parley_notify`, so this
  surface now says a third word for one operation — bought by the word being the true one.

  **"Start a conversation by telling somebody something" is gone outright.** A Notify does declare
  a conversation, but §5.0 gives its sender no membership and no vote, so that label promised a
  seat at a table the protocol does not set.

  **The clause is in the menu and never on the status line.** Two of them is fifty columns, which
  would push every action behind `?` at any ordinary width — so `RegionAction` grew a `Costs` the
  menu renders and the status line does not. A menu is a list somebody reads; a status line is a
  reminder of what they already know.

  **The console's checkbox said "I would like a reply"**, which is a preference rather than what it
  decides. It says `Ask a question — opens an exchange they must answer` now, from the same
  constant, with the announcement case under it.

- **A transcript says who could see a message and whether it was collected** — 0.25.3, no protocol
  change. Everything here came out of one broadcast to nineteen agents.

  **The visibility note was longer than the messages it annotated.** §5.0 makes a conversation a
  set of pairwise exchanges, so a broadcast is nineteen pairwise notifications in one conversation
  — and every one carried the other eighteen participants by name, four hundred characters, under
  a header that had already said who the message was for. The owner read it as the recipients
  having missed the message. It names four and counts the rest now, the shape every other list here
  settled on; `ExportTests`'s point survives, because naming *some* is what makes the note
  actionable and the export's header lists every participant.

  **The fact they actually wanted was on no line at all.** Who *could* see a message is fixed when
  it is written and is a statement about the protocol; whether the recipient has *collected* it is
  a fact about the deployment now — §4.2's inbox drain, `delivered_utc`. Two different questions,
  one of them unanswerable from the screen, and the wall of names was standing where the answer
  should have been. `Delivery` puts it in the header of every message, on both surfaces and in the
  export: `collected 14:03` or `not collected yet`. **"Collected", never "seen"** — delivery is the
  broker handing it over, and no row here records what the agent did next.

  **A conversation can be copied to the clipboard** (`c` in the transcript), which is what somebody
  reading one over SSH needs and what `parley export` cannot give them without a shell on the other
  machine. What is copied is what is on screen rather than what the export writes: the export
  flattens payloads because a text file has no defence against one shaped like an annotation, and
  this surface has the indent instead, so the readable form is also the safe one. `Shell.Copier` is
  a seam for the reason `Opener` is one — a suite run in a loop would otherwise replace whatever
  the person at the keyboard had on their clipboard.

  **Copying is the first transcript action that does not need a seat**, so that arm is no longer
  gated as a whole: reading a conversation and taking a copy of what you read are one act, and an
  operator without a seat is the one most likely to be quoting a transcript somewhere else.

- **Fields apply their own rule, and the message line is a paragraph** — 0.25.2, no protocol
  change. Three defects from one session, all met while telling nineteen agents one thing.

  **A field with a rule about characters now applies it as it is typed, on both surfaces.** The
  owner named a conversation `tell everybody test`, wrote the message, sent it, and got the rule
  back with the form closed and their words gone. `Naming.NameCorrection` had made exactly this
  argument for a project's name in 0.18.11 and three fields were left out: the conversation name on
  the composer, the role an enrolled repository answers to, and the name somebody joins under —
  plus the console's own *Start a conversation called*, whose placeholder ("what it is about")
  invited the sentence that cannot fit.

  **A space becomes a hyphen rather than being dropped, which is the owner's call and a departure
  from the field beside it.** Dropping gives `telleverybodytest`, which nobody reads back;
  `tell-everybody-test` keeps the words. So the project-name correction stopped swallowing spaces
  too — one grammar, one correction: `ProjectName` and the role half of `AgentName` are the same
  expression, and `Parley.Core.NameCorrection` is now the only place it is applied. The
  conversation name has its own character set and lives in `Parley.Client.ConversationName`, which
  `SeatVoice` reads as well, so what a field offers and what the send refuses cannot disagree.

  **The console applies the same rules in the browser and states them without scripts.** One
  implementation in the frame, walking whatever a field declares in `data-correct`; `pattern` and
  `maxlength` come from the same constants and are what a browser with no scripts enforces; the
  server refuses regardless. A rule with three spellings would be a console that admitted what the
  terminal refused.

  **`Label` eats `_`, and the owner read a rule with one of its own characters missing.** The
  toolkit takes an underscore as the hotkey marker, so the sentence naming `'.', '_' and '-'`
  reached the screen as `'.', '' and '-'`. That is the same class of defect `Safe` exists for, read
  the other way: almost every label here draws text this program did not write — a path in the
  picker, a project's description before it is destroyed, a conversation identifier on the status
  line — and an underscore is legal in all of them. `Plain` is a label with hotkeys off, and
  `UnderscoresSurviveTests` walks the view tree so the next label somebody adds is held to it.

  **The message line grows to a paragraph and can be cleared.** It was one row, and a broadcast
  report is a count, a cause and the names: what the owner saw was *"Refused for 19: nothing has
  registered under that name yet, so nothing can be addressed to it. Stating it once is what"* —
  cut off inside the half that says what to do. It is as many rows as it needs now, up to eight,
  bounded by a floor the regions keep; past that the last row names the key that shows it whole,
  because nothing here is silently cut. `x` clears it and gives the rows back. Nothing clears it on
  a timer: a line that faded would take the only record of what happened with it.

  **The status line names `?` at every width.** The rung that lists everything named no menu, so on
  a wide terminal — where everything fits — the one route a pointer has to any action was on no
  screen at all.

  **What was reported as a third defect is §14 working, and what stands behind it is not.** "All
  agents" does fan out — *Told 0 of 19* is nineteen sends — and every one was refused because none
  of those agents has ever registered: §12.3 says "§14 has the broker refuse an unregistered agent
  every operation", and `IsUnroutableAsync` counts an unknown name unroutable. That much is the
  protocol. **Why nineteen enrolled agents were unregistered is a defect on this surface, and the
  owner was right to refuse the answer "start each of them".**

  **The add screen predicted a registration instead of checking it.** It said *"Started 'x'. It
  registers itself as it attaches"* — a sentence about the next few seconds, printed as a result. A
  node reports that it launched something; whether the adapter attached, reached this broker and
  registered is a different question, and the registry is the only thing that answers it.
  `parley enroll apply` has looked since 012 step 1 and waits thirty seconds for each; this screen,
  which is where the owner actually enrolled nineteen repositories, never looked at all. It now
  gives three answers where it gave one: nothing started, started and registered, started and not
  seen — the third worded as "I do not know", per 008.

  **And the terminal can now start every agent that has never registered, in one act.** That is
  what 012 step 1 exists to remove — nobody should open nineteen repositories by hand — and the
  remedies left on this screen were exactly that or nineteen separate keystrokes. `u` in the agents
  pane, offered only while there is something to start, and named with the count because it
  launches one process per agent.

  **Every start is asked for before anything is waited on.** Thirty seconds each is nine and a half
  minutes for nineteen, on a surface whose work queue is serialised; one wait for the set is one
  window however many there are. `Every_start_is_asked_for_before_anything_is_waited_on` pins it by
  ordering rather than by timing. It says what it is doing while it does it, because a screen that
  goes quiet for a minute is one somebody assumes has hung.

  Not changed: the picker still offers a never-registered agent, because judging routability from a
  roster this surface holds would be the client second-guessing the broker, and `Peers()`'s own
  remark about spawn-on-demand is now out of date — recorded, not acted on.

- **First use of the rebuilt deployment** — 0.25.1, no protocol change. Five defects the owner met
  within an hour of running the terminal against nineteen real agents, and none of them was visible
  from inside the suite.

  **The peer picker did not scroll, and it ate its own description.** `ChoiceDialog` gave its list
  `Height = choices.Count`, so twenty rows inside an eighteen-row dialog believed every row was
  visible and had nothing to scroll: the cursor walked off the bottom and the highlight — the only
  mark saying which row a key acts on — went with it. The same arithmetic gave the label
  `Dim.Fill(choices.Count + 2)`, so a long roster squeezed the description to nothing. The list fills
  the space now and the label is sized from its own words. **`DoorsAreReadableTests` could not see
  either**, because every dialog it drives offers two or three choices.

  **Read the list's viewport, never the screen, and that was learned here.** The harness never pumps
  the main loop, so `Driving.Text()` returns whatever was last drawn — a screen assertion passes or
  fails on when a redraw happened. The first probe written for this reported a bug in the toolkit
  that was not there.

  **A broadcast named who was refused and dropped why.** `NotifyReply` has carried a rejection since
  the operation existed and the single-recipient path turns it into a sentence; the broadcast — the
  one path where a single cause can refuse the whole roster — threw it away and printed nineteen
  names. The owner's screen cut the line off part way through them. Grouped by reason now, names
  abbreviated after four, and the same abbreviation applied to the *waiting for* list beside it,
  which had the identical problem and no report yet.

  **Every field was one row tall, and rule 8.4's cap is eight thousand characters.** Fields that hold
  prose are several rows deep and grow and shrink under what is written into them; a name, a path or
  a project stays one line, because Enter in those is how every form here is finished and a line
  break in a project name is a value that will not survive a manifest. `TextView` is deprecated in
  Terminal.Gui 2.4.17 in favour of a package this repository does not have — the suppression is one
  file, one type, and says why taking the replacement is the owner's call.

  **A repository could not be added to a second project at all.** SPEC.md §4.1 permits it and 009
  built the tooling; what stopped it was one word on this surface — the terminal enrolled with
  `Unattended: true` always, and FR-013 refuses a second unattended start for one workspace. So a
  rule that was never about the second project refused it, and a different name did not help
  **and must not**: the rule is keyed on the workspace because the hazard is two nodes running one
  working directory. The second project gets the launch entry, no catalogue line, and a start
  through the project that does hold the consent — one node starts a client in the repository's own
  directory, the client brings up every parley server in its launch configuration, and the entry just
  written is one of them. One start, two registrations.

  **Adding opens a folder picker first.** Nineteen repositories was nineteen absolute paths typed
  into a field that could only report afterwards that one was not a directory. It starts beside the
  last repository added this session, and at the home directory before that. Built on this surface's
  own list rather than `OpenDialog`, which runs itself modally — every dialog here is a session a
  test can drive — and which browses files where the question being asked is *which of these is a
  repository*. The field is still typed into.

  **What the review gate found is most of what is worth reading about this change.** Fourteen
  findings, every one reproduced by probe before it was fixed:

  - **A broker could forge a second line of the broadcast report.** The reason on the failure path is
    `Status.Detail` — whatever came back from the far end of a gRPC call — and the report is
    contracted to be one line, so a newline in it produced a second line reading *"Told all 2."*,
    which is this report's own success sentence in somebody else's hand. **0.18.6's transcript
    forgery in a new place.**
  - **Adding writes three files behind a dialog that named two.** The third is the repository's own
    `CLAUDE.md`, which enrolment creates or appends a `## Peers` section to. Pre-existing, and the
    one thing this dialog exists to prevent. Naming it then made the description long enough to
    squeeze the confirmation's own choices off the screen — **a dialog asking a question with no
    visible answers** — which is how the second half turned up.
  - **A folder could claim to be a repository.** The marker was a suffix and a folder name is
    somebody else's text. It is a prefix inside an indent now, and only the dialog's own rows sit at
    column zero: the rule `ColumnZeroTests` holds the transcript to, in the other place this surface
    draws somebody else's text in a list.
  - **The add confirmation quoted manifest values raw.** 0.18.9 fixed exactly this in
    `Removing.Describe` and gave `Withdrawing` the same treatment; `Adding` was the one left out.
  - **The grouped refusal report grew back to a thousand characters** when the reasons do not repeat
    — a broker whose detail names the recipient gives one group each. The owner's own defect,
    through the door grouping opened.
  - **The actionable half of the report came after the names.** A terminal shows one row, so what
    comes first is what survives, and *"Refused for"* plus nineteen names is what they actually saw.
    Counts, then the reason, then the names.
  - **The message cap was enforced only after the form closed**, losing what somebody wrote. Nobody
    met it while every field was one row tall; this change made it reachable.
  - **`SetupSurfaceTests` could not see a nested public type.** `Type.IsPublic` is false for one, so
    `Enrolment.Outcome` — public since the split — was never on its list, and neither was the type
    added beside it here. The guard's claim is that a member appearing there is a decision somebody
    makes in that file, and it could not keep it.
  - Plus: a form taller than the screen and clipped at the top; a description measured by character
    count rather than by the lines it already had; a picker that dead-ended on an unreadable folder;
    an empty refusal reason printing a dangling colon; a type made public that nothing outside its
    assembly could use; and a record and a method sharing one name in one file.

  **Two probes were wrong before the code was.** One reported that the second-project re-check never
  fires — its catalogue fixture had a workspace and no command, which is not a role anything can
  start. One reported the picker not scrolling by reading the drawn screen, which the harness never
  redraws. Both are written down where they happened.

  **Hygiene**: a real deployment's agent name had appeared in a comment in `src/Parley.Client` since
  012 step 1 and is replaced with placeholders. **The name is not repeated here**, because this file
  is committed and writing it down would be the leak again in the record of closing it — which a
  first draft of this paragraph did. The two pre-existing terms recorded in `REVIEW-LEDGER.md` are
  still there and still the owner's decision.

- **Previous plan**: `specs/012-one-catalogue-per-machine/` — 0.25.0, step 1 of two. **Adding an agent
  now leaves it registered.** `parley enroll apply` wrote two files and started nothing, and said so
  in its own success message — *"Restart your editor in each repository"* — a step that existed only
  to compensate. The terminal's enrolment already started what it wrote; the CLI did not.

  **A never-registered agent can receive no message at all.** §14 counts an unregistered name
  unroutable and both `Ask` and `Notify` refuse an unroutable recipient before any wake, so such an
  agent is not quiet — it is unaddressable. Only an operator `start` reaches it. `NeverRegisteredTests`
  pins all three answers over a real socket, because two of the three were reasoned about and got
  wrong.

  **Nothing writes a registration and nothing may.** §12.3 makes it the adapter's own act on attach,
  so the only honest way to leave an agent registered is to start it and let it register itself —
  which costs a process launch and no model tokens, because the node's start and the turn it
  dispatches are separate calls.

  **A timeout says "I do not know", never "it did not happen".** 008 exists because a broker stopped
  waiting before a node had finished and recorded the opposite of what occurred.

  **Step 2 is the architecture and is not built**: one catalogue per machine keyed by workspace,
  project membership read from each repository's `.mcp.json`, consent moved into the store, one node
  per machine owning the catalogue as sole writer, and enrolment as an operator operation. Protocol
  0.5, one coordinated upgrade. The spec carries nine decisions with the argument for each; the plan
  carries why the two steps ship apart.

  **Enrolment now refuses a project whose broker does not answer** (0.25.0). It was written, reverted
  once, and taken on the owner's call. It cost ten probes in `ProbeThree`, `ProbeTen` and
  `ProbeEleven` that enrolled into a resolved project with nothing listening — a state the command
  now declines to be in, so the fixtures were describing a deployment it will not write into. Each
  has a broker that actually listens, through one `ServedProject` helper rather than three copies.

  That also retired three hardcoded `51707`s, which is the shape of the flake already recorded
  against `FreePort`, and found a fourth thing: one of those endpoints was `127.0.0.1:51707` with no
  scheme, which nothing could ever have connected to and which made the probe *throw* rather than
  refuse. It reports now, because this runs among the manifest's other refusals and one of them
  throwing would take down a run where every other says what is wrong with it.

  **Still open**: a machine that names no project at all enrols freely, because there is no endpoint
  to ask. Closing that changes first-run setup and is recorded in 012's spec rather than decided.

- Previous: `specs/010-waking-notifications/` — 0.23.1, merged (#87), **protocol 0.4**. A
  notification wakes the agent it is addressed to, and a person can tell every agent in a project at
  once.

  **This reverses SPEC.md §17 question 15**, which recorded the current behaviour as deliberate —
  "only a Question, a Clarify and an Answer ask for one — a Notify nudges and never asks" — and
  warned that wiring Notify to the wake path "would make it unbounded and would be question 1's
  problem reached by a second route". The objection is **answered rather than overruled**, out of
  question 1's own evidence: it records sixty notifications accepted in a tenth of a second and
  records that `inbox` with no limit "collects everything pending", so one turn drains all sixty.
  Question 15 assumed the wake meant one turn per notification. Coalescing means it does not.

  **The bound counts turns, not processes, and that came from reading rather than reasoning.**
  `NodeCommand.StartAsync` dispatches a turn when the resident is already alive — deliberately, to
  close a race — so the node already refuses a duplicate *process* and cannot refuse a duplicate
  *turn*, and must not learn how: it does not know whether a command came from a notification or a
  question. **The spec's first draft keyed the marker on the recipient attaching, and that would
  have bounded nothing**: the first notification starts the agent, it attaches in a second, and the
  next fifty-nine each buy a turn. It keys on the **drain** instead, which is the event §4.2 already
  distinguishes from waking an adapter and the only one that proves the mail was read.

  **Every wake marks attention pending; only a notification consults the mark.** A question keeps
  today's behaviour because §7.1's per-pair cap already bounds questions — which is precisely the
  bound question 15 identified as holding the line. What changes is that a notification will not
  stack a turn on top of one a question has already asked for.

  **`parley top` becomes unusable against an un-upgraded broker and that is the intent.** §18 gives
  a protocol no patch level, and while the major is zero the minor carries breaking changes, so
  every adapter must be upgraded with the broker or be refused at attach. The bump is caused by the
  behaviour rather than by the new reply field: an agent that starts where it did not before is
  observable by a second implementation, which is §18's own test. **So this release carries the wire
  change and nothing else that might need reverting**, on 008's reasoning that a release changing a
  versioned wire contract and a live scheduled task's flag "cannot be reverted in halves".

  **All agents is not a protocol operation.** N ordinary notifications from the surface, on §5.5's
  precedent — and its argument is stronger here, since the costs §5.5 lists for a one-to-many
  exchange (its own admission rules, its own cap arithmetic, its own definition of concluded) are
  all absent for a message that opens no exchange at all.

  **Diagnosed while answering the report, and out of scope:** `AgentFleet.TickAsync` is called from
  exactly one place, `parley fleet`, its own long-running command. The autostart entry runs `serve`
  and `parley node` never sweeps, so on a deployment configured the documented way **nothing runs
  the sweep** that would eventually collect an uncollected notification. That is why the reported
  defect was permanent rather than slow — both doors shut, one by question 15 and one by the sweeper
  not being installed. This opens the first. Whether `parley fleet` belongs in the autostart entry
  is a separate decision and is not taken here.

- Previous: `specs/009-multi-project-enrolment/` — 0.22.2, merged (#86). A repository may answer
  in more than one project. SPEC.md §4.1 already permits it — the protocol has no concept of a project,
  membership is which endpoint an agent connects to, and `ProjectManifest` constrains name, store,
  endpoint and catalogue path but never a workspace. What forbade it is one line of deployment tooling:
  the fixed `parley` key in `.mcp.json`, and a refusal written against *re-pointing* that catches
  multiplicity only because one key can hold one entry. Brokers do not federate, so a repository several
  product lines depend on must pick one set of peers and be unreachable to the rest.

  **The key becomes `parley-<project>`.** The owner chose the prefix over the bare project name because
  ownership of an entry must be decidable from the key alone: withdrawal is the only path that deletes
  somebody's configuration.

  **A legacy `parley` entry is read and never renamed.** 007's rule with the middle third dropped —
  *stop writing, stop accepting, keep reading* becomes *keep writing it when no project is named, keep
  reading it always* — because the key is a component of every tool name a working agent sees
  (`mcp__parley__parley_ask`), so a rename edits an agent's vocabulary to change nothing.

  **Three things `research.md` found by reading rather than reasoning.** The six surfaces are two
  writers and four illustrations: the web console never opens a launch configuration at all, so one
  implementation of the key rule is achievable by construction rather than by discipline.
  `EnrollCommand.cs:803` calls `Describe` with no project, so today only `parley-top` can enrol into a
  named project and the feature would have shipped terminal-only. And `ResidentAgent.cs:157` sets
  `WorkingDirectory = workspace`, which is why one workspace may hold many launch entries and at most
  one catalogue entry — the launch entry is inert until a person opens the repository; the catalogue
  entry is standing consent to start without one.

  **SPEC.md does not change, and that was checked rather than assumed**: it contains no occurrence of
  `mcp.json`, `mcpServers` or "server key", and two of "launch configuration", neither giving a shape.

  0.22.0 because this is the release that also removes `parley console`, `--with-console`, `--console`
  and `--no-console`, which 008 committed to and called "a commitment, not a second reprieve".
  `InstalledEntry` keeps *reading* `--console`.

- Previous: `specs/008-instruction-readback/` — 0.21.1, merged (#85). An operator instruction
  must survive the turn it asks for. Three defects, all established by probe or by reading rather
  than by reasoning:

  **The broker gives up before the agent does and destroys the answer when it arrives.** It waits
  five minutes; the node will wait twenty. Any turn longer than five minutes is settled `failed`
  with no reply while the agent is still working, and when the node reports what the agent actually
  said, `SettleAsync`'s `WHERE outcome = 'pending'` refuses it. The store then asserts two things
  that are both untrue — the turn did not complete, and the agent said nothing — and there is no
  other copy of either. Pinned by `tests/Parley.Store.Tests/InstructionSettlingTests`.

  **Patience is punished.** `Instruct` passes the operator's own call token into both the wait and
  the settling write after it, so an operator who closes the tab cancels the write, the row stays
  pending, the node's real report lands, and the reply is *kept* — while an operator who waits gets
  `failed` written first and the reply *destroyed*. Exactly inverted, and neither is told which
  happened.

  **§10.2 records a reply nothing can read.** The `instructions` table holds text, outcome, reply
  and both instants; `proto/parley.proto` has `Instruct` and no reader; `InstructionRepository.GetAsync`
  exists and no operator surface calls it. Every part of an asynchronous design is built and
  unreachable — the wire enum has `PENDING`, the schema pairs settlement with its timestamp, and
  `Act.cshtml.cs` renders a `"Still pending."` branch nothing can ever reach.

  **The consuming defect is the one the owner met**: both console pages hold the HTTP request open
  for the whole turn. That is a defect on every screen, not a mobile one — it was found while
  discussing a phone, which is how it came to look like one.

  **Which artifact is wrong was decided, not assumed.** The spec and the code do not disagree; the
  spec is *incomplete* and the code faithfully implements the incomplete thing. §10.2 promises the
  reply is kept and gives no way to retrieve it, which is storage with no reader — and its own
  argument for returning an outcome at all ("a button and no indicator") is the argument for the
  readback. So §10.2 gains an operation and this is a protocol change.

  **No schema change — until the gate found one.** The table already carried text, outcome, reply
  and both instants. `005_instruction_reply_cap.sql` was added later, by lens 2, and is the only
  migration here.

  **What the review gate found is most of what is worth reading about this feature.** Five findings
  above low, all fixed, all in `REVIEW-LEDGER.md` with the probe that produced each:

  - **A departing node settled its agents' turns, and that destroyed answers.** Built deliberately,
    with a comment saying "what settles an instruction now is only ever something that knows" — and
    a node's *stream* ending is not its process dying. A network blip settled the record while the
    agent worked on, and the real report was refused on arrival. **The defect this whole feature
    exists to remove, reintroduced through a door nobody had checked, by the person writing that
    sentence.** The mechanism is gone; a turn whose node has gone stays *still running* until the
    sweep presumes it dead. The cost of waiting is a delay and the cost of guessing is a loss.
  - **An agent's reply had no bound at all.** `instructions.text` has carried rule 8.4's cap since
    003; `reply` carried no CHECK and the reporting path applied none, so anything able to reach the
    broker and claim to be a node could write an arbitrary blob into the operator's record. Two
    hundred thousand characters were stored and read back whole. Always possible, not always
    *reachable*: before the readback a reply came back only on the issuing call and was otherwise
    write-only.
  - **`InstructionsRemoved` was computed and shown to nobody.** The pruner counted it, the result
    carried it, the wire had no field. `parley prune` printed *"Removed 0 message(s)."* after
    deleting five hundred instructions. **The `Shell.Said` shape, one feature after `Shell.Said`.**
  - **`parley instructions` flattened the reply and not the instruction**, so a newline in an
    operator's own words forged a listing line — in output scripts read. The terminal had flattened
    both since it was written; one rule applied to half the output is worse than either applied
    whole.
  - *(low)* The truncation cut split a surrogate pair.

  **Two checks that did not exist were added rather than the findings merely fixed.**
  `The wire against the document` — SPEC.md was checked against the C# contract and nothing checked
  `proto/parley.proto`, though it opens by saying the document is the authority and it is not. And
  `TickAsync` actually calling the sweep, which nothing verified and which, since the disconnect
  settle was removed, is the whole of the guarantee that nothing stays running for ever.


  **THE DEPRECATION DATE MOVED, AND THIS IS THE RECORD OF IT.** 007 wrote that `parley console`,
  `--with-console`, `--console` and `--no-console` are *removed at 0.21.0*. This feature is 0.21.0
  and **does not remove them; they move to 0.22.0.** The reason is not convenience: one release that
  both changes a versioned wire contract and removes a flag a live scheduled task depends on cannot
  be reverted in halves, and an operator whose broker will not start at logon cannot tell which half
  did it. The wire change is the one that must be revertible alone. `InstalledEntry` must keep
  *reading* `--console` regardless, for entries written before 0.19.0, so the removal is "stop
  writing, stop accepting, keep reading" — narrower and more careful than it sounds, and worth its
  own change. **0.22.0 is a commitment, not a second reprieve.**

- Previous: `specs/007-split-terminal-tool/` — 0.20.3, merged. The terminal and the web UI
  become their own
  tool packages, so changing either no longer stops the broker. Today one executable holds
  everything, an executable cannot be replaced while it runs, and stopping the broker stops the
  node, which SPEC.md §12 says "tells every agent it started to quit" — where "the process holds
  everything the agent has learned". Changing a status line ends every conversation on the machine.
  The terminal is also the most-changed part of the repository: 29 source files last cycle against
  1 in the broker and 0 in the adapter.

  **The web UI splits too, and is renamed.** "Console" means three things here and only one is a
  browser surface — `parley console` and `--with-console` become `web-ui`; `OperatorConsole` (§10.2)
  and `ConsolePicker` (whether a terminal exists) do not change. An earlier draft of the spec said
  the web UI was unusable on a headless machine; that was wrong and is corrected in place. It is an
  HTTP server, it needs no display, and an SSH port-forward is what §16 permits — on a headless box
  it beats the terminal, being a forward you leave up rather than a session you hold open. The
  argument is that it should be a *choice*.

  **Writing the spec found a live defect, and it ships first, alone.** The terminal reaches SQLite
  today: `Parley.Cli/Top` uses `NodeCatalogue`, which lives in `Parley.Headless`, which references
  `Parley.Store`. `GraphTests` could not have caught it — it walks from `Parley.Client` and
  `Parley.Web`, and the terminal sits inside `Parley.Cli`, which owns the store legitimately. The
  reader uses only `System.Text.Json` and `Parley.Core`, so it moves to `Parley.Configuration` at no
  cost. `Parley.Client/Views/AgentStanding.cs` already documents the problem as a workaround it had
  to code around.

  **Step 1 is done (0.18.12): the leak is closed.** `NodeCatalogue` is in `Parley.Configuration`,
  which is what it always needed — it uses `System.Text.Json` and `Parley.Core` and referenced
  nothing else in `Parley.Headless`. `Parley.Headless` keeps `Parley.Store` for `AgentFleet` alone.

  **The failing check came first, and that ordering is the point.**
  `The_terminal_reaches_no_store_by_any_assembly_it_imports` reads every `using Parley.X` under the
  terminal's sources and walks X's own references, because the terminal has no project to walk
  from — it lives inside the one assembly that owns the store legitimately, which is exactly why no
  existing guard could see it. Run before the move it said
  `the terminal has a route to a store: Parley.Store (via Parley.Headless)`. It is replaced by an
  ordinary project walk once `Parley.Top` exists.

  `Parley.Configuration` gains a walk of its own in the same change: it must reach neither a store
  nor the transport, because every tool is about to reference it and anything acquired there is
  acquired by all four.

  **Step 2 is done (0.19.0): `parley-top` is its own package, and it cost a project the plan did
  not foresee.** The terminal does not reimplement enrolling a repository or creating a project —
  it runs the same commands in process and prints what they said, with a remark saying why. Closing
  over every `Parley.Cli` type it names in non-comment code is 11 files and 4,153 lines, every one
  of which imports only `Parley.Configuration`, `Parley.Core` and the BCL. That became
  `Parley.Setup`; `research.md` R8 carries why not `Parley.Configuration` (Parley.Mcp references
  it, and an agent's server would then carry the code that writes its launch configuration) and why
  not left where it was (the leak reopened through a wider route).

  Five things learned building it, each written where it happened:

  - **The terminal's tests lost a module initializer in the move.** `TestMachine` fixes the machine
    half of every agent name; without it a registry holding `orders@test` no longer matched a seat
    asking for `orders@<this laptop>`, and two seating guards stopped refusing. The guards were
    intact and the fixture had gone, which is the more dangerous of the two.
  - **`DriftTests` named two surfaces and there are three.** It read the whole of `Parley.Cli` as
    "the terminal", so `PruneAsync` — bound by `parley prune` and deliberately by neither screen —
    counted as bound by a screen. The command line is named as the third surface rather than the
    operation exempted.
  - **Two tools in one output directory break the child.** `Parley.Top.Tests` references both, and
    `Parley.Cli` brings a framework reference to ASP.NET Core, so several
    `Microsoft.Extensions.Configuration` assemblies resolve from the shared framework and are left
    out — while the terminal's runtime configuration names only the base framework and
    Terminal.Gui's module initializer scans every loaded assembly on start-up. The co-located copy
    dies before its first line. Nothing ships that arrangement, and `TopProcess` says so.
  - **SC-001 has two halves and the second is easy to miss.** Not stopping the broker is worthless
    if the script re-registers the autostart entry afterwards: that replaces the scheduled task,
    which restarts the process, which ends every resident agent anyway — the stop avoided and the
    cost paid three hundred lines later. Found by reading the script's tail before running it on a
    live deployment, which is where it would have been paid for real.
  - **`install.ps1` read the version from `Parley.Cli`'s package**, which a surface-only run never
    packs, so it stopped with "nothing was packed" on a run that had just packed exactly what it
    was asked to. Found by running it.

  **`-Only` is what SC-001 actually rests on, and the plan had not designed it.** `-Surfaces`
  chooses which surfaces a machine installs; `-Only <command>` installs exactly those tools, and
  the broker is stopped only when its own binary is among them. Verified on a live deployment
  rather than asserted: `install.ps1 -Only parley-top` with a broker running left the same pid, the
  same start time, the task Running, the project reachable and every agent registered.

  **No test was lost in the move**, checked rather than claimed: the method listings before and
  after differ by exactly two, both additions, both named.

  **Step 3 is done (0.20.0): `parley-web-ui` is its own package, and "console" now means one
  thing.** The broker starts it as a child process, does not wait on it, does not restart it, and
  ends it when the broker ends. `Parley.Cli` no longer references `Parley.Web`, checked by a graph
  walk that failed before the reference was removed and by reading the packed tool — a project file
  says what is referenced directly, and a package says what lands on a machine.

  **The deprecation has a date rather than being a kindness that becomes permanent.**
  `parley console`, `--with-console`, `--console` and `--no-console` are the old spellings. They
  work, warn once naming the new one, and are **removed at 0.21.0**. Two things made that
  non-negotiable rather than polite: the owner's scheduled task holds `--with-console` right now,
  and `InstalledEntry` must keep reading `--console` for as long as any machine has an entry
  written before 0.19.0 — a reader knowing only the new name would report those entries as having
  no web UI, so `autostart status` would say one thing while the entry did another.

  `OperatorConsole` and `ConsolePicker` do **not** change, and a test says so. "Console" meant
  three things here: the operator operations of §10.2, whether there is a terminal to draw on, and
  the web pages. Only the third is a browser surface, and one of the other two is a protocol
  boundary.

  Four things learned building it, each written where it happened:

  - **SC-005 is narrower than it reads, and it is narrowed in the open.** "A broker-only machine
    carries no part of the web stack" cannot be literally true: the broker serves gRPC over Kestrel
    and always has, so `Grpc.AspNetCore.Server` is in its package because `Parley.Server` needs it.
    What this removes is the web UI's own half — the pages, Razor, and `Parley.Web` — and that is
    what `PackedToolTests` asserts.
  - **Suppressing the startup block on a failed web UI would take the terminal line with it.** The
    first version printed the block only after the child started; a broker whose web UI could not
    bind then never mentioned `parley top`, on exactly the run where somebody needs telling there
    is another way in. The failure is a line inside the block now, not instead of it.
  - **A banner extraction anchored to `var body` broke for the second time**, because the web UI's
    line moved up one more variable when it gained a second form. Anchored at the first step of the
    composition now, so it covers however many steps there are.
  - **The plugin's skill lists `parley console` on purpose.** A test compares the verbs in the
    tool's source against the skill's, and the deprecation message itself is a verb by that
    measure. Rather than reword the message, the skill names the old spelling and the version it
    goes at — which is true, and tells an agent both things.

  **Step 4 is done (0.20.1): an install that must stop the broker says what that will interrupt.**
  Both scripts ask before stopping, name the conversations with an exchange still open, and warn
  rather than refuse — `-Force` already means the decision belongs to the person making it.

  **It needed a command the CLI did not have, and `parley waiting` was the wrong one.** That counts
  messages *queued* for an agent, which is the opposite of the risk: a question already drained
  into an inbox is marked delivered and no longer queued, so it sits in §4.2's four-hour Working
  window with nothing running to answer it once the agent is killed. That is exactly the exchange
  that ends as `ghosted` and exactly the one `waiting` would say nothing about. `parley open` is
  the §10.2 `conversations()` reply both surfaces already read, filtered to what is open — and a
  test asserts it prints identifiers and participants and not one word of any payload.

  **A guard that fails open says so.** When the broker cannot be asked, the scripts say they could
  not ask rather than implying nothing is in flight; the two are indistinguishable from the
  operator's side and lead somewhere completely different. That path is reached more often than it
  looks: a `parley` older than 0.20.1 has no `open` command, exits non-zero, and the script
  correctly degrades to "could not ask" — which is what every upgrade from an earlier version will
  do once, and is the honest answer.

  Neither script opens the store. §4 gives it to one process, and reading it for a progress message
  would break the boundary the whole architecture rests on; a test names the ways a script could
  open one rather than forbidding the word, because both scripts say "SQLite" out loud in prose
  they print.

  `research.md` carries eight decisions, `contracts/commands.md` the command surface each tool
  exposes.

  **0.20.3 is the grouping's own cost, reported by the owner: Up did nothing.** A heading is not a
  row the cursor may rest on, so a move landing on one carries on past it — and it carried on
  *downwards* whichever way the cursor had been going. Up stepped onto the heading above and was
  pushed straight back onto the row it left. Every group was a room with no door out of the top,
  and the seats, which sort first, could not be reached from below at all.

  **Nothing in the suite had ever pressed an arrow key** — 2,552 tests, and the guard on this had
  been written and checked by assigning `SelectedItem` directly, which is the one route that really
  does only ever travel one way. `Lists` already records the same shape once, about the letters the
  status line advertises: *a key tested only in the negative is a key nobody has pressed*. This is
  the stronger version — a key nobody had pressed at all, guarding the only navigation the surface
  has.

  **Two fixes were tried and both were wrong, which is worth keeping.** Taking the direction from
  the key in `OnKeyDown` cannot work: that is a *pre-process* hook, raised before the commands
  bound to the key are invoked, so it runs before the cursor has moved and compares a position with
  itself — the probe printed `handled=True selected=3` and the position unchanged. Neither
  `View.NewKeyDownEvent` nor `ListView.MoveUp` is virtual, so there is no seam around the move
  either. `ValueChangedEventArgs<T>` carries `OldValue` and `NewValue`, so the direction is in the
  move itself, and taking it there covers a key, a click and a direct assignment under one rule
  with no list of which keys mean "up" — a list that would have to be kept in step with a toolkit
  that owns the bindings.

  **0.20.2 is what the owner found on first use, and one of the three was invisible to the whole
  suite.** `Shell.Said` was written in twenty-odd places — seated as somebody, the broker refused
  the join, sorted by name, an agent retired, a log that could not be read — and **read by nothing
  on screen**. Fifteen test files asserted on the property. Every message this surface produced was
  computed, checked, and shown to nobody; the owner met it as `j`, a name typed, and nothing
  happening. That is the same shape as the terminal having no colour while `ColourTests` passed: a
  property the suite holds to and the screen never shows. There is a message line above the status
  bar now, always present rather than appearing on demand — a row that comes and goes resizes every
  region under it, and this surface has been bitten three times by exactly that.

  **Enter on a seat takes it.** The name is already on the screen under the cursor, so that is what
  somebody presses; `j` and typing the name back works and nobody guesses it. A retired seat asks
  first, because reinstating is an operator operation and this surface does not take one silently —
  but it is the person's own name, so what it needs is a sentence and a deliberate answer rather
  than a name typed back. The call lives in `Withdrawing` with the other operator operations,
  because `SurfaceTests` allows `Reinstate` in exactly one file and that check is the point.

  Two defects found while building it, both of which would have shipped:

  - **`Seat.TryRead` takes the person's name and adds the machine**, so passing a row's
    `arvind@test` whole is refused as not a name. Every Enter would have reported that the name on
    the screen was not a name.
  - **A seat on another machine would have seated you under a different name.** `arvind@elsewhere`
    resolves to `arvind@here` — a different agent, in a different row, while the row pressed went on
    belonging to somebody at another desk. Not offered at all now.

  **Reading a region's selection from another view is a defect that has now appeared four times.**
  `SeatSelected` read `Projects.Selected`, which is null in a project view. `Shell.Current` is the
  answer and was already there.

  **The agents list groups by state.** The owner reported the state scrolling off the end of a row,
  and it did: a row was `name [standing] owns workspace`, and in the half-width pane a project view
  gives that region, a 27-character name and a 50-character standing are over the width before the
  ownership line begins — so the one fact somebody opens the screen to read was the one cut off. The
  state is a heading now, said once, and a row is a name indented under it. Seats are their own
  group and come first, because a seat is a person rather than an agent.

  Grouping cost three checks their meaning, and each was moved rather than deleted: the
  highlighted-row guard went to the conversations region, which still tints selectable rows; the
  colour comparison now asks the headings, which is where a state is drawn; and the selection tests
  name the agent instead of its index, which is what they always meant.

- Previous: `specs/006-three-view-operator/` — three drill-down views (projects, one project,
  one conversation) replacing 005's four regions on one screen, on **both** surfaces; and the four
  operator operations the protocol did not have: `archive`, `unarchive` and `destroy` for a
  conversation, and `start` for a catalogued agent. The protocol work landed before the views,
  because the surfaces are clients of it. 0.18.11 — the minor moved to 0.18.0 because §10.2 is a
  versioned contract and four new operations change what a V1 broker must implement.

  **0.18.6 is lens 2's finding: a peer could forge another agent's line in a transcript.** FR-018's
  rule is structural — a header sits at column zero and nothing else does — so it survives colour
  being stripped, which is the property every test here is held to. `TranscriptRegion.Wrap` trims
  the leading space off each piece it breaks off, and that space *is* the indent the rule is made
  of. A payload is agent-written and eight thousand characters long, so its author picks where the
  break lands: seventy-six filler characters and then a plausible header rendered
  `billing@here -> orders@here (Answer)` at column zero, in a conversation `billing@here` had
  never been sent a message in.

  Colour happened to give it away, which is exactly the crutch the rule exists so as not to need.
  The fix is in `Redraw` rather than in `Wrap`: the wrap stays a pure width-fitter with its own
  contract and its own tests, and the transcript — which is where the security decision belongs —
  wraps to the width less the indent and indents every continuation.

  **The rule is now exact rather than nearly exact**, which is what made it testable. The zone
  note, the destroyed-conversation record and the three standalone messages all sat at column zero
  as this surface's own words; the third of those is a *broker's* words, since `problem` is
  whatever came back from the far end of a gRPC call. All are indented, so `ColumnZeroTests` can
  assert the whole invariant — every row at column zero is a header — rather than one attack on it.

  **0.18.11 is the same overflow in the neighbour, and the reason the name rule never bit.** The
  owner typed `TimeTracker` into the create form and met no resistance at all — because
  `FormDialog` gave every label one row and counted three rows per field, so the sentence saying
  *lowercase letters, digits and hyphens* sat on a clipped row under a label that had itself
  overflowed. Fixing `TextDialog` for the destroy warning and not looking at `FormDialog` beside
  it is what left this standing.

  So the rule is **applied rather than reported**: `FormField.Correct` folds case and drops
  anything outside the rule as it is typed, with `Corrected` saying what it did, because a field
  that will not take a capital cannot fail because of a layout. It corrects and never completes —
  an empty name, or one starting with a digit, is left for the message to refuse, since guessing
  what somebody meant is how a field comes to hold a name they did not choose.

  Two defects in that came from the existing suite rather than from thought: `NamingTests` caught
  the note never clearing, and typing the name one key at a time caught it clearing on the very
  next keystroke — `TimeTracker` corrects on the `T` and every letter after it is already
  acceptable, so the note was gone by the second character. It stays while the corrected text does.

  **0.18.10 is the destroy warning running off the screen, reported by the owner trying to delete a
  project.** `TextDialog` gave its question a label with no width and no height, so whatever it
  asked got exactly one row however long it was. Seventeen words of `Removing.Destroying` were
  never drawn — including *Every conversation in it goes with the store*, which is the whole
  consequence of the button. `ChoiceDialog` beside it always sized its label, which is why the
  dialog *before* this one reads correctly and this one did not.

  **The gate had been over this ground and missed it.** `DoorsAreReadableTests` was written two
  runs earlier for exactly these dialogs, and asked whether the description could be *tampered
  with* — never whether it could be *read*. It asks both now, at two terminal sizes, because a
  failure about fitting proves nothing at one size.

  **0.18.9 is what the Linux run found when CI could not run.** GitHub refused the jobs — *"recent
  account payments have failed"* — so the suite was run in a container instead, which is the
  practice recorded for a lapsed CI. It failed twice where Windows had passed, and one of those was
  real: `Compose` captured the seat when the composer opened, so a seat swept while somebody was
  typing still spoke, and the transcript recorded words under a name the book had released.
  **Windows passed on timing** — whether the attach stream tore down before the send landed — which
  is a race standing in for a guard. Sweeping for the shape found it twice more, in `Answer` and
  again inside `ResolveAsync`'s second form; `TakeDelivery` had always re-checked, which is what
  made it a pattern applied unevenly. The rule is now *at the send*.

  The same gate found `Removing.Describe` quoting a manifest's endpoint, store and catalogue path
  raw — the description somebody reads before typing a project's name to destroy it, which an
  escape sequence in an endpoint clears. `ProjectsRegion` had neutralised the same four values
  since it was written; the dialog never had.

  **0.18.5 is the review gate's first finding, and the lesson was already in this repository.**
  Every read finishes on a pool thread — `SelectAsync` awaits the broker with
  `ConfigureAwait(false)` and then calls a region's `Show`, which reaches `ListView.SetSource` —
  while the main loop is inside `ListView.OnDrawingContent`, which reads its source's count and
  then indexes it. Swapping the source between those two reads throws on the draw thread, which
  in a terminal ends the process. Four thousand draws against a writer alternating forty rows with
  one: 434 failures in the agents region, 1,116 in projects, 499 in agents, 322 in conversations,
  53 in the transcript.

  **`LogView` had solved this already**, and says so in a remark naming the same bug — its follower
  only queues into `_pending`, and `Drain` runs from `OnDrawingContent` on the draw thread. The
  four regions never got the same treatment. That is the argument for `GuardedList` being the place
  the guarantee lives rather than a rule each region is trusted to follow: `Replace` swaps the rows
  and the fields a row's colour is derived from together, under the same lock the draw takes.

  **`IApplication.Invoke` was tried first and reverted.** It is the toolkit's answer and it runs
  inline on the main thread, but the harness deliberately never pumps the loop, so a queued update
  never ran and `Shell.Settled` stopped meaning "the screen is true" — thirty-odd tests. A lock
  needed no harness change and cost no test.

  **The probe that found it first found nothing**, across four thousand draws, because the region
  under it had never been given a size and the row handler had not run once. `RefillRaceTests`
  keeps both counters — rows rendered, and how many different list lengths were seen mid-draw — so
  a future version that stops racing anything fails instead of passing.

  **The re-run of the same lens found a second one, by a different mechanism.** `ArrangeLocked`
  removed all four regions and added back the current view's, which mutates the subview list —
  while the main loop walks that same list in `View.CollectAll` during layout. `_screen` locked
  that method against itself and the toolkit takes no lock, so it never protected against the
  reader; eight threads changing view against three thousand draws threw `Collection was modified`
  749 times, and the route is the commonest action there is — opening a conversation calls
  `GoAsync` after awaiting the broker. The regions join the tree in the constructor now and never
  leave it; `Visible` was already doing the real work.

  That change exposed a third thing, which is why the fix is worth reading: `Lay`'s early return
  asked `SubViews.Count > 1`, a proxy for "already arranged" that was true from the constructor the
  moment reparenting stopped. The first call returned early, nothing was ever sized, and the
  surface drew a blank screen — caught by two tests out of 825, because every other path reaches
  `Relay`. It asks `_arranged` outright now.

  **0.18.4 is three things the owner reported after running the terminal, and two of them are the
  same finding twice.** The layout threshold goes back to eighty, which is where it was before
  0.18.0 raised it to two hundred as a *diagnostic* — the raise is recorded in `Shell.Threshold`
  as temporary and it stayed. The other two are about a screen saying nothing about itself.

  **There was no way back out of a project, and Escape had worked the whole time.** Both halves are
  true, which is the finding: `NavigatingTests` proves the key goes back, and nothing on the screen
  ever named it. It was in no status line and in no menu — so on a conversation held by somebody
  with no seat, where the transcript region offers no actions at all, `Offer` returned without
  opening anything and the only route out of that screen was a key nobody had been told about. It
  is on the status line of every view that has one now, before the actions and surviving their
  collapse to `?: actions`, and it is the menu's first entry. `WayBackTests` presses no Escape at
  all: that half was never broken, and testing it again would have found nothing.

  **There was no way to quit either, and a comment asserted there was.** `TopCommand` justified not
  intercepting Ctrl+C partly on the grounds that "Ctrl+Q leaves the surface properly"; enumerating
  what Terminal.Gui 2.4.17 binds at application scope turns up ten entries, all navigation, and no
  Ctrl+Q. The chord did nothing, and the only mention of it in the suite was a comment on a test
  that disposes the surface directly. **What did quit was Escape** — the toolkit binds
  `Esc => Quit` at application scope and the surface returned false for Escape at the top of the
  tree, deliberately, "to whatever else wants it". What wanted it ended the program. That is the
  exact thing `Escape_at_the_top_does_nothing_rather_than_anything_surprising` is named for, and it
  passed throughout because it asserted on `Place.Viewing` — still `Projects` on a shell that has
  just requested a stop — rather than on `IRunnable.StopRequested`. Both are fixed and both are on
  the status line; `QuittingTests` asserts on `StopRequested`, never on the session stack, because
  the harness does not pump the loop and a popped session is not observable there.

  **The status line grew a ladder, because adding the way back had silently broken the narrow
  rule.** At forty columns `alpha / Focus: Agents  Esc: back to Projects` is over the width before
  an action is named, and the guard for that rule drives the projects view — the one view with no
  place prefix and no back label, so the shortest line the surface ever draws. It only failed once
  Ctrl+Q pushed the top view one column over as well. `Shell.Fits` now walks candidates longest
  first, and what it drops before anything else is the pair the `?` menu also holds: at the narrow
  end the way out is the same way out, one keystroke further away. `LayoutTests` drives every pane
  of every view now, and asserts the two things that must never be cut — which pane has focus, and
  that `?` exists.

  **The suite opened the owner's browser, and it is the same defect for the third time.** F8 hands
  the view to a browser through a real `Process.Start`; two tests in `NavigatingTests` pressed it,
  so every run of the suite put a tab on whatever machine it ran on — reported by the owner while
  the terminal was being worked on and the suite was being run in a loop. `Launch` already records
  this being fixed once for a direct caller, and `EmptyDeploymentTests` and `ManifestMovesTests`
  each carried a comment asking authors to skip the console action for the same reason. Three
  statements of it in prose and none of them a check. `Shell.Opener` is the seam — an instance
  property, not a static, because test classes run in parallel — `Driving.Show` replaces it on
  every driven surface, and `LaunchTests` asserts both halves: production still holds
  `Launch.Open`, and a driven surface does not. The two files that stepped around the action now
  exercise it.

  **The terminal had no colour, and `ColourTests` passed throughout.** That suite reads the screen
  with every attribute stripped and asserts nothing is lost — the property that matters, and one a
  surface drawn entirely in the terminal's default satisfies perfectly. `PaintedTests` reads
  `Driver.Contents`, where a cell's attribute survives, because `App.ToString` renders the cells
  and none of their attributes: a screen with a bright red row and a screen with none produce the
  identical string, which is why a year of tests could not see this. `Colours` carries the rules —
  every colour is derived from a word already on the line, the background is the terminal's own
  except in the status bar, and an agent's colour is `Palette`'s so it is the same hue in a browser.
  A conversation's archived state is deliberately *not* dimmed: this list does not write the word,
  so a dim row would be the one fact on screen existing only as a colour.

  **Three patches, every one found by running the thing rather than reading it.** 0.18.3 is the
  startup block naming `parley top` beside the console. It named only the console, to a reader who
  is by definition at a terminal on the machine running the broker — which is frequently not a
  machine with a browser on it, and is the entire reason the terminal surface exists. Its rule is
  measured from the widest line now rather than from the address, because the address stopped
  being the widest thing between the rules.

  **The other two are about the broker's log, and both were found by reading a real one.** 0.18.1
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
