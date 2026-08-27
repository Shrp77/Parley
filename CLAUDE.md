# Parley

Cross-session agent-to-agent messaging with a durable store and a registry. **`SPEC.md` is
the primary artifact of this repository; the code is a reference implementation of it.** If
code and spec disagree, that is a defect in one of them, and which one is a decision, not an
assumption — do not silently change code to match spec or spec to match code.

Read `SPEC.md` before changing protocol behaviour. Open design questions live in `SPEC.md`
§17.

## Layout

```
src/Parley.Core     Domain. No I/O, no dependencies. AgentName, PairKey, Exchange,
                    WaitForGraph, BrokerEndpoint, MachineName, Responder,
                    ProtocolVersion, WorkspacePath, and the enums. WorkspacePath is
                    string comparison rather than path handling on purpose: the broker
                    compares workspaces from machines it is not on, so touching a
                    filesystem here would both break the no-I/O rule and answer the
                    wrong question.
src/Parley.Store    Persistence. SQLite via Dapper. ParleyDatabase (connection
                    factory), SchemaMigrator, WriteScope, and the repositories:
                    Exchange, Message, Agent, Conversation.
src/Parley.Broker   Orchestration. Decides when the other two run. Broker holds the
                    agent operations of SPEC.md §10.1; OperatorConsole holds §10.2.
                    Knows nothing of transports or adapters, and must not learn.
src/Parley.Transport The wire contract, generated from proto/parley.proto. No
                    hand-written code at all, which is why its warnings are silenced.
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

**Run to convergence, not to a fixed count.** "Three green" is not three passes; it is passes
repeated until one finds nothing above low severity. A pass that finds something means the
branch was not green, so fix it and go round again — including the lenses already run, because
a fix is a change and changes are what the lenses are for. Stop when a full rotation turns up
nothing worth acting on, and say how many rounds it took.

The failure this corrects is specific and was repeated: running each lens once, fixing what it
found, verifying the build still passes, and reporting green. A build that passes is not a
review. The verification sweep at the end confirms the fixes did not break anything; it does
not look for what the fixes introduced.

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
  style preference. It lives in the schema so no code path can route around it. The number is
  currently 4,000 and is deliberately provisional: `parley refusals` records what it turned
  away so the next change to it can cite evidence rather than taste. Move it on that evidence,
  not because a message did not fit.
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
<!-- SPECKIT END -->
