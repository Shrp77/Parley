# Contract: the command surface after the split

What each tool answers to. This is the contract FR-008 and SC-006 are checked against: every
document that tells somebody what to type is measured against this table.

## `parley` — broker, node, commands

| Command | Change |
|---|---|
| `parley serve [--db \| --config] [--listen] [--log] [--with-node]` | unchanged |
| `parley serve --with-web-ui [--web-ui <url>]` | **renamed** from `--with-console` / `--console`. Now starts the separate tool as a child |
| `parley serve --with-console [--console <url>]` | **deprecated.** Works, warns once, names the new spelling. Removed at the next minor |
| `parley top [...]` | **forwards** to `parley-top` when installed; says how to install it when not |
| `parley web-ui [<url>]` | **forwards** to `parley-web-ui`. Renamed from `parley console` |
| `parley console [<url>]` | **deprecated**, as above |
| `parley registry`, `project`, `export`, `retire`, `prune`, `autostart`, `up`, `node`, `enroll`, … | unchanged |

**The wrapper is thin.** It resolves the tool's command name, starts it with the arguments it was
given, and returns its exit code. It does not parse the surface's arguments, so a new terminal flag
needs no change here.

**When a surface is not installed**, the wrapper says which tool provides the command and how to
get it. It does not fail with "unknown command", which would be untrue.

## `parley-top` — the terminal

| Command | Notes |
|---|---|
| `parley-top [--config <manifest>] [--broker <url>]` | The same arguments `parley top` takes today |

Refuses when input is redirected, as today. Opens no store.

## `parley-web-ui` — the browser surface

| Command | Notes |
|---|---|
| `parley-web-ui [<url>]` | Defaults to the loopback address from `Parley.Configuration` |

Opens no store. Reaches the broker over the transport. Binding beyond loopback remains the
operator's deliberate act, and §16 is unchanged.

## `parley-mcp` — the agent adapter

Unchanged. Listed so that its absence from the diff is deliberate rather than assumed.

## Version mismatch (FR-011, SC-007)

A surface and a broker can now be different versions. When the surface finds a broker whose §10.2
version it does not speak, it must:

1. Say so, naming **both** versions.
2. Show nothing it cannot vouch for.

The terminal already has this shape: `ProjectsRegion.Describe` renders
`speaks <theirs>, this build speaks <ours>` for a project whose broker disagrees, and its remark
already argues why both numbers are named — *"'incompatible' without them cannot be acted on"*. That
wording and that reasoning extend to the whole surface, rather than a new mechanism being invented.

## Install (FR-009, FR-010, FR-013)

| Argument | Meaning |
|---|---|
| `-Surfaces terminal \| web-ui \| both \| none` | Which surfaces to install. Default `both` on Windows, `web-ui` on Linux |
| `-Force` | Ends processes holding a tool being replaced, as today, now including a running terminal |
| `-Uninstall` | Removes every tool it installed, and the autostart entry |

**Per-tool reporting.** Each tool reports `installed`, `unchanged`, or `skipped`, so "the terminal
was replaced and the broker was not" is distinguishable from "nothing happened" — which is the
failure `install.ps1` was written for in the first place.

**Before stopping a broker**, the script asks it whether any exchange is open and names them. If it
cannot ask, it says so rather than implying nothing is in flight.
