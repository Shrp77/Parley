# Lens 3: API surface and maintainability

Part of [the review gate](README.md).

## What is medium or above

- The wire contract or a public surface disagreeing with SPEC.md or the proto. Which side changes is a decision, not an
  assumption: say so, and put it to the owner when it changes behaviour.
- An instruction, a tool description or a refusal sentence that would lead an agent or a person to do the wrong thing.
- Documentation that contradicts the code: SPEC, README, `docs/`, plugin commands and skills, install script output,
  `.specify/memory/constitution.md`. A doc promising a screen, a report or a guarantee nothing provides is medium.
- A rule in CLAUDE.md that the code breaks.
- A test that passes with its guard removed.

Low: a wrong word that misleads nobody, a stale citation, a comment out of place.

## How to probe

- **Read a document end to end against the code**, not a line at a time; the ledger names which documents earlier runs
  read, so pick ones it does not.
- **Mutate to check a test's claim**: turn the guard off with a change that compiles and watch the test fail.
- When a fix rewords a document, check every other document that says the same thing: a sweep that misses one is
  what the next run finds.

## Settled by the owner

- The fetch destination rule stays.
- The person may be the agent a roadmap item is for; Parley may not.
- Only local storage.
- **Parley is not addressable** by agents for now; agents ask the person directly.
- **A turn-cost report, in US dollars, comes after #94** as its own feature. Until then the docs say the cost is
  recorded and shown nowhere.
- **Credential references are not built yet.** SPEC §12 and the docs say so; building `env:` and `file:` is a later
  feature.
- The review rules live in this folder, not in CLAUDE.md.
- The lows triaged in the ledger's 2026-10-29 entry as moot or by design stay that way.
