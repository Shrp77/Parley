# The review gate

A pull request merges when three lenses each pass three times in a row on the same shipped code, and the full test run
(the `nightly` workflow, which the `full-tests` label starts on a pull request) is green on the commit that merges. A commit that changes only tests, CI, docs or plugin prose keeps the counts (see below). This folder holds the rules for that gate. CLAUDE.md points here instead of carrying them.

| File | What it covers |
|---|---|
| This file | How the gate runs, what counts, what resets a count, how a pass is done safely |
| [correctness.md](correctness.md) | Lens 1: correctness and concurrency |
| [security.md](security.md) | Lens 2: security and data integrity, and the web console's defences |
| [surface.md](surface.md) | Lens 3: API surface and maintainability: SPEC, docs, plugin prose and tests against the code |

## Two parts

**The floor runs as tests.** `tests/Parley.Spec.Tests` checks SPEC.md: section references resolve, no table is
ragged, no operation name spans the §10.1/§10.2 boundary, §8's rules are numbered contiguously and all appear in its
enforcement table, `proto/parley.proto` agrees with the document, and every schema fragment executes against SQLite in
the order written. Do not hand-verify a constraint and move on; add the case. Triggers contain semicolons, so they
are matched to `END;`, and `The_document_contains_a_schema` guards against an extraction that silently matches
nothing.

**The lenses are judgement.** Each lens has its own file, which says what is medium and above for it, how to probe
it, and what the owner has already settled.

## What counts

- **A clean run** finds nothing above low. Three clean runs in a row per lens, all three lenses, on one version of
  `src`.
- **A finding above low resets that lens to zero.** After fixing it, run the same lens again before moving on.
- **A change to `src` restarts every lens**, because it is a new commit of shipped code. A change only to tests, CI,
  docs or plugin prose keeps the counts: the code the clean runs read is unchanged.
- **Each run must probe something the earlier runs did not.** Re-reading the same code is one run repeated.
- **Probe, do not reason.** Write the malicious case and watch it be admitted. A finding found only by reading says so.
- Run the lenses after the last edit, not alongside it.
- **CI must have run against `HEAD`.** `gh pr checks --watch` can return the previous run, so compare the run's commit
  with `git rev-parse HEAD`.

## Which tests run when

Every test class is `[Unit]` or `[Integration]` (`tests/TestCategories.cs`; a test in each project fails if a class has
neither). A unit test touches one module in memory: no file, no database (an in-memory one included), no socket, no
process, no clock it does not own, no environment it does not inject, and no UI. Everything else is integration, even two modules together.

- **Between review rounds and before each commit, run the unit tests**: `pwsh scripts/test.ps1` (seconds). Add
  `-Category All` when a change could only show in an integration test, such as a race or a process boundary.
- **A push runs the unit tests** on Windows, Linux and macOS (`build.yml`).
- **The `nightly` workflow runs everything** on Windows and Linux, every night against `main`, and on a pull request
  while it carries the `full-tests` label (`gh pr edit <number> --add-label full-tests`; each push reruns it). The
  merge needs that run green on the commit that merges. Once the workflow is on `main` it can also be started by hand:
  `gh workflow run nightly.yml --ref <branch>`.

Every run records its results in the git-ignored `.test-metrics/` (`runs.jsonl` per run; `tests.jsonl` per test that did
not pass, and per test on CI, which passes `-AllResults`); `pwsh scripts/pull-test-metrics.ps1` copies CI's runs in beside the local ones.

## The ledger

The running count lives in the untracked `REVIEW-LEDGER.md`. Log every run, clean ones too, with what it probed.
Record a real finding below the bar as *low, recorded not acted on*. A decision the owner has made is recorded once
and not re-raised; each lens file lists the ones that bear on it.

## Doing a pass safely

- **Bind nothing but loopback** (127.0.0.1 or ::1). Binding 0.0.0.0, [::] or a LAN address pops a firewall prompt
  on the owner's desktop. Probe address parsing by calling the parser.
- **A network probe uses a TEST-NET address** (192.0.2.x), which never routes, and a fresh host each time: Windows
  remembers a host it failed to reach and answers the next try at once, which hides a lookup that slipped through. A
  lookup that did slip through shows as the 21-second SMB wait.
- **The machine is short of memory.** Run targeted tests only, one test host at a time, never a full suite while
  other passes run. Check free memory first and pause under 7 GB.
- **A mutation must compile.** One that does not proves nothing, because the tests run the previous binary. Back the
  file up with `mktemp`, never a fixed name, restore it, `touch` it, and check `git diff --stat` before rebuilding.
- Name a temporary probe `Zz…R<run>`, delete it and anything it made (links included), and rebuild at the end.
- Never touch `%APPDATA%` or a live store, and leave the owner's running broker and web UI alone.
- A reviewer does not commit, push, check out, or edit `REVIEW-LEDGER.md` or `CLAUDE.md`.
