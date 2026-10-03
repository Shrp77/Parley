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
                    and Parley.Core has no I/O. Read by both; written only through
                    Parley.Setup's ProjectCommand, which `parley` and the terminal both run.
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
                    departure are — not a session flag the console keeps to itself.

                    **A seat outlives the person's presence; the claim that they are present
                    does not** (constitution 5.0.0, for specs/023). This paragraph used to say
                    a seat must not outlive the person, and that ran two things together:
                    *having* a seat, which is being addressable and having mail wait, and
                    *being in* it, which is somebody at a screen. Releasing the first because
                    the second stopped left every agent waiting on a person's decision with
                    nobody to ask. From 023 the broker provides `human-user@<project>` in every
                    project and never releases it; what `SeatBook.IdleAfter` still does is drop
                    the connection of a tab nothing has touched, because a tab left open is not
                    somebody at a desk, and that connection is now the evidence of *here*
                    rather than of the seat.

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
  moving it is a table rebuild rather than a constant. The number is 8,000, and is the first value here set
  from `parley refusals` rather than from judgement: SPEC.md §17 question 16 carries the
  evidence. It stays provisional on the same terms. Move it on that record, not because a
  message did not fit.

  **The ownership statement is the one exception, and the constitution was amended to 3.0.0 to say
  so** — the owner's call on 2026-09-26, for 020. `agents.owns` has **no bound by default**: the
  constraint stays in storage and stands down at the top of its range, so a deployment may reinstate
  one, and **the character class that stops a roster entry breaking, hiding or reordering its own line
  is not relaxed**. Taken with the arithmetic rather than around it — nineteen peers at two thousand
  characters each is about thirty-nine thousand charged to every agent on every roster listing, against
  a cap of eight thousand — and both raising it to three hundred and keeping it at one hundred were
  offered and declined.

  **It is a reduction in this control and is recorded as one.** The reasoning is the one used for
  §7.1's open-question cap and the clarification depth cap, that a cap censors the evidence that would
  justify it — and **that precedent is weaker here**, because both of those capped *counts* and 006
  says outright its cap "was never a technical bound". This one was, and measured: a roster entry costs
  about seventy-five characters of structure, so ten peers at a hundred cost roughly what one message
  is allowed to. What stands in its place is evidence rather than a number, on exactly the terms the
  8,000 above stays provisional on. **Until that evidence exists, a roster may be the largest thing an
  agent reads.**
- **No agent has authority over another.** There is no orchestrator and no lead. Any design
  that introduces one contradicts the premise.

  **The facilitator is the second asymmetry, beside the operator, and the constitution was amended to
  4.0.0 to say so** (the owner's call of 2026-09-28, for 022). Every project has Parley, a participant
  the broker provides. It is neither an agent nor a person. It may do three things and nothing else:
  ask a question; send a reply a person wrote, recorded as that person's words; and propose and agree
  that its own conversation is done. It has no operator operation. It is never the person seated and
  never receives anybody's work. It may not assign, direct, prioritise, close or override work, or
  decide which conversation an agent's own work goes into. **What it adds is who asks, never what
  asking can do.** One more operation would make it the orchestrator this line forbids.

  Its identity is the one no launch fixes. The broker fixes it for its own store, as a reserved role
  that storage refuses to every other adapter. **Auto Accept**, on by default, makes an agent's answer
  its ownership line with no person in the loop. That is a further reduction in the ownership control
  above, recorded as one. In its place are truthful provenance (`automatic`) and a review on every
  surface.
- **Credentials are references, never values.** `env:`, `keychain:`, `file:`. The broker
  resolves them at invocation and never persists or logs them. This is what makes the
  manifest safe to commit.

<!-- SPECKIT START -->
Design work for a protocol change runs through Spec Kit: `/speckit-specify` writes
`specs/<n>-<slug>/spec.md`, `/speckit-plan` writes `plan.md` beside it, `/speckit-tasks`
writes `tasks.md`. Read the plan for the feature being worked on before implementing it.
The constitution in `.specify/memory/constitution.md` restates the standing constraints
below in the form Spec Kit's checks read; this file remains the authority when they differ.

- **Latest plan**: `specs/023-always-a-seat/` — **0.36.0, being built; on `019-layout`.** **Upgrading to 0.36.0
  needs a reset** (`install.ps1 -Reset`, `install.sh --reset`): schema 1 was edited in place and its number did not
  move, so a store made by an older broker opens without complaint and keeps its old shape — no person-seat rules,
  and each project recorded as a machine. The owner's call was to say so rather than have the broker refuse such a
  store; both installers warn when they replace an older broker. The person always has a
  seat. The broker provides `human-user@<project>` in every project it serves, as it provides the facilitator
  (the owner's answer of 2026-09-29), and it is never retired, purged or released. **The facilitator is renamed
  `parley-facilitator@<project>` by the same rule, and neither has a machine**: `agents.machine` is NULL for the two,
  because a person and the facilitator are not hosted anywhere (the owner's calls in `/speckit-analyze`, 2026-09-30).
  **Having a seat and being in it are separate**: presence is still a connection — a surface drops its stream after
  20 idle minutes and takes it back on the next keystroke — but the seat no longer depends on it, and a message to
  an away person is answered `WITH_THE_PERSON`, never Unreachable. No join step on either surface. `human-user` is
  reserved, **with a limit recorded rather than hidden**: the row exists before any attach, so an attach under its
  name fires no storage rule and the guard is code trusting the caller's adapter, as §16 concedes of every name.
  **Principle III is amended to 5.0.0 first**, in its own commit. `research.md` R1–R11 carries the decisions.

  **0.36.1: the owner could not send a question into a conversation after leaving the agents to work.** The terminal
  said *"Your seat is held, but this view resolves no project to speak into … That should not be possible"*. The
  project was fine; the draft was gone. Going idle drops the stream, and `Held`'s setter threw the draft away with it —
  right while a seat went with its stream, wrong once 023 made only the connection go. The next key took the stream
  back and nothing rebuilt the draft, and `SendLine`'s first guard folded no seat, no draft and no conversation into
  one sentence that blamed the project. The draft is kept now, a missing draft says so and asks for the read, and
  Enter while away takes the stream back and sends rather than refusing. `SendAfterIdleTests` fails against 0.36.0 in
  both orders. **The sixth appearance of `Projects.Selected` standing in for `Current`** was beside it, in the reread
  after a send.

  **0.36.2: a conversation is tailed, on both surfaces** (the owner's call of 2026-10-01). It opens on the newest
  message and carries a reader at the end to each new one. **Anywhere else the reader is not moved at all** — the
  owner's nuance, and the part that was wrong in the first draft. **What is on screen decides, never where the cursor
  is**: the wheel scrolls the terminal's view and leaves the cursor behind, so a rule read off the cursor carried a
  reader who had wheeled back to the start down to row 134 (`FollowingTests` watched it). And setting the selection
  scrolls it into view, so the redraw itself moved such a reader; the view is put back now. A cursor somebody placed on
  a visible row is their place too, since a conversation that fits on one screen is always "at the bottom". **This
  reverses 0.25.7**, which left a reader who had never moved at the top, and the test that asserted it was turned round
  rather than deleted. On the web, "at the end" is the end of the newest message being on screen — not the page's
  bottom, because the composer sits below it, and not its first line, which a reader on the message before can see.
  The web script has no test, as before: this repository has no browser.

  **0.37.0: every action in the terminal is Alt and a letter, and the menus open with F10** (the owner's call of
  2026-10-01). The owner typed a `c` into a message and the conversation went to the clipboard. Copy is the
  transcript's action, so focus had reached the transcript pane — and while every action was a bare letter, the only
  thing between typing and an action was the box's guard, which holds while the field itself has focus. **A guard that
  has to be right about focus for typing to be safe is the wrong shape**; with a modifier a plain character is never an
  action anywhere. Alt was chosen over Ctrl because a terminal already owns too many Ctrl letters (C stops the program,
  H is Backspace, I is Tab, M is Enter, Z suspends on Linux) and the box's editor takes its own Ctrl keys first. That
  freed Alt by moving the menus to F10, the key most programs with a bar use; Left and Right walk between menus, and
  the bar says `(F10)` after its titles because nothing else on the row names it now. **Breaking for anybody who knew
  the keys**, hence the minor. The cost: macOS's Terminal needs *Use Option as Meta* for Alt to arrive at all.
  `KeysNeedAModifierTests` presses every plain letter on every pane of a conversation and fails against the old rule.
  Dialog keys (the tick list's `a` and `n`) are unchanged: they are inside a dialog with no text field.

  **0.37.1: the box says who a line goes to and nothing else, there is no "ask back instead", and Enter on an agent in
  the Room goes to the box** (the owner's calls of 2026-10-02). The preview read *"to: orders@host.
  orders@host answer — closes the exchange (F2: ask back instead)"*, and the owner read it as wrong twice:
  **a conversation never really stops** — *"even when it has come to a natural conclusion, some jerk might come in and
  say 'actually…'"* — so a line saying a reply closes something narrates bookkeeping as if it were the conversation;
  and F2's choice made no sense. The act is still derived (a reply to an agent's request is its Result, a reply on
  one's own request is a Clarify, anything else opens one), just not narrated. **The cost is recorded in `Draft`'s
  remarks rather than hidden**: the choice existed because a person's reply that is really a question is recorded as
  the request's Result. The stray full stop was `Reports.Sentences`, which closes every part of a report with one and
  ran the preview's lines together. Enter on an agent puts the cursor in the box and leaves the ticks alone; Space is
  the tick.

  **0.37.2: who each conversation's line goes to survives a restart.** The owner had four agents pick one to speak for
  them, ticked that one, upgraded, and every agent was ticked again — so all four answered a line meant for one.
  `AddressBook` keeps the narrowing in `terminal-addressing.json` beside the manifest, per project and conversation,
  written whenever the ticks, an `@` completion or `/add` change it; everyone is recorded as no entry, so an agent that
  joins later is not silently left out. A remembered agent no longer in the conversation is dropped rather than
  addressed, since addressing it would pull it in. **Only the program names the file** (`TerminalCommand`), on
  `CataloguePath`'s reasoning: a driven test must not write into the machine's Parley folder. **Building it found a
  live defect**: the draft was rebuilt from the previous draft whatever conversation that had been, so narrowing in one
  conversation addressed the next conversation's line to the same agent. `_drafted` keys the draft to its conversation.
  `RememberedAddressingTests` fails against both halves.

  **0.37.3: one message can be copied, with Alt+M** (the owner's key, 2026-10-02), in the View menu beside copying the
  whole conversation. It takes the message the cursor is on — header and text, without the four-column indent, which is
  this screen's structure rather than anybody's words — and the whole message even when the transcript is folded. A
  cursor on no message says so rather than copying the nearest one. "Show the whole message" moved from `m` to `e` to
  make room; it appears only when the message line has been cut off.

  **0.37.4: focus stays where a person put it, and typing counts as being here.** The owner reported focus leaving the
  box at the `9` of "PR9 yes". **The 9 was a coincidence of timing, and probing the key found nothing** — every digit,
  Shift+9, Page Up, Alt and Ctrl with 9, and a follow redraw mid-word all left the cursor in the box. **What found it was
  asking which code paths can focus the transcript**, the owner's suggestion. `FocusedRegion` changed only through the
  shell's own `Focus`, so a pane reached by the mouse had focus on screen and not in the record; every re-arrangement
  re-applies the record; and **the mail poll re-arranges every five seconds**. Opening a conversation records the
  transcript, so somebody who clicked into the box was moved back to it at the next poll. Every region's
  `HasFocusChanged` now updates the record. `FocusStaysWhereItIsTests` clicks into the box with the poll running and fails
  without it. **The lesson worth keeping: when a symptom will not reproduce from its trigger, enumerate the paths to the
  effect instead.**

  The first round of probing found the second defect: the idle rule was fed only by `OnKeyDown`, and **a focused view
  takes its keys before the shell is offered them** (0.30.4's sentence) — so every letter typed into the box and every
  arrow in a list was invisible to it, and twenty minutes of writing an answer marked the person away while they wrote
  it. The application's own `Keyboard.KeyDown` feeds it now, raised for every key before any view sees one.
  `TypingIsBeingHereTests` fails against the old wiring in both directions.

- **Previous plan**: `specs/022-parley-facilitator/` — **0.35.0, built through every story; on `019-layout` with
  019–021, and the combined review gate covers all four** (the owner's call of 2026-09-28). Parley is a participant
  the broker provides in every project. It asks each attached agent what it owns once, with nobody asking, and —
  with the project's Auto Accept switch, on by default — makes the answer the agent's line and has its node restart
  it once it is idle so peers read it. A review on both surfaces (`v` in the terminal, *Review ownership* on the web)
  shows every line and whether a person or nobody read it. **The constitution went to 4.0.0 in its own commit
  first** (b4cd0e6), then SPEC.md, then the code. `research.md`'s fifteen findings shaped the design; read it first.
  `quickstart.md` was walked on 2026-09-28 (`walk.ps1`), every check passing against packed tools with real
  `parley-mcp` agents answering over their own stdio. **The combined 019–022 review gate (T051) has not been run.**

  **What building it found that the plan did not know, each caught by a test or by reading the code:**

  - **A person's close was read as the agent's answer.** Force-closing a conversation writes a `closed` Result under
    the *receiver's* name, attributed to the broker, so the read-back took a sentence nobody wrote for the agent's line.
    It reads only the agent's own attributions now.
  - **Parley never collects its mail**, because nothing attaches as it. Every answer it received would have said "not
    collected yet" in every transcript for ever, and retention never removes an uncollected message. The read-back is
    its inbox now and records the collection.
  - **A question sent to a node would come back unreachable.** An agent asking Parley something back is mail for
    Parley, and the wake path asked a node to start `parley-facilitator`, which none can — telling the sender nothing
    could see its message. Parley's mail is `AlreadyComing`: the next sweep reads it.
  - **The question's text depended on the checkout.** A raw string literal takes its line breaks from the file, and a
    Windows checkout with `core.autocrlf` turns them into CRLF — so the words an agent received, and their hash, would
    have differed by machine. **020's `OwnershipAsk.Text` had the same latent defect**, masked because its working copy
    happened to be LF. Both are normalised in code now.
  - **The `owns-<role>-<stamp>` identifier collides** when two agents of one role on two machines are asked in the same
    second, which the sweep makes likely. Numbered when taken.
  - **Every decision about a waiting answer failed at the constraint.** Settling one kept its reason, and storage
    refuses a reason on anything but a waiting answer — so accepting and discarding could never have worked. Found by
    the first test to settle one.
  - **A person accepting before Parley's next pass failed too.** Settling marked the question accepted with no answer
    read yet, which storage refuses, so the person's own record raised an error. The line overtook the question; it is
    discarded now, and the agent still holding it is not polled for a vote.
  - **A reply to an agent that asked back was refused until the next pass**, because it read the question's recorded
    state rather than whose turn the exchange says it is.
  - **Removing a rule is how a test is checked, and three first drafts could not fail**: a store test passed against a
    schema with no table, because inserting into a missing table also throws; a mutation left an unbalanced bracket and
    broke the whole migration; and the refresh test's ten-second grace let a stopped agent finish its turn anyway, so a
    refresh that ignored the turn passed. Each was rewritten until removing the rule failed it.

- **Current plan**: `specs/019-one-way-in/` — **0.32.0. The layout, the port claim, naming a project,
  and the removal of `--db`, `-d`, `PARLEY_DB`, `--listen` and `-l` from every command. 46 of 111 tasks,
  every suite green, nothing pushed.**

  **Everything a person does to a deployment is the terminal's, and the command line no longer offers any
  of it** — the owner's call on 2026-09-25, after US5 moved the sixteen operator verbs the day before:
  *"if we're doing everything in the terminal app, then we DO everything in the terminal app."* So
  `project add`, `project set` and `project remove` went as well. `project list` stays, because both
  installers read it — for the manifest path, and to check a project's broker answers rather than that a
  task was registered — and because it changes nothing.

  **Refused at the entry point rather than deleted, and that distinction is load-bearing.** The terminal
  does not reimplement naming a project: `Naming` and `Removing` call `ProjectCommand` in process and
  print what it said, which is what makes a machine configured from a screen configured exactly as one
  configured from a shell. Deleting the arms would have taken the terminal's own create and remove with
  them. Every arm survives; what went is the dispatch in `parley`.

  **The sweep was the work, not the removal.** Twelve tests failed, every one of them the same check —
  *a refusal must name what replaced it* — pinned to a spelling that had moved. Eight further places
  still sent people to `project add` and no test covered them: `parley use`, the MCP adapter's refusal
  when `PARLEY_PROJECT` names a project the manifest lacks, the enrolment's next steps, `ProjectResolution`,
  the web UI's naming page, and the header comments written into `project.json` and `ports.json`. **A
  command is removed by grep over its own dispatch; a command is *replaced* by finding everything that
  ever recommended it.**

  **And it opened a gap that had to be closed rather than discovered.** The web UI has never been able to
  name a project — the one-writer rule its own naming page records — so a machine installed without the
  terminal now has no way to create one at all. Both installers check and say so, with the command that
  adds the terminal without stopping the broker.

  **The logon entry can be installed without a console window.** `autostart install --hidden` has the
  broker let go of its console; both installers take a switch for it, opt-in, so a machine already
  installed behaves at logon exactly as it did. Three defects on the way: the flag first *moved* the
  window rather than removing it, because a child console program inherits its parent's console and the
  web UI child therefore never needed `CreateNoWindow` — once the broker detaches there is nothing to
  inherit and Windows allocates a fresh console; `autostart install` printed the web UI's address only
  inside the command line it runs, at the end of the longest line it prints, so it is a line of its own
  now on `install` and on `status`; and **leaving the terminal with Ctrl+C left mouse reporting switched
  on for the life of that window**. Terminal.Gui enables six DEC private modes and disables them on
  shutdown, and `TerminalCommand` deliberately lets Ctrl+C kill the process before that runs — an
  argument that is right about the seat, which a dying connection releases, and silent about the console,
  which nothing else puts back. `ConsoleRestore` is the missing half, and its guard reads the modes out
  of the toolkit's own assembly rather than keeping a list: a hand-kept list is right the day it is
  written and silently wrong at the next upgrade, and the symptom of being wrong is not a red test but
  somebody's terminal.

  **The migration is cut.** US6, US7 and FR-062 to FR-064 are withdrawn: nothing has been released, so
  there is nothing to move and nothing to strand. `reset.ps1` at the root clears a developer's pre-019
  layout — it stops the broker, reads the old manifest *before* deleting it so every store goes with its
  companions, and keeps `node.json`. A `reset.sh` is owed for Linux and macOS (T080).

  **FR-020 and FR-021 are withdrawn too**, on the owner's call: no command makes a broker reachable off
  this machine. That is a real reduction, taken deliberately, and it was **reconsidered on 2026-09-24 and
  deliberately kept** — the owner asked for `--public` on the command line and in both surfaces, and
  withdrew the request once the security position was laid out. **The deciding fact is that all three gRPC
  services share one port**: `ParleyServer` binds a single URL and maps `Agents`, `Operators` and `Nodes`
  onto it, so a reachable port is not a messaging surface, it is the control plane. Anything able to open a
  connection gets `Operators.Export` (every conversation), `Operators.Purge`, `Operators.Start` (launch a
  process on that machine) and `Operators.Instruct` — which hands text to an agent that executes code in a
  repository. With no credentials on the wire (§16), **binding a network address is unauthenticated remote
  code execution by proxy.**

  **So the supported path is a tunnel, and the README says so where somebody is about to need it**: key-based
  SSH port-forward, `PARLEY_BROKER` pointed at the local end. Three designs were costed and rejected —
  binding `0.0.0.0` (fail-open: a decision taken once on a trusted LAN follows a laptop onto every network it
  ever joins, with no event and no prompt), binding one pinned interface with re-resolution at start (fails
  closed and is defensible, but still exposes the control plane), and splitting `Operators`/`Nodes` onto a
  loopback-only bind while `Agents` is exposed (the strongest of the three, and the one to revisit if this is
  ever wanted — it bounds the worst case to agent impersonation within one project). **None of them is
  authentication**, which is what §16 says the real fix is, and that is why none was taken.

  An endpoint edited into `project.json` by hand still works, because §16 permits a private network the
  operator controls. It is now **warned about on every broker start**, in the log, naming what reaching it
  grants — `ServeCommand.OnlyThisMachine`. Warned rather than refused: refusing would break the arrangement
  the protocol explicitly allows. Everything 019 owed is now built and its `quickstart.md` is walked.

  **019's review gate was never run, and will not be: there is one gate for 019, 020 and 021 together.** The
  owner's decision of 2026-09-26 — *"I'm never going to run the review gate on 019 because we're rolling all
  the changes together. The review gate will be 019, 20 and 21. The sum of all these actions."* It is tracked
  as 020's T064; 019's T110 is deferred into it and must not be run or ticked separately.

  **That is the correct reading of this file's own rule rather than a deferral of it.** The lenses run *after
  the last edit*, and three features on one branch means an early gate would have been three gates with two of
  them invalidated by the next feature's commits — which is 015's recorded cost from the other side, where
  post-gate installer changes cost nine more defects in the confirming runs. **What it costs is that the
  surface under review is three features wide**, so each lens covers more ground per run and a finding
  anywhere resets that lens across all of it. Nine clean runs remains the floor, not the expectation.
  `tasks.md` orders them by what blocks what rather than by requirement number: the MVP is phases 1–3, an
  operator brings up a deployment by typing one name, with none of the removals, the migration or the
  enrolment work. Three of its tasks exist because the plan found traps rather than because a requirement
  named them — the removal stub that puts a verb back into `Program.cs` and fails `PluginTests`, the test rig
  conversion that is 26 files with one needing a decision rather than an edit, and the two races that each
  carry a probe which must be watched to fail first. One place for a
  machine's files and one door per job. A project becomes a folder under one root, named by the project, holding
  its own configuration and its own store — so naming a project is the whole of creating one, and `--db`,
  `--listen`, `PARLEY_DB` and every other way to point outside a project are gone, from every command rather
  than from some. A port is handed out from a record at the root,
  claimed under a hold that nothing on the serving path ever takes. Starting Parley serves everything or Parley
  is not running. And the command line sheds sixteen verbs a person cannot use, because there is no
  `parley conversations` and every one of them takes an identifier nobody can obtain.

  **Three of the four things the spec feared were already solved here, and reading the code rather than
  reasoning found them.** The serve watcher already *polls* through a `reread` function, so feature 015's
  filesystem-watching hazards do not apply at all — only `reread` changes. `StoreLock` already holds a file
  exclusively across Windows and Linux, so the port claim is that mechanism held transiently. And
  `project remove --destroy` already enumerates everything belonging to a store, with the ordering the migration
  needs: take the lock first, as the check that nothing is serving.

  **What the inventories found that nobody predicted.** `PluginTests` asserts the verb sets in `Program.cs` and
  the operator skill are exactly equal, extracted by regex over the whole source — so a removal stub reading
  "`parley export` moved to the terminal" puts the verb back and fails the build. The five `/parley:files-*`
  slash commands execute `parley files` on the agent's behalf, so that verb stays. And the test rig cannot call
  `project add`, because the band is per machine while a root is per test: parallel classes would each be handed
  19000. The conversion is 26 files, not the 14 a grep suggested. Also recorded: the live deployment's ports,
  51707 and 53283, sit inside both operating systems' ephemeral ranges, which is a latent logon-time bind failure
  the new band avoids.

  **What is built, and what each defect cost.** A project is a folder under `<root>/projects/` named by the project,
  holding its own `project.json` and its own `parley.db`. **Two invariants stopped being checks and became facts**: a
  name cannot repeat because it is a directory entry, and two projects cannot share a store because a store is derived
  from the name. `Collisions` keeps exactly one rule — two projects on one endpoint — because that is the one the layout
  cannot prevent. `parley project add orders` is the whole command; `--db` is gone and is *refused* rather than ignored,
  because every deployment on this machine was made with it.

  **A port is claimed, not probed.** `PortLedger` hands one out under an exclusive hold on `ports.json` — `StoreLock`'s
  mechanism borrowed whole, with both of its rules: the file is never deleted, because on Linux a lock belongs to an open
  file rather than a name, and a failed open is contention only when the file exists afterwards. One difference, and it is
  the only one: two brokers on one store is a defect and is refused outright, while two people running `project add` at
  once is a Tuesday, so the claim retries under a budget. **Mutation-checked**: with the hold opened `FileShare.ReadWrite`
  instead of `None`, twenty-four concurrent claimants were handed 19100 nineteen times. The band is 19100–19355 for the
  reason recorded just above.

  **Six defects, and the compiler was happy with every one.**

  - **A first draft of `ProjectFolder.Write` lost four rules the manifest had paid for** — unknown keys surviving a write,
    the byte order mark and line endings surviving, an unparseable file refused rather than replaced, and the write
    committed by rename. It was `File.WriteAllText` over a freshly built object. For a while there was **no atomic write
    anywhere in the repository**, and every suite was green. `Keys_this_version_does_not_know_about_survive_a_write` found
    it.
  - **The grant gate and the project writer stopped excluding each other.** `TryUpdate` moved its lock into `projects/`
    and composed the path inline while `Catalogues.GateAsync` went on calling `LockFor` — two implementations of one rule,
    agreeing until one changed. **018's `LaunchKey` lesson, repeated inside one feature.**
  - **And that gate would never have been taken at all.** Its guard was `File.Exists` on what is now a directory, so it
    returned null every time and enrolment ran unguarded. **A guard that fails open is worse than no guard**, because what
    it protects goes on looking protected.
  - **The serve watcher noticed nothing.** It stamped one file's write time; that file no longer exists, so the stamp
    never moved and a machine went on serving whatever it started with — while `project add` reported success and named
    the port it had been given. SPEC.md's "served within seconds" was silently untrue.
  - **The console had the same defect, and so did the terminal.** `BrokerDirectory` asked `FileInfo.Exists` about a
    directory. **Three copies of one rule, all three broken the same way, none of them by a change anybody made to them**
    — so the rule is `ProjectFolder.StampOf` now and all three ask it.
  - **The machine's catalogue and the broker's log moved one directory up**, because both derived "the directory holding
    the manifest" — the same place as the root, said the other way.

  **Every one was found by a test rather than by reading.** `REVIEW-LEDGER.md` carries each with the probe that produced
  it; the review gate itself has not started.

  `specs/020-what-an-agent-owns/` follows it. **Planned in full; its storage is built and green, and its
  surfaces are not started** — 0.33.0, **schema 1**, protocol 0.7 unchanged because 0.7 has never been
  released and §18's precedent is to record that rather than spend a number. An agent is *asked* what it owns and the answer is discussed to agreement, replacing a worksheet a
  person filled in from memory. It amends SPEC.md §17 question 10, which is closed and says the field is not
  self-declared — argued there rather than stepped around, and the cost is recorded: the claim narrows again,
  and anchoring gets worse rather than better.

  **Three things carry its risk and all three were settled by probe or by the owner rather than by argument.**

  **The hundred-character bound on `owns` goes, and that is a deliberate reduction in a control the
  constitution names.** Principle IV calls a roster entry text that crosses and says a bound is part of its
  design, and `001_initial.sql` records the hundred as *measured* — seventy-five characters of structure per
  entry, so ten peers at a hundred cost about what one message is allowed to. The owner took the decision on
  2026-09-26 with the arithmetic in front of them (nineteen peers at two thousand characters is ~39,000 against
  a cap of 8,000) and declined both raising it to three hundred and keeping it. **The spec's 006/007 precedent
  is weaker than it reads**: both of those capped *counts*, and 006 says outright its cap "was never a
  technical bound". This one is. So `plan.md`'s Constitution Check carries it as a **finding**, and the
  constitution should be amended to 2.1.0 *before* implementation — 015's amendment is the precedent, and
  `/speckit-analyze` found changing it inside a feature's tasks a blocking conflict.

  **The bound cannot be moved by altering anything, and four routes were probed before the question was
  handed back.** The number is a literal inside a table-level CHECK on `agents`; SQLite cannot alter a CHECK;
  `ALTER TABLE DROP COLUMN owns` is refused because `owns` appears in one; a trigger can refuse but never
  permit; and `PRAGMA writable_schema` + `UPDATE sqlite_master` is **accepted and changes nothing**, on a fresh
  connection too, while `integrity_check` still returns `ok` — the worst shape there is. What was left was
  rebuilding `agents` outside a transaction, because `PRAGMA foreign_keys = OFF` is a no-op inside one.
  **006's blocker holds here with more force than where it was written**: `agents` is a parent of eleven tables
  and **six of them refuse a cascade** — `invocations`, `exchanges`, `messages`, `conversation_participants`,
  `result_items`, `result_sources`, which are exactly the tables a used store has rows in. Three triggers name
  `agents` and two are on other tables, and the RENAME validates every trigger in the schema, so with them
  standing it fails outright.

  **Two of those probes were unsound before they were right, and both are recorded rather than tidied away.**
  The first seeded `agent_credentials`, one of the few children declared `ON DELETE CASCADE`, and reported that
  006's blocker does not apply — it had picked the one child that cannot refuse. The second reported
  `bound rewritten: True` while comparing the whole DDL string, which the table rename had already changed, so
  it said True with the bound untouched and the rebuild then went on to refuse 400 characters.

  **So there is no migration: the schema is one file again and its number is 1.** The owner's call once the
  rebuild had been priced — *"don't worry about migration. Go ahead and make the changes and call it schema #1.
  Nothing we have is worth keeping right now."* Migrations 002–011 were folded into `001_initial.sql` and
  deleted. **That is available exactly once and has now been used**: nothing has been released, so no store
  outside a test process held anything worth keeping, and it is the same ground 011 was edited on rather than
  being followed by a 012. **From the first release every schema change is a migration again**, which is what
  `MigrationDiscoveryTests` moving off 1 is there to notice.

  **Entries below this one name migration files by number and those files are gone**, as are the schema
  numbers in SPEC.md §18's earlier entries. Both are left as written: they record what a change did at the
  time, and rewriting them would turn a record of how this got here into a record of something that did not
  happen. What each of those migrations decided is in `001_initial.sql` — the rules inline beside the columns
  they constrain, and the file-level arguments carried into its header.

  **The collapse was derived, not hand-merged.** A store was migrated through all eleven and its schema read
  back out of `sqlite_master` in creation order, because hand-merging eleven files is how a collapse quietly
  loses a trigger — and four tables existed in two shapes across that sequence. Verified by a one-time
  comparison against a store built the old way: **41 shared objects, with identical names and identical creation
  order**, plus the one table this feature adds — 20 tables of ours beside SQLite's own `sqlite_sequence`,
  7 indexes and 14 triggers; twenty-two differed on raw bytes and twenty-one of those **only
  by carriage returns inside the text SQLite stores in `sqlite_master`** — the later migrations were LF files
  and this one is CRLF. **Exactly one differed for any other reason: `agents`, by the one number this feature
  changes.** That comparison cannot be kept as a test, because the thing it compared against has been deleted,
  and `001_initial.sql` says so rather than claiming a guard that does not exist.

  **It cost fifty-three tests, in six files whose whole subject was an upgrade path that no longer exists.**
  Each built a store at schema 7, 8, 9 or 10 from the embedded migrations. The rules they protected are still
  held: `ItemIdBoundTests` for the item-id bound that lens 1 found, and `OldShapeRendersTests` and
  `OldShapeOnThePageTests` for rendering a pre-0.7 payload, which needs no store at all. Three dead helpers
  went with them — `Work.OldRequest`, `Work.DoneWhen` and `Requests.OldRequestOf` — each of which carried a
  comment describing a schema-10 route that had stopped existing, which is how a reader concludes a check was
  lost by accident.

  **Removing the bound had a blast radius the plan had not costed, and it is the useful part.** Four places
  built a string of `MaxCharacters` characters, which at `int.MaxValue` asks for two gigabytes, and two printed
  the number into prose a person or a model reads. So `OwnershipStatement.Bounded` exists to keep 0.25.8's rule
  — nothing prints the sentinel — and the plugin's setup guidance now states *why* to be brief instead of a
  number that is gone. **`Adding.OwnsProblem` was the interesting one**: it compared a length and checked
  nothing else, so a statement carrying `U+2028` passed the field and was refused by the database. With the
  length gone it would have been a guard checking nothing while still looking like a guard; it asks
  `OwnershipStatement` now, which is the one implementation. **Two review-gate probes had to be inverted rather
  than deleted** — `ProbeThirtyOne` and `ProbeTwelve` both asserted a length was refused, and left alone both
  would have gone on passing against nothing.

  **The operation is built end to end, and it is `record_ownership` rather than `accept_ownership`.**
  §10.2 opens by warning about two operations whose names invite confusion across the agent/operator
  boundary — which is why `force_conclude` is not `conclude` and `registry` is not `agents` — and
  `AcceptOwnershipAsync` sits one reading away from the agent-facing `AcceptAsync`. The guards pass
  either way, since `ContractSeparationTests` compares names exactly, so this was the convention rather
  than the build. **The new name is also the more accurate one, which is what settled it: the person did
  the accepting.** A name claiming the operation did it describes the wrong actor for something that
  only writes down somebody else's decision — and it does not even write `owns`.

  **What it deliberately cannot do is the point.** It records the statement, which of three routes
  produced it (`proposed`, `edited`, `composed`), how long the *agent's own* message was, and the
  conversation it was agreed in — and it **does not write `agents.owns`**. The registry is still filled
  from the launch environment on attach, so *pending* is a comparison of two observations rather than a
  third flag somebody has to clear, and the write and the live read are **one act under one transaction**,
  because a second call would let an attach land between them and report a line as already live that
  nobody had yet read. `RecordingOwnershipTests` drives thirteen cases, each naming what it must fail
  against; the first is that `owns` is untouched, which is the obvious thing to write and is the route
  around Principle III that §17 question 10 exists to keep shut.

  **Two things the fixture found rather than the design.** `agent_ownership_accepted.conversation_id` is a
  real foreign key, so a test naming a conversation that does not exist is refused — which is correct and
  load-bearing: FR-011 says an accepted line must be traceable to the discussion that produced it, and an
  identifier pointing at nothing is not traceable, so a fixture that stubbed it would have tested a weaker
  rule than the schema holds. And the dump that produced `001_initial.sql` left **forty doubled statement
  terminators** in it, which SQLite tolerates as empty statements — invisible, harmless, and noise in what
  is now the canonical schema. Collapsed.

  **Both surfaces bind it, and both only compose so far.** `DriftTests` requires the operation in both
  trees with no exemption, and it is: the terminal has `Ownership` on the agents pane under `n`, offered
  through the action table so the status line and the menu get it by construction, and the web UI has an
  `ownership` action on `Act`. **Neither asks the agent anything yet** — what they record is a person's own
  line, as `composed`, and both say so, because question 10's amendment turns on telling a person's words
  from a model's.

  **And accepting now actually reaches peers, which it did not for one commit.** `record_ownership` stores a
  decision and nothing else; the registry is filled from the launch environment on attach, so until
  `OwnershipLine` existed an accepted line was `pending` for ever — a path that was half-built in the way
  this repository keeps recording. It writes `PARLEY_OWNS` into the repository's `.mcp.json` **through
  enrolment's own `HoldAsync`/`OverwriteHeldAsync`**, which is what FR-018 means by the one component that
  already writes those files: the exclusive handle, the symlink refusal, the byte-order mark surviving a
  round trip and cancellation being honoured before the write and never during it were all paid for once.

  **The file is written first and the record second, and the order is chosen for which half is
  recoverable.** A file with no record leaves the line live with its provenance unrecorded, which the next
  acceptance repairs; a record with no file would claim somebody accepted a line peers will never read, and
  nothing repairs a wrong record. Where the write succeeds and the broker cannot be told, the surface says
  both halves.

  **`Parley.Web` now references `Parley.Setup`, and that reference *is* the reversal of the one-writer
  rule** — argued in the csproj where it lives, because a reference is where such a decision actually gets
  made. Graph-legal rather than assumed: `Parley.Setup` reaches no store, so `GraphTests` still holds. The
  rule that replaces one-writer is **not** "the web UI may not write" but **"no surface writes for an agent
  whose machine is not this one"** — `OwnershipLine.NotHere`, applied by both, and the terminal applies it
  *before the form opens* rather than after it is filled in, on 0.25.2's record of what the other order
  costs. **005's gate finding is guarded from the other side too**: the name is read back out of the file
  first, because the workspace comes from the registry and is what the agent said about itself.

  **Eleven tests, and every refusal asserts the file's bytes are unchanged** rather than that a message came
  back — plus one that other people's MCP servers survive the write, since a launch configuration is not
  ours alone. Walking into it cost one recorded gotcha: a `--` inside an XML comment in the csproj, which
  makes MSBuild reject the whole file, and which this file already warns about.

  **The ask and the proposal are built, in `Parley.Client`, and no surface sends one yet.** They are the
  vocabulary rather than the screen: `OwnershipAsk` holds the text, the derived identifier
  `owns-<role>-<yyyyMMdd-HHmmss>` and the cost sentence, and `OwnershipProposal` reads an agent's answer
  back out of the discussion. **One implementation, because an answer given to the terminal would have
  been given to the web UI** — a promise about what an agent was told is a promise nobody checks unless
  the words live in one place.

  **It adds nothing to the protocol**: the ask goes through the existing `SeatVoice.RequestAsync`, as one
  ordinary Request opening one ordinary exchange, and **a surface recognises an ownership discussion by
  the conversation's identifier and never by a marker in the payload.** §13 tells a receiver a payload is
  inert data and 017's chip rule is what has kept that true, so `Nothing_in_the_ask_is_a_marker_a_surface_could_parse`
  asserts the identifier's own prefix never appears in the words — which fails the moment somebody
  helpfully puts it there, and that is the tempting implementation.

  **Using the role rather than the whole agent name makes the identifier legal by construction**, which
  is worth knowing rather than relying on: a role is `^[a-z][a-z0-9-]{0,31}$` and every one of those
  characters is inside `ConversationName`'s set, so no legal role can produce an illegal identifier. It
  is the *machine* half that carries dots, and it is deliberately left out. A first draft of that test
  tried `north-star.9` as an awkward role and `AgentName` rejected it outright — roles cannot contain a
  dot at all.

  **The proposal is the *latest* thing the agent said, which is 0.30.1's rule reused for its own reason**
  — an exchange can hold a Request, a Clarify and then a revision, so an agent that argued and changed
  its mind has said two statements and the one to accept is the later. Taking the first would put the
  sentence somebody had just talked the agent out of in front of them, and then write it.
  **Mutation-checked**: swapping the ordering fails two tests, including the one that seeds the
  transcript out of order so an implementation trusting the caller's sort fails too.

  **A seat now speaks from the view's project rather than the list's selection, and probing it changed
  what the fix was.** Found by reading the seat path while wiring the ask: `Shell.Voice()` and
  `Shell.Tick()` both asked `Projects.Selected` which project to speak into, and `Shell.Current` exists
  for exactly this — *"the view's project, falling back to the list's selection … reading the list made
  those actions depend on a region nobody is looking at"*. **This is the fifth appearance of the shape
  CLAUDE.md already records four of.** It was latent rather than live, because the one route a person
  has into a project view is selecting a row, so the list is always filled first — a guarantee resting
  on navigation rather than on the seat, and **021's menu bar is the next thing that could reach a view
  another way.**

  **`Current` has its own failure mode, and finding it is why a blind swap would have been wrong.** It
  resolves the place's project through the directory, so a place naming a project the directory does not
  hold returns null where the list would have returned something. The swap turned **seven tests red**,
  and every one was a fixture that put the shell in a project view for a project its directory did not
  contain — a state production cannot produce. Two of them were simply inconsistent with
  `Walking.GoAsync`, which places the shell in `alpha` while they named `acme`; they are
  production-shaped now rather than merely passing. **That null is correct behaviour**: a project removed
  while somebody is inside it is exactly when speaking must stop, because acting on the wrong project is
  the one mistake this surface cannot take back — the transcript records it.

  **What was wrong was never the refusal; it was the sentence.** Every send that found no voice said
  *"Your seat was released"* — a statement about the seat, printed at a moment when the seat is
  demonstrably held two lines above. **0.29.1 records that exact sentence being the defect.**
  `Shell.SeatWithoutProject` says what is true, what to press, and that it is a defect worth reporting
  rather than anything the person did. `SendLine`'s first guard had also collapsed three different
  absences — no seat, no draft, no conversation — into the seat's. **Mutation-checked**: putting
  `Projects.Selected` back fails the probe.

  **The ask is on a screen now, on both surfaces, and it is two actions rather than one.** The agents pane
  asks — `a`, where the row is the agent — and a transcript accepts, under `n`, which is what ownership is
  keyed to in the agents pane as well, because it is one operation reached from the two ends. **An ownership
  discussion is recognised by the conversation it opens and never by anything in a payload**, which is what
  keeps §13's promise that a payload is inert data; the terminal offers the acceptance only in a conversation
  whose identifier `OwnershipAsk.Recognises`, and a test asserts an ordinary conversation does not offer it —
  accepting there would write whatever that agent last said about anything.

  **The machine rule deliberately does not gate the ask, and saying why is the useful part.**
  `OwnershipLine.NotHere` guards *writing a file on the agent's machine*; asking an agent a question is not
  that. A broker reaches agents on machines neither surface will ever touch, so refusing to ask them would
  withhold the operation from exactly the deployment it is worth most in. The refusal belongs at the accept,
  and that is where both surfaces apply it.

  **Whose line a discussion is about comes out of the discussion.** `OwnershipProposal.Subject` matches the
  identifier against the agents the messages name, and **never parses the identifier back into a name** — it
  carries a role and deliberately no machine, so `owns-orders-…` alone cannot say which `orders` was asked,
  and guessing `orders@<this machine>` is 0.20.2's recorded defect of seating somebody at another desk. Two
  agents of one role in one discussion returns **null rather than the first match**, because what follows an
  acceptance is a write to somebody's launch configuration. An agent asked but still silent is still the
  subject, found as the *recipient*, so the surface can say "not answered yet" about a named agent rather
  than about nobody.

  **The discussion is read again at the accept rather than taken off the screen.** A transcript is followed
  every three seconds, so what was drawn is what was true a moment ago — and an agent that revised in that
  moment would have had the sentence it retracted written to its launch configuration. One read answers both
  questions, which also avoids the two-reads-on-one-keystroke shape 017 removed from the conversations list.

  **Which words are whose is a comparison, not a flag.** The field opens holding the agent's own sentence, so
  accepting is one keystroke and editing is ordinary typing; `Ownership.Chosen` compares the two and records
  `proposed` or `edited`. A flag would be right every time somebody remembered to set it. The trim is the
  dialog's and not the person's, so surrounding whitespace is not an edit — while a changed letter, case or
  dash is, because what is being claimed is that the words are the agent's. **The length recorded is the
  agent's own message**, never the accepted one, which is 0.29.0's lens 2 applied where it was aimed: the
  evidence table exists so a bound can one day be set from what agents write.

  **The ask is shown before it is sent**, on both surfaces, because FR-003 is a promise about what the agent
  was *told* — a surface that summarised it would be the one place that promise could quietly stop being
  true. The web UI describes it on a GET and sends only on a POST, so no link can spend anybody's turn, and
  **a missing seat is said plainly rather than falling through to the operator's voice**: an instruction in
  place of a message puts the question in front of the agent with nothing in the transcript and nothing owed
  back. The terminal offers the ask whether or not a seat is held, which is 0.28.2's defect guarded from the
  other side — the action hidden until a seat existed was the reason the owner reached for Instruct.

  **The web UI's three tests are all run with no broker listening, and that is the point rather than a
  compromise.** Each asserts a guard applied *before* anything is sent or written, so a console that deferred
  either check downstream reports a transport failure instead of the sentence asserted.

  **The acceptance is driven end to end over a socket, and the fixture for it already existed.** I recorded it
  as owed and needing a fixture the size of `FileLocationFixture`; `Parley.Server.Tests` has `LoopbackBroker` and
  `AttachedClient`, which is where T027 said the whole acceptance belonged. The agent proposes through `Agents`,
  the person records through `Operators`, and **nothing opens a store** — everything is read back through the
  wire, which is the stronger assertion anyway: a row nobody can read through the protocol is not evidence the
  protocol records anything. **The fixture gained an `owns` on the attach**, because that is the only route a
  line has into the registry (§12.3) — a test needing an agent whose peers already read one has to attach as
  one.

  **`AgentsCannotSetTheirOwnLineTests` holds §10.2's boundary structurally.** It matches the *words*
  `Ownership`/`Owns` rather than an exact method name, because the mistake being guarded is an operation added
  under a name nobody predicted — a test naming only `RecordOwnership` passes against `SetOwns`. **Mutation-checked
  by putting the rpc on `Agents`**: both new guards fail, and so does
  `BrokerV1Tests.An_agent_cannot_reach_the_operator_surface`, which already covered reachability where these cover
  the name on the generated client. Complementary rather than redundant, which is worth knowing rather than
  assuming.

  **A pending line is now visible, and that needed the registry read to carry both halves.** `registry()` returns
  the accepted line beside the live one, because the surfaces reach no store and a comparison needs two things to
  compare. `AgentRecord.OwnsPending` is the comparison; `Roster.PendingMark` is the one sentence both surfaces
  append. **Both halves travel rather than a `pending` flag** — a flag carries the comparison and loses the thing
  compared, and is a third state somebody has to clear. **The roster goes on showing the live line**, because it
  is what peers route by; showing the accepted one would tell a reader that peers route by a sentence they have
  never read. **The mark clears itself**: an agent that comes back carrying the line makes the two agree, with no
  event and no bookkeeping.

  **The restart is offered and not taken.** §12 says the process is what held everything the agent had learned,
  so accepting a sentence about scope is not a reason to spend that silently; leaving it is a complete answer and
  the safe one is under the cursor. It reports **what was asked for** rather than claiming the line is live, since
  `Operators.Start` asks a node to start a role and whether the agent attaches and registers is a different
  question — 0.25.2 records a screen predicting a registration instead of checking one.

  **And it fixes a defect the ask shipped with.** `Shell.Registered` read the drawn row's ownership cell, which is
  a sentence for a person — `not stated` where an agent has said nothing. The review dialog would have told
  somebody peers were reading the words *not stated*, and accepting would have written them into a launch
  configuration. It asks the registry record now, which is why the agents region keeps the records it is given and
  not only the rows it drew. **A row is what a person reads and never what a decision is made from.** The restart
  also goes on the surface's own work queue rather than a bare `Task.Run`, because `Shell.Settled` is what makes
  every driven test deterministic and 005's gate found work escaping that chain.

  **Still owed**: the discussion (US2), the bulk ask (US3), the neighbours column (US4), and Phase 7's polish —
  the refusals sweep, enrolment no longer asking for a line, the quickstart walk, and asking this repository's own
  agent what it owns.

  **An ask is recognised by the conversation it opens, never by a marker in a payload** — §13 promises a
  receiver that a payload is inert data, and 017's chip rule is what keeps that true. The identifier is
  derived (`owns-<role>-<timestamp>`), and the current proposal is the **latest** thing the agent said in that
  exchange, which is 0.30.1's rule reused for its own reason.

  **Both surfaces write the launch configuration, reversing the one-writer rule the web UI's naming page
  records and 019 upheld six commits ago** — the owner's call. Designing it found the constraint that makes
  the reversal defensible and that applies to the terminal just as much: **a launch configuration is a file on
  the agent's machine, and neither surface is guaranteed to be on it.** So the rule is not "the web UI may not
  write" but "no surface writes for an agent whose machine is not this one", checked identically by both. And
  it restates a defect already paid for — 005's gate found a withdrawal deleting a *different* agent's launch
  configuration because the workspace came from the registry, so the name is read back out of the file first.

  One new operator operation, `Operators.AcceptOwnership`, and **five existing reads** — which is the evidence
  that FR-002's "no new operation for the ask" was achievable rather than aspirational. It records the
  acceptance and **does not write `agents.owns`**: the registry is still filled from the launch environment on
  attach, so "accepted but not yet live" is a *comparison* of two facts rather than a third flag somebody has
  to remember to clear. The file is written before the record, because a file with no record is repaired by a
  later ask while a record with no file claims a person accepted a line peers will never read.

  `specs/021-terminal-menu-bar/` is **built, and shipped as 0.34.0; the walk of 020 below took it to 0.34.1** — a minor because that is what each feature
  on this branch has taken (019 was 0.32.0, 020 is 0.33.0), and nothing here is breaking. Every action was
  behind `?`, and `?` was behind knowing about `?`; there is a bar on the top row now, always visible.

  **0.34.2 is the owner's first use of 0.34.1, and most of it is things the suite could not see.** The Alt
  keys the bar advertised did nothing (`Key.AsRune` is empty for a key carrying Alt, and no test had pressed
  one). A menu is now a real dropdown rather than a dialog. The toolkit's own `MenuBar` was tried first and
  made `RefillRaceTests` fail one run in three against ten clean runs on `HEAD`, because its titles are views
  in the shell's tree; `MenuBarView` draws its own row and drops a `PopoverMenu`, which lives outside that
  tree. Project is the leftmost menu. Enrolling a folder moved into the project, as
  **Enroll agents from folders** (`f`), and its picker can now choose a folder that holds repositories rather
  than only one that is one. The tick list pages ten at a time, and Enter works on a screen, where the list
  had been taking it before the dialog saw it. Two keys in the agents pane were doubled (`a`, `w`);
  `NoTwoActionsShareAKeyTests` holds that. The scan no longer reads git history: it was the whole of a
  three-second freeze on a folder of 33, for a line nothing showed. The installers end with the terminal's
  flow, and every sentence that sent people to `parley terminal` now says `parley`, which is what is typed.

  **0.34.3: the owner enrolled four agents and none registered, and the cause was a race nothing had
  tested.** Enrolment writes the catalogue and asks for each start in the same breath; the node re-reads
  its catalogue on a two-second poll. So the first ask reliably reached a node that did not know the role
  yet, it answered "not something I can start", and that was taken as final -- while the broker's log
  showed the node rejoining with all four a moment later and nothing left that would ask again. Both
  starters now keep asking for fifteen seconds (`Registering.CatchUp`, `Starting.CatchUp`), and the fix was
  confirmed against the live broker: one agent started and registered in 1.6 seconds. And an enrolment now
  says what it is doing while it does it -- which agent of how many, and whether it is waiting for the node
  or for the registration, with the seconds counting -- because the owner's next report was that after
  confirming, nothing happened for a long time. **That progress is on each agent's own row**, on the owner's
  call of 2026-09-28 (`AgentsRegion.Activity`), and the message line is left for what went wrong; and the
  status line no longer names `?: actions`, because the menu bar is that list now. Actions report in one
  line now, and the facts moved onto the projects rows -- address, answering, agents running, conversations
  open -- where they stay rather than scrolling away.

  **The action carries metadata and does not carry the UI**, which is the owner's D1 in their own words.
  `RegionAction` gained `Category`, `Group` and `Order`, and `MenuRow` turns those into menus — sorted by
  `(Group.Order, Order)`, a separator wherever the group changes. The status line, the `?` dialog and the bar
  are three readings of one list. **`ActionGroup` is a record rather than a string** so a run's position is a
  property of the run; a bare string would have every action repeating it, and two in one run would disagree
  the first time one was edited.

  **`Category` is nullable rather than defaulted, and that is the load-bearing choice.** An unclassified
  action must still reach the screen, but filing one under a category nobody chose would be the bar inventing
  a fact — so it goes visibly under `Other` and a test fails the build when anything lands there. **The region
  supplies the default**, because stating it at forty-five sites is forty-five chances to disagree; what states
  its own is the handful the owner's table files elsewhere — health, the log and the web UI are the
  deployment's however they are reached.

  **Alt and a category's first letter opens a menu, and a menu is a `Prompting.Choose` dialog.** Alt because a
  focused view is offered a key before the surface's own handler is (0.30.4), so the Conversation menu had to
  open while somebody is typing into the composer where `c` is a `c`. The dialog rather than the toolkit's own
  menu bar because it inherits what was paid for once: a description that sizes itself, a list that scrolls,
  labels that survive an underscore, and a session a test can drive.

  **Three findings, two of them defects rather than tidy-ups.** `DeadSurfaceTests` reported that
  `ActionCategory.Help` existed and nothing used it — the category was built and the actions the table puts
  under it were not, so *Keys* and *About* exist now, About naming **this tool's** build rather than the
  broker's. Giving About an F1 chord **would have advertised a key nothing dispatched**, because `Global()` was
  not in the key handler: a key the surface names and then ignores is worse than an action nothing offers, since
  one can still be found and the other teaches somebody the surface is broken. And **the bar's row broke the
  regions' floor** — `Show()` computed the message line's room as `height - 1 - LeastRegionRows`, counting the
  status line and not the new row, so on a short terminal the message grew into the six rows the regions are
  promised. Caught by a test that already existed, fixed at the arithmetic, and `BarRows` is a named constant
  because five sites have to agree about one row.

  **One of my own tests was the mirror of a probe that cannot fail: one that could only ever fail.** It moved
  focus on the projects view, which has exactly one pane, so `Focus(1)` moved nothing and the bar was rightly
  identical. It drives into the project view now, which has two.

  The spec's three open questions are settled in it from precedent rather than taste: availability shows the
  action with its reason rather than greying it — 0.28.2 records a hidden action being why the owner reached for
  Instruct — accelerators are shown because D2 makes them unambiguous, and the mouse is the toolkit's, with
  `ConsoleRestore` already reading the modes out of its assembly rather than keeping a list.

  **020's `quickstart.md` was walked on 2026-09-27 (T062), and it found three defects — two of them made 020's
  whole web UI half untrue. 0.34.1.** 77 checks against tools packed and installed into a scratch path, with a
  root, a store and a port of their own; 72 passed and every one of the five failures was one of the first two.
  **The confirming re-walk against the fix is 79 of 79.** `walk.ps1` sits beside the quickstart, as 019's does.

  - **All three ownership actions were performed by the page model and linked from no page at all.** The agent
    page offered *Instruct*, *Retire* and *Purge*; nothing anywhere named the ask, the bulk ask or the
    acceptance, and `Act`'s `OnGet` answered the acceptance with *"This is not something the console does"*
    above a form with no field to type a line into, while its `OnPostAsync` would have done it. **`DriftTests`
    could not see it and is right not to** — it compares the operations `BrokerClient` offers against the two
    surface trees, and `RecordOwnershipAsync` genuinely is in this tree, in the page model. What it cannot know
    is whether anything a person touches leads there. **The fourth appearance of the shape this file already
    records three of**, after `Shell.Said`, the colourless terminal and the line pane with no size: a property
    the suite holds to and the screen never shows. `EveryActionHasADoorTests` reads the arms out of both
    switches and fails the build when one is performed and unlinked, or linked and not performed, or done on a
    POST and described on no GET.
  - **And the acceptance asked for the wrong launch key.** It passed `project: null` to
    `OwnershipLine.WriteAsync`, which looks at the unnamed `parley` entry — so every repository enrolled into a
    named project, meaning every repository since 009, was refused with *"has no entry for this agent. Nothing
    has been written."* The terminal passes its project and wrote the same file happily, so the two surfaces
    disagreed about a repository they were both looking at. **It was invisible because the three existing tests
    all run with no broker listening**, which is deliberate and right for what they check — each asserts a guard
    applied before anything is sent — and the write is downstream of a registry read, so no test that refuses to
    let the registry answer can reach it. `OwnershipConsole` is the fixture for that gap; the legacy unnamed key
    is asserted too, because losing it is how a fix for this breaks the case it replaced.

  - **And `parley registry` showed the live line while saying nothing about one already accepted.** FR-020 says
    *any* surface showing the line must make that clear, and `DriftTests` names the command line as the third
    surface — and **this is the listing somebody runs to check that an acceptance worked**, so what it showed
    them was the old line and no reason to think anything had happened. Low, because no surface that *accepts*
    a line was wrong and the mark clears itself on the next attach; fixed anyway, because a known contradiction
    of the spec going into the combined gate means the gate reviews a surface the document forbids.
    `OperatorCommands.RegistryAsync` reads through `BrokerClient` now, which is what gets it
    `AgentRecord.OwnsPending` and `Roster.PendingMark` — **the comparison and the sentence, not a second
    spelling of either**, since three spellings of one state is three chances for a reader to think they mean
    different things.

  **All three are mutation-checked**: put `null` back, remove one link, drop the `PendingMark` call, and five
  tests fail naming exactly what went.

  **What the walk could not measure is recorded in it rather than claimed.** Naming a project is the terminal's
  and `parley terminal` refuses a redirected stream, so the project is written as the `project.json` the product
  writes — 019's walk recorded the same gap. Every review dialog is on a screen no shell can drive. A NUL never
  reaches the surface at all, because the host refuses the request before a page sees it, so that character is
  checked against the table instead. Reinstating a cap is a rebuild, so 8.6 is cited to
  `OwnershipStatement.Bounded` rather than walked. And **a real model's answer is the one thing that cannot be
  walked with a scripted agent**, which is what makes T063 — asking this repository's own agent — the measurement
  the feature actually turns on.

  **The walk's own first attempt was not isolated, and that is worth more than the checks it ran.** It set
  `PARLEY_CONFIG` and left the rest alone; this machine sets `PARLEY_NODE_CONFIG` to the live catalogue, so the
  enrolment obeyed it — correctly — and wrote two entries for temporary repositories into the file somebody's
  agents are started from. **018's incident, reached from a shell rather than from a surface**, and the product
  was right both times. `walk.ps1` clears every `PARLEY_` variable as its first act and says why.

  **And the script pinned the version it installed**, so the moment the fix moved it the install asked a local
  feed for a package that no longer existed and every check below reported failure while measuring nothing. It
  reads `Directory.Build.props` now — **a literal that was wrong within the hour**, which is why
  `ProjectRecordTests` derives that number rather than being told it.

- **The review gate found seven defects in its first six probes, two of them high** — fixed
  at **0.31.2**, **0.31.3** and **0.31.4**, and the
  gate has since been **met on all three lenses** on `019-one-way-in` — nine clean runs, with lens 1 reset
  once and lens 3 twice by findings along the way. `REVIEW-LEDGER.md` carries each with the probe that produced
  it. Two are 017's and two are 018's, and the 018 pair are both consequences of one change — sharing the
  resident table across every project a process hosts, which closed one hazard and opened two.

  - **A chip came out of the wrong place.** `Draft.Says` removed each recorded chip at its *first*
    occurrence, so somebody who typed an agent's name into their sentence and then completed that same agent
    had their own words taken out of the middle of the message while the addressing went on to be sent — the
    exact inversion of what the method exists to do, and the opposite of what its own remark claimed. Last
    rather than first is correct by construction: `Completion.Fills` only completes a trailing fragment.
  - **The room refill dropped the chips.** `Text` was carried to the rebuilt draft and the record of which
    runs in it were addressing was not, so `Says` had nothing to remove. **Three versions of that probe
    passed while exercising nothing** — no focus, then an entry point that returns immediately, then the same
    again. Only asserting that the draft had *actually been replaced* exposed it. A probe that cannot fail is
    a probe that has not run.
  - **Two projects starting one checkout produced two processes.** 018's headline claim, measured:
    **forty-seven processes from twenty-five concurrent pairs.** `TryGetValue` and the assignment after it
    are not one act, and the loser was overwritten in the table — so its process was invisible to
    `StopEveryoneAsync` and nothing would ever stop it. Looking and starting are one act now.
  - **One project stopping sent away every agent on the machine.** The sweep ran in each node loop's
    `finally` on a table shared by all of them, so `project remove` on an unrelated project — or changing
    another project's store — took down everything Parley was running. **018 had already fixed one instance
    of this shape**; this is the same class one level down, introduced by the same feature. Whoever makes the
    table stops it now.

  - **Stopping an agent mid-turn threw, and took the rest of the fleet's shutdown with it** (0.31.3).
    `ShutdownAsync` closes the agent's standard input as the polite form of stopping, and caught
    `IOException` and `ObjectDisposedException` around it — but a `StreamWriter` refuses `Close()` while an
    asynchronous write is in flight, and throws `InvalidOperationException`. **The method's own remark makes
    that collision ordinary rather than exotic**: it deliberately declines to synchronise a shutdown against
    a turn, because waiting on a turn's lock would let one wedged agent stop the whole fleet from stopping.
    So the design permits the race and the catch did not cover what the race throws, and the exception
    escaped into whoever was stopping the fleet — one agent mid-turn abandoning the stop of every agent
    after it. **The same shape as the fourth finding, reached by a different route.** Swallowing it is
    correct rather than convenient: closing standard input guarantees nothing, and the bounded wait and the
    kill below run regardless.

  - **Replacing a dead agent leaked the one it replaced** (0.31.3). `StartAsync` reaches its start when a
    resident is absent *or* has died, and the assignment that records the new one overwrote the dead one
    without disposing it — dropping a `Process` handle and the two pumps draining that process's output,
    once per agent that ever dies. **`TurnAsync`, forty lines away, takes exactly this care and says why**,
    which is what makes it a defect rather than an oversight: one of the two places that meets a corpse
    clears it and the other did not. **And the replacement now says so**: starting an agent because nothing
    was running and starting one because the process behind an attached session had gone are different news
    to anybody reading a node's log, and were one sentence.

  - **A start in flight when the node stopped was left running for ever** (0.31.4, lens 1 run 1 on
    `019-one-way-in`). Recording a resident happens inside the `Starting` gate, and `StopEveryoneAsync`
    took no part in it — so a start already past that gate landed its process in the table *after* the
    sweep had walked it, and the sweep is the last thing the process does. **An agent still running with
    nothing left that would stop it**, which is the third finding's harm reached from the other side, and
    it breaks what SPEC.md §12 promises: stopping a node tells every agent it started to quit. The sweep
    takes the gate now and shuts the table before walking it, so a start still queued finds it closed and
    says so rather than launching. **Mutation-checked: 40 of 40 orphaned without the guard, none with it.**

  **The fourth finding's debt is paid.** `SharedResidentsSweepTests` drives it — two brokers, a node loop
  over each, one shared table, one loop cancelled — and fails against the pre-fix behaviour with *"the
  stopping node took the shared table's entry with it"*.

  **Recorded honestly**: the fourth was found by reading rather than by driving, and the driving test came
  later. Writing it also found that two probes had been launching a process that died in twenty
  milliseconds: `InvocationCommand.Parse` appends Claude Code's protocol arguments to any template that
  does not name its own input format, which `ping` refuses. **A fixture that launches through the
  product's own launcher inherits everything that launcher does to a command line** — and both probes
  passed while measuring a process that was never running.

  **And the fifth was found by deleting a sleep.** `OneCheckoutOneProcessTests` waited 250 ms before
  disposing, to let the launched processes write their evidence — and that sleep was also, unintentionally,
  long enough for every dispatched turn to finish writing. Removing it surfaced a product race that had been
  masked since the probe was written. **The probe was also unsound and is rewritten**: it counted processes
  launched, which cannot distinguish the race it names from the entirely correct restart of a resident that
  has died, so it passed alone and failed only in the full suite where load widens the window. It reads the
  command's own announcements now.

- **Previous plan**: `specs/018-agent-identity/` — **0.31.1, built through T028 and green across every suite.
  The review gate is owed, the manifest still carries a field nothing reads, and nothing is pushed.** An agent
  is an identity, not a name: a stable identifier keys one catalogue per machine, and a name is what a project
  calls it. The owner's model, stated on 2026-09-22 after a message reached one agent and not another — one
  checkout enrolled in two projects, startable in the one whose catalogue named it and unstartable in the
  other, because the catalogue is keyed by a per-project name and there is one per project. **The defect is
  not the catalogue's contents; it is that the arrangement can express that state at all.**

  It takes 012's **D2** (one catalogue per machine, not keyed by a name) and **D3** (membership lives in the
  repository) and leaves D4 to D9 where they are. **No wire change to how a message is addressed, no schema
  change, and no migration** — the owner was explicit that existing data does not matter. What it deletes is
  the per-project catalogue, the manifest's binding for it, the machine-wide enrolment gate, the
  cross-project scan, 009's documented deviation from Principle V, and the rule that a second project gets no
  catalogue line.

  **The identity lives in the repository and the catalogue is keyed by it.** `.mcp.json` gains
  `parleyAgentId`, minted once by enrolment and never derived from a path or a name, so a checkout that moves
  or is renamed is the same agent afterwards and nothing needs repairing. `node.json` says how to start that
  identity and where, and says nothing about projects — so what a project calls a checkout is read back out
  of the checkout, and two workspaces claiming one name resolve to neither and are reported.

  **What building it found, each caught by a test rather than by reading:**

  - **`Parley.Headless.Tests` had been eighteen red since 016** and nobody knew. Its fixture still wrote 0.6
    Request payloads, which schema 11 refuses outright, so every test that opened an exchange threw before it
    reached the fleet. Invisible because `dotnet test` reports nothing on this machine and the branch has
    never reached CI — the entry above about that trap says the same thing from the other side.
  - **The machine half of the catalogue key vanished with the rekey.** A key used to be a role and a lookup
    carried a machine, so `billing` here meant `billing@thisMachine`. An identity says nothing about machines
    and a store holds agents on several, so a local checkout claiming `billing` would have been started for
    `billing@server-b`. The check moved to `AgentFleet`, which is the one place left that compares a name to
    a machine.
  - **The launch-key rule grew a second implementation.** This project needed its own reader of `.mcp.json`,
    and it spelled `parley` and `parley-` itself — for one build the rule had two implementations that
    happened to agree. `KeyRuleTests` reported it by path, which is exactly what it was written to do, and
    `LaunchKey` moved to `Parley.Configuration` where both readers can see it.
  - **The copied-repository refusal was enforced on one of the two writers.** `AddAsync` had it and
    `enroll apply` did not, which is this repository's recorded shape for that mistake. One implementation
    now, asked of every row before a bulk run writes any of it.
  - **The catalogue's fallback resolved against the working directory.** Survivable while every manifest
    entry named an absolute path; the ordinary case once that entry went, and a broker started by a logon
    entry starts wherever the scheduler put it. It is beside the manifest now, and the node and the enrolment
    reach it the same way — writing consent into a file the supervisor does not read is the defect this whole
    feature exists to remove.
  - **Creating a catalogue restarted a broker.** The terminal recorded the new path on the project through
    the command that owns the manifest, and the serve watcher compares the manifest to decide whether a
    deployment is the same one — so adding the first agent to a project dropped every agent attached to it.
    Nothing is recorded on a project any more.

  **And one defect reached a real machine, which is why the seam exists.** `Catalogue.Proposed` read
  `PARLEY_NODE_CONFIG` for itself, deep inside the write. The terminal's tests build the surface in process,
  so they inherited the developer's own setting and enrolled their temporary repositories into the live
  deployment's catalogue — thirty-five junk entries in a file somebody's agents are started from.
  `ToolProcess` clears those variables before launching the tool as a child, which is the same guard; it
  simply could not cover a surface that never launches anything. The path is passed in now, beside
  `Shell.Opener` and `Shell.Copier`, and resolved once by whoever builds the shell. **Reading ambient state
  from the bottom of a call stack is what made it unreachable from the top.**

  **0.31.1: the installer's own output still named a catalogue per project, and the broker two lines below
  it named a different file.** Found by the owner on the first real install of 0.31.0 — `autostart install`
  printed `catalogue: ...\billing-catalogue.json` under one of two projects, and the broker's
  log said it was hosting from `node.json`. Both were read from the same manifest; only one of them was
  still true. `ServedProjects` went on reading `node_config`, which nothing else does any more (T027 has not
  removed the field), so it reported a path that is genuinely in the file and genuinely decides nothing.
  **The output is the last place a per-project catalogue survived, and it is the place somebody checks their
  deployment against what they meant.** It is said once for the machine now, on `install` and on `status`,
  and `parley --help`'s three paragraphs describing the per-project rule went with it.

  **The path reported is resolved with no environment, and that is the load-bearing half.** A supervisor
  starts the broker at logon where nothing has set `PARLEY_NODE_CONFIG` — the refusal a few lines above says
  exactly that about what an entry serves — so reporting what *this shell* resolves would name a file the
  logon broker will never open, looking right to the one person who checks. Proved by mutation rather than
  argued: swapping `_ => null` back to `Environment.GetEnvironmentVariable` fails two tests, and what comes
  back is the developer's own live `node.json`. **018's live-file incident from the other side** — ambient
  state read at the bottom of a call stack wrote into a real deployment; here it would be read at the top and
  describe one that does not exist.

  **And running the suite to check this found that 018's own "CLI 857/857" was untrue.**
  `ProjectManifestTests.Two_projects_cannot_share_one_node_catalogue` asserts the catalogue-collision rule
  018 deleted, and has been red in every run since — under a commit whose message records the suite as
  green. The number came from a run against a build made before the last edits, which is the trap this file
  already records twice: *a run recorded as green was measuring a build without the new file in it*, and
  the `dotnet test` entry says the same thing from the other side. **A count is evidence only about the
  binary that produced it.** The test now asserts the opposite, which is the state 018 made ordinary — one
  catalogue per machine means sharing is the only arrangement there is.

  Beside it, `Collisions` still carried the whole paragraph explaining the removed rule, sitting exactly
  where the check used to be. Rewritten rather than deleted: a comment left standing over a rule that has
  gone is how the next reader concludes the check was lost by accident.

  **And `enroll scan` could not be run at all on a machine naming several projects.** The refusal to choose
  between them is applied to the *verb*, before the subcommand is read, so all three of `scan`, `plan` and
  `apply` were held to the requirement of two. `plan` and `apply` do need a project — the key they write
  into each repository is `parley-<project>`. **`scan` acts on no deployment whatever**: it walks one
  directory, decides which subdirectories are git repositories, and writes a file for a person to edit,
  with no project named anywhere in what it produces. So the one step whose whole job is to survey a
  machine was the one step that could not be run on the machine worth surveying — found by the owner
  trying to re-enrol nineteen repositories after 018 made every catalogue entry unreadable. Exempt from
  having to *choose* a project and not from being told a wrong one, which is the line the explicit-address
  exemption beside it already holds. Driven through the real binary, because an in-process call never
  reaches the entry point — the same reason `FilesCommand.OwnOptions` is tested that way.

  `research.md` carries eight decisions, `contracts/catalogue.md` the two file shapes and every refusal, and
  `quickstart.md` the walk that measures the two claims worth doubting: one checkout starts from either
  project, and two projects asking at once produce one process. **Still owed:** `ProjectSettings.NodeConfig`
  and its readers (T027) — `project add` and `project set` still offer `--node-config`, which now writes a
  field nothing reads — the remaining surfaces and installers (T029–T031), US4's telling a person when this
  machine cannot start an agent (T032–T037), and the review gate.

- **Previous plan**: `specs/017-conversation-room/` — **0.30.7, built whole and green across every suite; the
  review gate is owed and nothing is pushed.** The terminal's
  conversation view becomes a room: participants, outstanding work and agent status down a narrow column; the
  transcript above one input line in a wide one. The owner's call on 2026-09-21, after using it — it should read
  like Claude Code with a layout.

  **The input line is the whole of it.** One box, and the protocol has four acts, so the act is derived per
  recipient from who owes whom: a Result where they asked and this person holds it, a Clarify where this person
  asked and is waiting, a Request where nothing is open. One line to three agents may be three acts, each
  correct. The composer shows what Enter will do and what it costs before it is pressed, which is the first time
  this surface has made §5.5's plural ask visible before it is paid for. One case is never guessed: holding a
  Request, typing may be the answer, which closes the exchange, or a question first, which hands the turn back.

  **It needs no protocol, schema or wire change**, which was the load-bearing discovery: `ExchangeSnapshot`
  carries no parties, and reconstructing them from the messages would put the broker's turn logic in a client.
  Joining `Export`'s exchange identifiers with `DescribeWaiting`'s `waiter`/`waited_on` edges gives the pair state
  from two operator reads the surface already makes (`research.md` R1).

  **Built around the existing transcript rather than in place of it, and that decision paid for itself**
  (`research.md` R11). Forty-nine test files in `tests/Parley.Terminal.Tests` drive this view, and reading them
  showed almost all are about the *transcript* — the column-zero rule that stops a payload forging a header,
  agent-written text escaped on the way to the screen, wrapping, reflow, copying, the refill race, colour
  surviving being stripped. Every one of those was found the hard way and several by probe. Keeping the transcript
  meant they kept passing by construction: **six tests failed out of 733**, and the sort is recorded in
  `tasks.md` — four were descriptions of the arrangement (pane counts of 1 and 4, now 2 and 5) and two were
  guards reached through `Focus(0)`, which silently became a different region.

  **What building it found, each caught by a test rather than by reading:**

  - **Arriving at a conversation did not read it.** The place and the read were two acts and the conversations
    list happened to do both, so any other route in left the room and the line describing whatever was last read.
    Going there reads it now, and the list no longer reads it first — two reads on one keystroke is one wasted
    export and two chances to disagree.
  - **Enter had to be taken from the field**, which receives it before any handler on the region. A send wired
    only to a region action would never have run, which is how the conversations list once did nothing at all.
    `LineTests` presses it over a real socket for exactly that reason.
  - **A cap refusal cleared the line.** The first version decided "did anything send" by matching the report's
    opening words, which is not that question. `SeatVoice.SendAsync` returns a count now, and 0.25.1 records
    what getting it wrong costs: the cap was enforced after the form closed and took the message with it.
  - **No seat and nothing read yet were one state** in a single nullable draft, so a seated person whose
    conversation had not been read was told to take a seat they already had — the same class of mistake as
    reporting "none waiting" for "could not ask".
  - **`DeadSurfaceTests` earned its keep again**, reporting three of the room's own constants as named only in
    their own file. Writing the tests is what answered it rather than making them private.

  **Phase 4 put the box on screen, and that is where the defects were.** Three of them, none visible from
  inside the suite, and the first is the reason the other two had never been met:

  - **The line pane had no size at all.** The arrangement handles one pane, stacked panes, and two side by
    side; the conversation view has three, and the third was never placed. So the region existed, was visible,
    held text, and drew nothing. `LineTests` set its text in memory, read it back and passed. **A property the
    suite holds to and the screen never shows** -- the third time this repository has recorded that shape,
    after `Shell.Said` being written in twenty places and read by nothing, and the terminal having no colour
    under a passing `ColourTests`. The heights now come from one method, because the message line assigns them
    too and its loop would have flattened the arrangement the first time a message grew to two rows.
  - **Every printable key belonged to an action, including inside the box.** The shell takes a region's letter
    in its own key handler, before the focused view sees it -- so typing a question mark into an ask opened the
    actions menu and `x` cleared the message line. A box with focus wins every character now; Tab, Escape, the
    quit chord, F8 and F2 still work, because leaving a box must not need the mouse. `Lists` records the same
    collision from the other side, a list's type-to-find eating every letter the status line advertised: one of
    the two has to yield, and while a text box has focus the answer is obvious.
  - **The answer-or-ask-back alternative was advertised on a key this surface consumes.** The preview said
    "Tab: ask back instead" while Tab means "the next region" everywhere here, taken before any region sees
    one. It is **F2** now, with a second route under `a` in the room column -- whose rows are what the choice
    is about, and which is a list, so a letter is free there. Tab keeps one exception, which the owner asked
    for by name: it completes an open list, and the list is on screen while that applies.

  **An agent completion is a chip and not text, which is what keeps routing out of the payload.** Completing
  `@ord` resolves the agent, records it as a recipient, and takes the sigil out of the line; `/add @bill` takes
  the command with it. What is left in the box is always prose, so nothing is parsed at send time, a semicolon
  in an ask is just a semicolon, and §13's envelope never carries a directive it exists to have ignored. What
  shows a person their chips is the preview's own `to:` line. A command still in the box at Enter is therefore
  one nothing consumed -- somebody typed it by hand -- and it is run or refused with a reason, never sent.

  **The narrowing carries across a refresh**, because the transcript follows every three seconds and a draft
  narrowed to one agent would otherwise widen itself back to the room while somebody was still typing to them.
  The same reason the answer-or-ask-back choices are carried.

  **And the place had to become authoritative for the read, which broke two tests that had been passing on the
  duplicate.** Arriving at a conversation reads it now, so the place must name the conversation -- and it did
  not from two of the three views: the conversations list passed the place through unchanged unless it was a
  project view, so from the projects view nothing named the conversation, and from inside one the place went on
  naming the *previous* conversation while the transcript showed the new one. Tolerable while the place was not
  what read. **A first fix queued the read**, which put it behind the item that asked for it -- every caller is
  already on the work queue -- so a test awaiting `Settled` got a task that did not include it, and a transcript
  came back empty in a test about escape sequences. It is awaited inline.

  Two smaller things, each found by a test rather than by reading: a closed list was passed a selection of row
  zero, which the toolkit refuses outright -- nothing selected is null, not nought -- and it threw on the first
  keystroke of every test in the file; and a frame is only true after a layout pass, so reading the screen is
  not enough to measure one. The room's fixed width resolved without a parent and everything derived from the
  space left over was nought, which is 0.25.1's probe-read-the-screen trap in a new place.

  **`RegionAction` learned a chord, because `EveryKeyIsOfferedTests` refused F2 and the arrow keys.** That guard
  reads the keys out of `OnKeyDown` rather than carrying its own list, and it reported all three as answered by
  the surface and named nowhere a person would look. The arrows joined its navigation list -- a decision the
  list exists to make visible -- and F2 became an ordinary action of the line pane, which put it on the status
  line and in the menu by construction. `DeadSurfaceTests` then reported `SendLine` and `FlipOne` as named only
  in their own file, which was true: both are reached through the action table and the line's own events, so
  both are private now, and the two labels a person reads are asserted instead.

  **A run recorded as "the terminal suite is green, 733 tests" was measuring a build without `LineTests` in
  it** -- a background run started before the file existed. The real first run with phases 3 and 4 in it failed
  six. The suite is 752, and the number moving is what gave it away; the entry above about `dotnet test`
  reporting nothing says the same thing from the other side.

  **Phase 5 (0.30.1) makes the column point at the transcript beside it.** A `TranscriptLine` carries the
  message it belongs to, so a surface can put one message in front of a reader rather than restating it in a
  list -- which is the whole reason the "waiting on you" column names things instead of repeating them. Null on
  a line the surface wrote itself, because answering "which message is this" with a guess would send a reader
  somewhere nothing was said.

  **Two selections rather than a viewport, and that is how the neighbours are guaranteed.** Terminal.Gui v2 has
  no `TopItem`, and a list scrolls its selection into view -- so a single jump lands the row against whichever
  edge it came from, with the message at the very top of the screen looking like the start of the conversation.
  Selecting two rows earlier first makes *those* the rows scrolled to, and the message sits below them. No
  viewport is touched, which is also the part a headless test can hold.

  Three smaller decisions went with it: the row points at the **latest** thing the peer said in that exchange
  rather than the first, since an exchange can hold a Request and then a Clarify and what is owed an answer is
  the last question asked; a row that points at nothing says so rather than moving the reader, because a
  transcript refilled since the column was drawn is an ordinary race; and the owners are read as one snapshot
  of the reference rather than under `GuardedList`'s guard, which is private on purpose -- the list is replaced
  wholesale, so a reader sees the rows as they were or as they now are and never a half-swapped pair.

  **0.30.2 is the owner's first morning using the room, and six things they asked for.** Four are about a
  transcript saying more than a person needs and two are about a column that could not be acted on.

  - **A header is a time, a sender and a recipient, and nothing else.** It carried the message type, the state
    of its exchange and whether it had been collected: *"Humans just talk; we do not begin by saying 'Inquiry'
    and 'Response'."* The type is derivable from the conversation; the state is what the room column already
    says per peer in the form somebody can act on. **And *collected* was actively misleading**, which is what
    made this a defect rather than a preference: it means the recipient drained its inbox, which for a person
    is a key they press — so an answer being read on screen was labelled "not collected yet", and read as a
    fault in the deployment rather than as a fact about a keystroke nobody had pressed.
  - **The list of everybody who did not see a message left the screen too.** 0.25.3 added it, and its argument
    was right then: it was the only statement that somebody in the conversation was not party to a message.
    The room column now lists every participant beside the transcript, so on screen it restated what was
    already there twice over. **The export keeps it**, which is where it earns its keep: a file read somewhere
    else has neither a header naming one recipient nor a column beside it. `AgreementTests` holds exactly that
    now — the export carries every annotation and the screen carries none — so a surface that quietly grew it
    back, or an export that quietly lost it, still fails.
  - **The transcript folds to one line per message** (`f`), and Enter opens one. A broadcast to nineteen agents
    is nineteen pairwise messages carrying one sentence, so the shape of an exchange is buried under its own
    content on exactly the conversations where that shape matters most. **What is folded is a view and not a
    shortening**: `Written` stays the whole conversation, so copying takes all of it, and `Drawn` is what is on
    screen. Folding puts the cursor on the first row that is a *message*, because a transcript opens with its
    own preamble and landing on one of those rows would be a cursor where Enter does nothing.
  - **The participants are a checklist that filters what is about to be sent.** Space or Enter ticks one; the
    preview above the box agrees because it is the same draft — `@` and this column are two ways at one set,
    and two ideas of who a message is going to is one of them being sent. The last recipient cannot be unticked
    and the refusal says why.
  - **Taking a seat moved to the room, and that is my own defect from the night before.** It was offered on the
    line pane alone — and a focused box wins every printable key, so `j` typed a `j`. The one place Join was
    offered was the one place its key could not arrive. This pane is a list, so a letter means what it says.
  - **The person is on their own line above the agents**, because a person is not one of them: every other row
    is somebody they might send to, and the one row that was them was in the middle of that list.

  Two things the suite found on the way: a clamp added for folding turned "no selection" into row zero, which
  is the state `FollowingTests` exists to protect — a reader who has not moved is on no row, and saying they
  are on the first one is the surface claiming they are reading something. And the driven fixture was copied
  in two files and about to be in four, so it is one `Seated` now; a copy per file is how two of them come to
  seat somebody differently and disagree about what the room should show.

  **0.30.3: a completion fills the name in, because one that makes text vanish reads as a broken key.**
  Reported as *"hitting tab does not auto-fill the name of the agent"*, and the probe found Tab working
  perfectly: it resolved `@ord` to the agent, recorded it as a recipient, and **deleted what had been typed**.
  Correct, invisible, and indistinguishable from a key that does nothing — what a person is looking at when
  they press Tab is the thing they typed.

  **The chip idea survives; only where it is drawn changed.** `Draft.Chip` records the exact spelling the
  surface itself wrote and `Draft.Says` takes it back out at the send, so addressing still never reaches a
  payload §13 tells the receiver to treat as inert data — and, because the removal is by that record rather
  than by hunting prose for anything that looks like a name, an agent's name written by hand in the middle of
  a sentence stays in the sentence. `/add` leaves its chip and takes only the command with it, which it has to:
  a command left in the box is one nothing consumed at the next Enter.

  **And the test that sent it found the defect that mattered.** Setting the field's text leaves the cursor
  where it was, so completing `@ord` into `@orders@test` left it at the fourth character and the next thing
  typed landed inside the name: the message went out as `@ordthe build is greeners@test`, with the chip no
  longer spelled the way it was recorded and so no longer taken back out. **Both halves are why the assertion
  is on what the broker recorded rather than on what the box says** — a test that stopped at the screen would
  have passed.

  **0.30.4: the box is a paragraph, and Enter reaches it. The second half is the worst defect this feature
  has had.** The owner could not send a message at all, and the reason is a sentence worth keeping:

  > **A focused view is offered a key before the surface's own handler is.**

  Every comment in this feature said the opposite — that the shell takes a region's key first — and the
  evidence looked like it agreed, because Tab does arrive at the shell. It arrives because the box *declines*
  it. What the shell receives is only what the focused view did not want, so an arm there for Enter is an arm
  that never runs, and the moment the box became a paragraph that binds Enter for a line break, sending
  stopped. `LineRegion` takes Enter on the box itself now, through `KeyDown` rather than `Accepting`, because
  a binding runs after the key event and this has to come first.

  **Nothing in the suite had ever pressed the key.** Every test called the `Accept()` seam, which raises the
  same event from the other side — so the one thing that seam exists to check, that the key arrives at all,
  was the one thing unchecked, in a file whose own summary said it was there to catch exactly that. `Lists`
  has had the sentence since 0.20.3: **a key tested only through its own seam is a key nobody has pressed.**
  The tests press it now, and assert on what the broker recorded.

  **The box grows downward and wraps.** A single-line field scrolls horizontally, so a long message slid out
  of sight a character at a time and the only way to read your own words back was to walk the cursor through
  them — in the box an eight-thousand-character request is written in. It is `Entry.Speaking()`, which keeps
  the `TextView` deprecation suppressed in the one file that already carries it rather than spreading a
  second `#pragma` into a region. The region stacks from the top now, so the rows it is given go to the
  message, and the clamp is **a floor under the transcript rather than a ceiling on the box**: it grows until
  the conversation is down to `TranscriptFloor` rows and scrolls after that.

  **Enter sends; a line break is Ctrl+Enter, or a backslash then Enter.** The chord is what a person reaches
  for and is named on the status line with its fallback in the menu — and the fallback is not decoration:
  several terminals deliver Ctrl+Enter, Shift+Enter and a bare Enter as the same bytes, so the spelling that
  cannot be taken away is two ordinary keys.

  **0.30.5: the box had no rows, and four suites of tests said it was fine.** A closed completion list went
  on reserving the row between the preview and the box, and the box takes what is left -- so the region asked
  for exactly the rows it needed and the list ate the last one. No cursor, nothing drawn, and **typing still
  worked**: the owner pressed `@` and got the list of agents while the characters themselves were invisible.
  A closed list is nought rows now, not one.

  **This is the fourth time this repository has recorded the same shape**, and the first three are all in this
  file: `Shell.Said` written in twenty places and read by nothing, a terminal with no colour under a passing
  `ColourTests`, and the line pane with no size at all last week. **A property the suite holds to and the
  screen never shows.** Every test of this box read what was written; none looked at the screen.
  `TypingIsVisibleTests` is the screen, and it was checked against the defect before the fix went in -- it
  fails there, which is the only thing that makes it a test rather than a second statement of the code.

  **0.30.6: what somebody is typing into cannot be the thing that gives way.** The box stopped growing at two
  lines and then lost its cursor again, and the cause is the ordering: the preview names every recipient, so a
  conversation of sixteen agents is a seventeen-row preview — and the box was given whatever the preview and
  the list had not taken. The box is anchored to the bottom with the rows it needs now, the list sits above
  it, and **the preview takes what is left**, bounded so that naming everybody cannot squeeze the message out
  of its own box.

  It grows to ten rows and then scrolls, both the owner's numbers, and **both the box and the transcript show
  a scrollbar once there is more than fits** — asked for by name, and the reason is the rule this surface
  already keeps about truncation: something hidden with nothing saying so is indistinguishable from nothing
  being there. The cursor also stopped flickering, because the placement is only reassigned when a number
  actually changed rather than on every keystroke.

  **This is the third defect in one pane found by use rather than by the suite**, and the sentence that keeps
  earning its place is that the box's rows are the one thing about this region the text in it cannot tell you.
  `BoxGrowthTests` and `TypingIsVisibleTests` read the laid-out screen.

  **0.30.7 is the worst of them: a message the owner typed to two agents reached neither, and 017's own spec
  is where the defect came from.** Both refusals were the broker's and both were right — *"the other side holds
  the next move on that exchange"* and *"you sent that request, so its receiver sends the Result"*. What was
  wrong is that this surface chose those acts.

  **Whose move it is does not say which act to send.** The wait-for graph answers *whose*, and 017's spec read
  that as the whole answer: *"a Clarify where this person asked and is waiting, because nobody may Result their
  own request"*. The second half is true (§5.3) and the remedy was not — SPEC.md §5.2 gives the Clarify to the
  *receiver* asking the requester for more, so a requester clarifying their own open request is refused. And a
  move owed on an exchange this person opened is the answer to a clarification, which is a Clarify and not a
  Result. Two states the derivation had collapsed into one. **`PairState` carries the role now as well as the
  turn**: who opened an exchange is the sender of its first message, which is reading the transcript rather
  than reconstructing the broker's turn logic in a client — the thing `research.md` R1 refused to do, and still
  refused here.

  **And a refused act no longer loses the words.** Where the broker will not take the derived act, the text is
  sent as a Request instead and the report says so. A new exchange is an obligation, so it is named rather than
  opened quietly — but the promise a person actually has is that what they typed is delivered, and a
  derivation this surface got wrong twice is not a thing to stake that on.

  **Which artifact was wrong was decided rather than assumed**, as this file requires: SPEC.md §5.2 is the
  authority, the broker implements it, and `specs/017-conversation-room/contracts/composer.md` carries the
  correction with what it cost. Three `ComposingTests` asserted the old rule and were replaced; each new one
  was run against the old code first and fails there.

  Commands are a surface rather than `/add` with a special case: `/` plus an identifier with no second `/`, each
  declaring what it takes and whether it changes the draft or acts; an unrecognised identifier is text, so
  `/etc/hosts` needs no escape. Destructive commands are allowed and expected to grow, each declaring the door it
  must pass — which means `SurfaceTests` has to read declarations or it will keep passing while meaning less.

- **Previous plan**: `specs/016-request-is-the-ask/` — **0.29.0, protocol 0.7, schema 11. Built, gated on all three
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
  remained at the time, for testing only — **019 removed all three**, which is the entry below.
  **These are code edits after the review gate**, so its lenses owe confirming runs.

  **`parley top` became `parley terminal`, on the owner's call**: "top" is the name of a process monitor, not of a
  person's view of a deployment. The tool is `parley-terminal` in the package `Parley.Terminal`, and the owner then
  had the internals follow, on consistency being what keeps a codebase maintainable: `src/Parley.Terminal`, the
  `Parley.Terminal` namespace, `TerminalCommand`, `tests/Parley.Terminal.Tests` and `TerminalProcess`, moved with
  `git mv` so their history follows. No alias is kept, since
  nothing was released under the old name, and both installers remove a `Parley.Top` package installed before —
  otherwise nothing ever would. Entries below this one say `parley top`, `Parley.Top` and `TopCommand` because those were the names then.

  **`serve --db`, `autostart install --db` and `PARLEY_DB` were for testing only, and 019 removed them
  outright** — this paragraph is kept because the arrangement it describes is the one that was reversed, and
  because it records the reasoning that turned out to be wrong. It ran one store outside the manifest so a
  test rig need name no project, which is what the suites used it for.

  **What that cost was two defects and two precedence rules.** `serve --db` on a machine whose manifest named
  projects served the projects and ignored the store it was told; a `PARLEY_DB` left in an environment from a
  test turned the logon entry into a broker for the test store. The review gate found them one after the
  other, and the fixes were both rules about which of two sources wins — a question that only exists while
  there are two. The owner's call on 2026-09-23: *"We can't have some commands using DB and others not using
  it. Everything is project centric."*

  **The test rig pays for it by naming a root instead**, which is what made the removal affordable: a project
  in a throwaway root costs one name and no paths, and `BrokerProcess` writes one with port zero so the
  operating system still picks the port and the suite still runs in parallel.

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
  Its obligation was discharged: SPEC.md §4.1 illustrated `projects.json` rather than `parley.yaml`.
  **019 replaced that file with a directory** and §4.1 illustrates the root now; the obligation stands
  discharged either way, since what it required was that the document show what the code writes.
  `tasks.md` carries the review record, including what five rounds turned up.
- Previous: `specs/001-agent-routing-hints/`.
<!-- SPECKIT END -->
