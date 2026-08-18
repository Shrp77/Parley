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
                    WaitForGraph, and the enums.
src/Parley.Store    Persistence. SQLite via Dapper.AOT. ParleyDatabase (connection
                    factory), SchemaMigrator, ExchangeRepository, AgentRepository.
tests/Parley.Core.Tests   Domain tests. Deliberately does NOT reference Parley.Store —
                          the absent dependency is the point. Keep it that way.
tests/Parley.Store.Tests  Repository and schema-constraint tests.
```

`Parley.Core` having no I/O and no package dependencies is a design constraint, not an
accident of the current code. A `using` that pulls I/O into Core is a review finding.

## Build and test

```
dotnet build
dotnet test
```

Tests run on Microsoft.Testing.Platform, selected in `global.json` under a `"test"` section
beside `"sdk"`. A green build is the whole lint gate: warnings are errors, documentation is
enforced, and NuGetAudit fails on high or critical advisories, so there is no separate lint
step. CI runs the matrix on Windows, Linux, and macOS because the transport claims parity
across named pipes and Unix domain sockets, and that claim is only worth making if it is
tested on all three.

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

## Review gate

Before a branch is considered done: **three review passes, each with a different lens**, each
finding nothing above low severity.

1. Correctness and concurrency
2. Security and data integrity
3. API surface and maintainability

Rotating the lens matters — three runs of the same review are correlated and mostly
re-derive the first. After fixing findings, **re-run the same lens** before moving to the
next; fixes have introduced new problems that only the re-run caught.

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
- **Terseness is a security control.** The 2,000-character message cap is a
  context-poisoning defence, not a style preference. It lives in the schema so no code path
  can route around it.
- **No agent has authority over another.** There is no orchestrator and no lead. Any design
  that introduces one contradicts the premise.
- **Credentials are references, never values.** `env:`, `keychain:`, `file:`. The broker
  resolves them at invocation and never persists or logs them. This is what makes the
  manifest safe to commit.
