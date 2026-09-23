# Contract: the catalogue, the membership, and what the node does with them

Two files and three operations. Everything the node knows about what it may start comes from here, and
nothing in it is project-scoped except the names, which the repositories own.

## The machine's catalogue

One per machine, beside the manifest. Keyed by identifier.

```json
{
  "agents": {
    "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011": {
      "command": "claude",
      "workspace": "C:\\src\\billing-infra"
    }
  }
}
```

- The key is the identifier (R1): a lowercase hyphenated GUID, opaque, never parsed.
- `command` and `workspace` are what it takes to start the thing. Nothing else belongs here.
- **No project, and no name.** Those are the repository's to declare (R3). A catalogue that carried them
  would be the second place to say one thing.
- One entry per identifier, by the shape of the file. "Catalogued twice for unattended start" is not
  expressible, which is what deletes the gate, the scan and 009's deviation.

## The repository's launch configuration

Unchanged in shape except for one addition. It already carries one entry per project it answers in, keyed
`parley-<project>`, with the name in `PARLEY_AGENT`.

```json
{
  "mcpServers": {
    "parley-orders":  { "command": "parley-mcp", "env": { "PARLEY_AGENT": "infra",         "PARLEY_PROJECT": "orders" } },
    "parley-billing": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing-infra", "PARLEY_PROJECT": "billing" } }
  },
  "parleyAgentId": "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011"
}
```

- `parleyAgentId` is the identifier, minted once by enrolment and never derived (FR-002).
- The two entries are what make `infra` and `billing-infra` **two names for one identity**.
- Everything that is not this tooling's is left alone, as it already is.

## What the node announces, per project

For each broker it joins, serving project `P`:

```
announced(P) = { PARLEY_AGENT of entry e
                 | e in launchEntries(workspace of c), for each catalogue entry c
                 , project of e == P }
```

So a name is announced for `P` when some catalogued workspace says it answers to that name in `P`.

| case | announced |
|---|---|
| catalogued, declares a name for `P` | that name |
| catalogued, declares nothing for `P` | nothing, and not an error — it belongs to other projects |
| catalogued, workspace gone | nothing for any project; reported, entry left alone (FR-014 in 012) |
| catalogued, launch configuration unreadable | nothing for any project; reported with the path |
| two catalogued workspaces declare one name for `P` | **nothing at all for `P`**, and both paths reported (R4) |
| registered on this machine, not catalogued | nothing — and this is what the person must be told (R8) |

## What a start resolves to

The broker asks by name, as it always has. Given `start(name, project)`:

1. `identifier` ← the catalogue entry whose workspace declares `name` for `project`.
2. If none, refuse and say so, naming the machine's catalogue — a start for a name this node announced but
   can no longer resolve is a catalogue that changed under it, not a protocol error.
3. If a resident for `identifier` is alive, **give it a turn**. Do not start a second.
4. Otherwise start `command` in `workspace`, record it under `identifier`, and give it a turn.

**Keyed by identifier, and the table is shared across every project one process serves** (R5). Two projects
asking for one checkout at the same moment therefore produce one process, and both projects' adapters attach
to it — which is what already happens when a person opens the repository by hand.

## Refusals, and what each says

| case | what happens | what is said |
|---|---|---|
| name not resolvable to an identifier | no start | names the catalogue file, so "started in the wrong place" cannot be mistaken for "nobody catalogued it" |
| workspace gone | no start | names the path; the entry is not removed and nothing is retired |
| two workspaces claim one name | nothing announced for that project | both paths, so the copy can be found |
| already running | a turn, not a start | nothing; this is the ordinary case |
| registered here, not catalogued | the request is accepted and queued | said in the composer before Enter, and in the report after (R8) |

## What enrolment does

| enrolling | catalogue | repository |
|---|---|---|
| a checkout with no identifier | mints one, writes one entry | writes the identifier and the project's name |
| a checkout already catalogued, into a further project | **unchanged** | adds the project's name |
| a checkout whose identifier is already catalogued at another path | refused, naming both paths | unchanged |

The third row is the copied-repository case (R2). It is refused by the file's own key rather than by a scan,
and the refusal names both paths because the remedy — re-mint, or point the entry at the right checkout — is
a decision only a person can take.

## What this contract deletes

- the per-project catalogue, and `projects.json`'s binding for it
- the machine-wide enrolment gate and the cross-project scan
- the rule that a second project gets no catalogue line
- `serve`'s refusal of one explicit catalogue offered to several projects, which was a rule about a
  per-project file
