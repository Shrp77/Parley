# Data Model: A repository may answer in more than one project

**Feature**: `009-multi-project-enrolment` | **Date**: 2026-09-09

No database changes. Nothing here is stored, sent, or seen by a broker. These are the shapes in one
file on one machine, and the rules that decide what may be written to it.

## Launch entry

One server in a repository's `.mcp.json`. A repository may hold several.

| Field | Source | Notes |
|---|---|---|
| key | derived | `parley-<project>`, or `parley` when no project is named |
| `command` | written | `parley-mcp`; a person may change it and it stays their entry |
| `env.PARLEY_AGENT` | the person | the role. **The identity**, per SPEC.md §16 — the only field that decides whether an entry is ours to remove |
| `env.PARLEY_PROJECT` | derived | present exactly when a project was named |
| `env.PARLEY_BROKER` | legacy | an address instead of a project name. Never written by this feature; read by FR-008 |
| `env.PARLEY_OWNS` | the person | per entry, so one repository may say different things to different rosters |
| `env.PARLEY_WORKSPACE` | derived | `${PARLEY_WORKSPACE:-<path>}`, unchanged |

**Invariant**: at most one entry per project per repository. Enforced by the precedence below, not by a
uniqueness check after the fact — there is no moment at which two entries for one project exist.

## Key

`parley` | `parley-<project>` where `<project>` matches `^[a-z][a-z0-9-]{0,31}$`.

- **Injective.** Two projects cannot produce one key.
- **Never collides with the legacy key.** A project name is at least one character, so `parley-x` is
  the shortest derived key and `parley` is not in the derived range.
- **Safe unescaped** in JSON and in a client's tool names: at most 39 characters, lowercase, digits and
  hyphens.
- A project may be called `parley`. Its key is `parley-parley`, which is fine and is a test case.

## Two ownership questions

| Question | Answered by | Used when |
|---|---|---|
| May this tooling write at this key? | the key's shape | choosing where to write, and refusing FR-009 |
| Is this entry ours to remove? | `PARLEY_AGENT` is present *and* matches the agent named | withdrawing |

They are separate because a hand-written server may occupy a key we would like, and an entry we wrote
may have had its `command` edited by somebody running the adapter from a build directory. Conflating
them either deletes a stranger's configuration or refuses to clean up our own.

## Precedence: which entry a project's enrolment lands on

Evaluated in order; the first match wins.

1. **`parley-<project>` exists** → update it in place.
2. **`parley` exists and pins this project** → update it in place. *Never renamed.*
3. **`parley` exists and pins no project at all**, and a project is being named → re-point it in place
   and say so. This is every repository enrolled before projects existed.
4. **Otherwise** → write a new `parley-<project>`, refusing first if that key is held by an entry that
   is not ours.

Case 2 before case 4 is what keeps a live agent's tool names stable. Case 3 after case 2 is what stops
an unpinned entry swallowing an enrolment meant to be a second one.

## The address case (FR-008)

An entry pinning `PARLEY_BROKER` and no project, resolved against *project name → endpoint* from the
machine's manifest:

| The address is | Outcome |
|---|---|
| the endpoint of the project being enrolled into | refused: the repository is already that project's agent by another spelling |
| another project's endpoint | proceed as an addition — this is a repository in two projects, spelled the old way once |
| not any project the manifest knows | refused, naming the address. Guessing permissively here is what loses an agent |
| — and there is no manifest | refused, as today |

## Node catalogue entry

Unchanged in shape: `agents[<role>] = { command, workspace }`, one file per project.

**New rule (FR-013)**: across every project in the machine's manifest, a workspace appears in at most
one catalogue. Compared with `WorkspacePath.SameRepository` — the textual comparison SPEC.md §4.1.1
defines and forbids from consulting a filesystem.

This is the one asymmetry in the feature and it is deliberate: **a repository may hold many launch
entries and at most one catalogue entry.** A launch entry does nothing until a person opens the
repository; a catalogue entry is standing consent for a machine to start a turn with nobody watching,
and a resident agent runs with its workspace as its working directory.
