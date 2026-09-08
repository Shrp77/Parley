# Data model: 007 — the tool set

No stored data changes. No schema change, no migration. The entities here are packaging.

## Tool

A separately installable executable produced from this solution.

| Field | Meaning |
|---|---|
| Package | The project that produces it |
| Command | What it is called on PATH |
| Surface | `none`, `terminal`, or `web-ui` — which surface it is, for install selection |
| Carries | What ships inside it |

### After this change

| Package | Command | Surface | Carries |
|---|---|---|---|
| `Parley.Cli` | `parley` | none | broker, node, one-shot commands, the forwarding wrapper |
| `Parley.Mcp` | `parley-mcp` | none | the agent adapter |
| `Parley.Top` | `parley-top` | terminal | the terminal |
| `Parley.WebUi` | `parley-web-ui` | web-ui | the browser surface and its host |

**Invariants.**

1. No tool with a surface may reach a store.
2. `parley` may not reach `Parley.Web`.
3. `parley-mcp` is unchanged, in content and in references.
4. Every tool is built from one solution, so all four carry the same `VersionPrefix`.

## Machine profile

Which tools a machine has. Not stored anywhere — it is the outcome of an install — but it is the
thing `-Surfaces` selects and the thing SC-005 checks.

| Profile | Tools | Typical |
|---|---|---|
| Full | all four | a developer's workstation |
| Headless with a web UI | `parley`, `parley-mcp`, `parley-web-ui` | a Linux agent host reached by SSH tunnel |
| Headless bare | `parley`, `parley-mcp` | an agent host nobody operates |
| Terminal only | `parley`, `parley-mcp`, `parley-top` | a machine operated over SSH |

The adapter is in every profile because every profile hosts agents. The broker's tool is in every
profile because it holds the node.

## Node catalogue

Unchanged in content and format. Only its reader moves.

| | Before | After |
|---|---|---|
| Reader | `Parley.Headless.NodeCatalogue` | `Parley.Configuration.NodeCatalogue` |
| Reader's dependencies | `System.Text.Json`, `Parley.Core` | unchanged |
| Assembly's dependencies | Core, **Store** | Core |
| Readable by a surface without a store route | **No** | Yes |

The file itself — its path, its shape, its `agents` map of role to command and workspace — does not
change. A catalogue written by today's build is read by tomorrow's.

## Web UI address

| | Before | After |
|---|---|---|
| Home | `Parley.Web.ConsoleHost.LoopbackAddress` | `Parley.Configuration` |
| Value | `http://127.0.0.1:5280` | unchanged |
| Read by | `Parley.Cli` (3), `Parley.Cli/Top` (1), `Parley.Web` (2) | `Parley.Cli`, `Parley.Top`, `Parley.WebUi`, `Parley.Web` |

It moves because after the split `Parley.Cli` may not reference `Parley.Web`, and three of its
readers are in `Parley.Cli`. It stays a single constant because `ConsoleAddressTests` exists for the
defect that appears when it is not: a hand-copied `5045` against a real `5280`, so the one action
that hands off to a browser opened a dead port and reported success.

## What does not change

- The store, its schema, and its migrations.
- The wire contract and the generated transport.
- `PARLEY_AGENT`, `PARLEY_OWNS`, `PARLEY_WORKSPACE`, and how an adapter takes its identity.
- The project manifest.
- What either surface displays.
