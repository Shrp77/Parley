# Contract: the command surface

**Feature**: 002-project-manifest

Exit codes follow the tool's existing convention: `64` usage, `78` configuration, `74` I/O,
`69` unavailable, `0` success.

## New commands

```
parley project list                                  what this machine is configured for
parley project add <name> --db <path> --listen <url> add a project
                         [--node-config <path>] [--description <text>]
parley project remove <name>                         forget how to reach a project
```

These are **operator** commands. They live on `Parley.Cli`, which the MCP server does not
reference, so an adapter has nothing to bind — the same structural split that keeps `retire` and
`purge` off an agent's tool surface.

### `project list`

Names each project with its store, endpoint, catalogue, and whether it is reachable. Reachability
is a bounded connection attempt per project, so one dead project does not stall the report.

| Condition | Exit | Output |
|---|---|---|
| Projects configured | 0 | One row per project; unreachable ones marked, not omitted. |
| No manifest | 0 | Says none is configured and where one would live. Not an error. |
| Manifest unreadable | 78 | Names the file and the problem. |

### `project add`

| Condition | Exit | Output |
|---|---|---|
| Valid, no conflict | 0 | Writes the manifest, creating it and its directory if absent. Reports the path written. |
| Name, store or endpoint already used | 78 | Names the conflicting project. Manifest unchanged. |
| Name malformed, path relative, endpoint unparseable | 64 | Names the field and the rule. |
| Manifest unreadable | 78 | Refuses. Never starts a fresh manifest over one it could not read. |

### `project remove`

| Condition | Exit | Output |
|---|---|---|
| Project exists | 0 | Removes the entry. **States that the store, catalogue and any launch configurations remain, and where.** |
| Project exists and is reachable | 0 | As above, and reports that a broker appears to be running for it. |
| No such project | 78 | Names it, lists what is configured. |

Removal is withdrawal, not destruction — the distinction `retire` and `purge` already draw. A
configuration command that deleted a store as a side effect would be the most destructive
operation in the tool wearing the least alarming name.

## Changed commands

Every command that needs a store, an endpoint or a catalogue accepts `--project <name>` and
`--config <path>`.

| Command | What a project supplies |
|---|---|
| `serve` | store, endpoint. With no project named and several configured, **starts one broker per project** in one process. |
| `up` | endpoint, node catalogue. |
| `enroll` | node catalogue, and the endpoint written into each `.mcp.json`. |
| `registry`, `retire`, `export`, and the rest | endpoint. |
| `autostart install` | store and endpoint, so a supervisor entry names a project rather than repeating paths. |

### Resolution, reported

Precedence is argument, then environment variable, then named project, then previous default —
per value, independently. Any command that is about to write reports the resolved path and its
source first, so `plan`-style previews can be trusted:

```
node catalogue: C:/deployments/orders/node.json   (project "orders")
broker:         http://127.0.0.1:51707            (--broker)
```

| Condition | Exit | Behaviour |
|---|---|---|
| No manifest, no project named | — | Exactly today's resolution, value for value. Nothing reported. |
| One project configured, none named | — | That project is used. |
| Several configured, none named | 78 | Refuse, list the names. Never choose. |
| Named project absent from manifest | 78 | Refuse, name it, list what is configured. Never fall back. |

### `serve` hosting several projects

| Condition | Exit | Behaviour |
|---|---|---|
| All projects start | 0 | Reports each project and its endpoint. |
| Some start, some fail | 69 | Each failure named with its cause; the rest keep serving; **the process must not exit 0 while running a subset.** |
| None start, but projects were configured | 78 or 74 | Distinct from an empty machine, which is 0. |
| Interrupted | 0 | **One interrupt stops every broker started.** Each host installs its own console lifetime, so the signal is owned once, above all of them. |

## Agent-side resolution

The MCP adapter may resolve `PARLEY_PROJECT` to an endpoint. It gains no ability to write the
manifest.

| Launch configuration | Behaviour |
|---|---|
| `PARLEY_BROKER` only | As today. |
| `PARLEY_PROJECT` only, project known | Connects to that project's endpoint. |
| `PARLEY_PROJECT` only, project unknown | **Refuses to start**, naming the project and the manifest consulted. Never falls back to a default endpoint: connecting an agent to the wrong deployment is worse than an agent that does not start, and is undetectable from either side of a message. |
| Both, agreeing | Connects. |
| Both, disagreeing | Endpoint wins; the disagreement is reported. |

`PARLEY_AGENT` and `PARLEY_OWNS` are untouched by any of this. A project supplies *where to
connect*, never *who is connecting* — the launch environment remains the sole source of identity,
and no operation accepts one.
