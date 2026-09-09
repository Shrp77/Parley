# Quickstart: one repository, two projects

**Feature**: `009-multi-project-enrolment` | **Date**: 2026-09-09

This is SC-001 written as a session. It is what the feature is verified against on a live deployment,
not a demonstration written afterwards.

## Before

Two projects on one machine, each with its own store, endpoint and node catalogue:

```
parley project list
  alpha   127.0.0.1:51707   alpha.db   alpha-node.json
  beta    127.0.0.1:51708   beta.db    beta-node.json
```

One repository — `/src/inventory` — is already an agent of `alpha`. `beta`'s agents cannot reach it,
because brokers do not federate.

## Enrolling it in the second project

```
parley enroll scan /src beta-enrol.json
# the scan reports what each repository already is:
#   /src/inventory   answers in: alpha (as `inventory`)
#   /src/checkout    answers in: beta (as `checkout`)
#   /src/reports     enrolled nowhere

# tick /src/inventory in beta-enrol.json, give it beta's name for it and its own owns line
parley enroll plan beta-enrol.json --project beta
#   inventory  (/src/inventory)
#     .mcp.json: create parley-beta entry, leaving 1 other server
#     already an agent of 'alpha'; that entry is left exactly as it is

parley enroll apply beta-enrol.json --project beta
```

`.mcp.json` now holds `parley-alpha` and `parley-beta`. The `parley-alpha` entry is byte-for-byte what
it was.

## Checking it from both sides

```
parley --project alpha registry     # lists inventory@this-machine
parley --project beta   registry    # lists stock@this-machine — beta's name for the same repository
```

Open the repository in an editor. Two parley servers start, one per project, and the client namespaces
their tools: `mcp__parley-alpha__parley_ask` and `mcp__parley-beta__parley_ask`. Asking a question
through the first reaches `alpha`'s peers and no message crosses to `beta`.

## Withdrawing from one

```
parley-top → the beta project → the agent → Withdraw
#   .mcp.json: parley-beta entry removed, 1 parley entry and 1 other server left
```

It goes on answering in `alpha`.

## What a repository enrolled before this looks like

Nothing changes and nothing is edited:

```
{ "mcpServers": { "parley": { "command": "parley-mcp",
    "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha", … } } } }
```

`parley enroll scan` reports it as answering in `alpha`. Enrolling it in `alpha` again updates that
entry where it stands — **no `parley-alpha` key is created and the key is never renamed**, because the
key is part of every tool name the agent already has. Enrolling it in `beta` adds a second entry beside
it.

## The one thing that is refused

```
parley enroll apply beta-enrol.json --project beta
#   inventory: 'alpha' already lists this workspace for unattended start, in alpha-node.json.
#   A repository may answer in several projects and may be started unattended by only one.
```

A node starts an agent with its workspace as the working directory, so two nodes starting one workspace
is two models editing one checkout. The launch entries are still written; it is the catalogue entry —
standing consent to run with nobody watching — that stays singular.
