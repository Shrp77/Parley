# Implementation Plan: A repository may answer in more than one project

**Branch**: `009-multi-project-enrolment` | **Date**: 2026-09-09 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/009-multi-project-enrolment/spec.md`

## Summary

A repository's `.mcp.json` may hold one parley entry per project it answers in, keyed
`parley-<project>`. The protocol permits this already (SPEC.md §4.1); one fixed key and one refusal
written against a different question are what forbade it. The work is entirely inside
`src/Parley.Setup`, plus a project field the CLI never had, plus three illustrations and a rule about
unattended starts.

Nothing crosses the wire, no schema changes, and `SPEC.md` does not change — checked, not assumed
(`research.md` R8).

## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: `System.Text.Json` (already the only thing `Parley.Setup` parses with)
**Storage**: none. This feature never opens the store, and `Parley.Setup` has no route to one — a graph
walk asserts it
**Testing**: xUnit v3 on Microsoft.Testing.Platform. `Parley.Cli.Tests` holds the enrolment suites;
`Parley.Top.Tests` holds the terminal's
**Target Platform**: Windows, Linux, macOS
**Project Type**: CLI tools plus a terminal application
**Performance Goals**: not applicable — the work is per repository, at the speed a person enrols one
**Constraints**: no I/O in `EnrollmentPlan.Describe`, which is pure by design and heavily tested that
way; no new package
**Scale/Scope**: the deployment this was written against is 18 repositories on one machine, of which one
would be multiply enrolled

## Constitution Check

*GATE: passed before Phase 0, re-checked after Phase 1.*

| Principle | Verdict |
|---|---|
| I. The specification is the product | **Pass, and it is the interesting one.** SPEC.md is untouched. §4.1 already permits what this admits, so the code is moving *towards* the document rather than past it. |
| II. No agent has authority over another | **Pass.** Nothing here is an agent operation. Two entries give one repository two identities, neither of which can address the other. |
| III. Identity is fixed by the launch, never by a call | **Pass, and worth stating.** Each entry is its own launch environment. A repository with two entries has two servers; which one is speaking is decided by which server the client started, never by a call. No operation gains a sender. |
| IV. Terseness is a security control | **Pass.** No new text crosses between agents. The `owns` line is per entry and unchanged. |
| V. Invariants live in storage | **Deviation, recorded below.** FR-013 cannot be a storage constraint. |

### Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
|---|---|---|
| FR-013 is enforced in application code, not in the schema | The rule is "one workspace may not be catalogued for unattended start in two projects on one machine". A node catalogue is a file per project, and no store holds it — `ProjectManifest` already says so: *"the one thing SPEC.md §4.1's isolation cannot supply from storage because the catalogue is not in the store."* Two projects are two databases by design, so there is no schema either could carry the constraint in. | Putting the catalogue in the store would give the constraint a home and would break §4.1's isolation to do it: a broker would have to read another project's file. That is a larger violation than the one it fixes. |

## Project Structure

### Documentation (this feature)

```text
specs/009-multi-project-enrolment/
├── plan.md              # This file
├── spec.md
├── research.md          # Eight decisions, each with what was read
├── data-model.md        # The entry, the key, and the precedence
├── quickstart.md        # Enrolling one repository in two projects, end to end
├── contracts/
│   └── launch-configuration.md   # The file's shape, and what each reader owes
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks output, not this command's
```

### Source Code

```text
src/Parley.Setup/
├── LaunchKey.cs          # NEW. The whole key vocabulary: build, recognise, read back
├── LaunchEntries.cs      # NEW. Reading a servers object into entries, and the precedence of R3
├── EnrollmentPlan.cs     # Describe gains a project-endpoint map; the key comes from LaunchKey
├── EnrollmentEntry.cs    # EnrollmentManifest gains `project`
├── Enrolment.cs          # ServerKey moves out; WithdrawAsync takes the project
├── EnrollCommand.cs      # --project; the two "what is this already" readers answer with a list
└── Catalogues.cs         # NEW. FR-013: the same workspace elsewhere in the manifest

src/Parley.Top/
├── Adding.cs             # Passes the endpoints map through; nothing else changes
└── Withdrawing.cs        # Names the project it is withdrawing from

src/Parley.Cli/Program.cs # `enroll apply --project`, and the help text
src/Parley.Web/Pages/Projects/Register.cshtml   # FR-014
README.md                                        # FR-014
plugins/parley/commands/setup.md                 # FR-014

tests/Parley.Cli.Tests/
├── LaunchKeyTests.cs         # NEW
├── LaunchEntriesTests.cs     # NEW — the precedence of R3, case by case
├── MultiProjectEnrolmentTests.cs  # NEW — User Stories 1 to 4 as written
├── CatalogueUniquenessTests.cs    # NEW — FR-013
├── EnrollmentPlanTests.cs    # extended; every existing case must still pass unchanged
└── EnrolmentTests.cs         # extended

tests/Parley.Top.Tests/       # the terminal's add and withdraw, against two projects
```

**Structure Decision**: everything lives in `src/Parley.Setup`, which is where both writers already
are. No project is added and no reference changes. `research.md` R1 carries the check that no fourth
reader exists — in particular that the web console never opens a launch configuration, which is why
this is two writers rather than six.

## Phasing

The order is chosen so that each phase leaves the tree green and shippable, and so the two phases that
can lose somebody's configuration are the ones with the most behind them.

1. **The vocabulary, with no behaviour change.** `LaunchKey` and `LaunchEntries` land; every existing
   caller is rewritten in terms of them and still writes `parley`. The whole suite must pass with no
   test edited. This is the phase that proves the extraction was faithful.
2. **Reading many.** The scan readers, the plan's "already correct" test, and the terminal's views
   answer with every enrolment rather than the first. Still nothing new is written.
3. **Writing many.** `Describe` takes the project and the endpoint map; the precedence of R3 decides
   what is updated and what is added; FR-008's three outcomes; FR-009's refusal.
4. **Withdrawing one.** The project reaches `WithdrawAsync`, and FR-011's two cases.
5. **The unattended rule.** `Catalogues` and FR-013.
6. **The CLI's project.** `--project`, the manifest field, and the resolution note that says which
   input won.
7. **The illustrations.** README, the plugin's setup instructions, and the Register page.
8. **Version and gate.** `VersionPrefix`, then three consecutive clean runs on each of the three
   lenses, all after the last source edit.

**Version**: `0.22.0`. The minor moves because this is the release that also removes `parley console`,
`--with-console`, `--console` and `--no-console` — 008's record commits 0.22.0 to that and calls it "a
commitment, not a second reprieve". Doing both here is deliberate: they are one upgrade for the person
who has to run it. `InstalledEntry` keeps *reading* `--console`.

## Where each settled rule lives

| Rule | Lives in | Read by |
|---|---|---|
| The key is `parley-<project>`; `parley` when no project is named | `LaunchKey` | every writer and reader in `Parley.Setup`; nothing outside it |
| A legacy entry is read and never renamed | `LaunchEntries.For`, cases 2 and 3 of R3 | `EnrollmentPlan.Describe` |
| Ownership of a key vs. ownership of an entry | `LaunchKey.IsOurs` and `Enrolment.NamedBy` | writing consults the first, removing both |
| FR-008, the address resolved through the manifest | `EnrollmentPlan.Describe`, as a pure function of an endpoint map its callers supply | `Enrolment.AddAsync`, `EnrollCommand` |
| FR-011, an ambiguous withdrawal refuses | `Enrolment.WithdrawAsync` | `parley` and `parley-top`, which both call it |
| FR-013, one workspace catalogued once | `Catalogues` | `Enrolment.AddAsync` and `EnrollCommand` apply |
| The comparison that decides "the same repository" | `WorkspacePath.SameRepository` in `Parley.Core`, unchanged | `Catalogues` |

## Questions this plan settles

**Q1. Does the CLI need a project at all, or is the terminal enough?** It needs one.
`EnrollCommand.cs:803` calls `Describe` with four arguments and no project, so today only `parley-top`
can enrol into a named project. Without R5 this feature would be terminal-only and User Story 1, which
is written as a shell session, could not be performed. `--project` and a manifest field are in scope.

**Q2. Is a rename ever offered?** No — not as a flag, not as a prompt. The key is part of every tool
name the client exposes, and a rename changes an agent's vocabulary to achieve nothing it could not
achieve by being left alone. A repository that wants the new spelling gets it by being withdrawn and
enrolled again, which is two deliberate acts by a person who can see both.

**Q3. What happens to a repository in two projects when one broker is down?** Nothing new. Each entry
is a separate server making a separate connection, and SPEC.md §4.0 makes an open connection the
evidence an agent is reachable. The agent is simply absent from that project's roster and present in
the other's. No code is needed for this and none is planned; it is recorded because it looks like it
might need some.
