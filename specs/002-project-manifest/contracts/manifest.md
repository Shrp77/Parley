# Contract: the project manifest file

**Feature**: 002-project-manifest

The manifest is an interface: the tool writes it, an operator may edit it, and the MCP adapter
reads it. All three must agree, so its shape and its refusals are specified here rather than left
to whatever the writer happens to emit.

## Location

| Platform | Path |
|---|---|
| Windows | `%APPDATA%\Parley\projects.json` — verified: `C:\Users\<user>\AppData\Roaming\Parley\projects.json` |
| Linux | `$XDG_CONFIG_HOME/parley/projects.json`, else `~/.config/parley/projects.json` |
| macOS | as resolved by the runtime; **asserted by test on CI rather than assumed here** |

Resolved by `Environment.SpecialFolder.ApplicationData` + `Parley/projects.json`. Overridden by
`--config <path>`, then `PARLEY_CONFIG`.

The per-platform mapping is checked by a test asserting the resolved path is absolute and under
the user's profile, run on all three CI platforms — the mapping on Linux and macOS was not
verified on the machine this was written on, and saying so is cheaper than being wrong about it.

## Shape

```json
{
  "_readme": [
    "Projects on this machine. Each is an independent Parley deployment: its own store,",
    "its own endpoint, its own agents. Names may repeat across projects — two projects may",
    "each have an `orders` — because a broker's connection cannot see another's file.",
    "",
    "Managed with `parley project add|remove|list`. Editing by hand is fine; the same rules",
    "are applied on read, so a mistake is refused rather than half-applied."
  ],
  "projects": [
    {
      "name": "orders",
      "store": "C:/deployments/orders/parley.db",
      "endpoint": "http://127.0.0.1:51707",
      "node_config": "C:/deployments/orders/node.json",
      "description": "The order-handling repositories."
    },
    {
      "name": "billing",
      "store": "C:/deployments/billing/parley.db",
      "endpoint": "http://127.0.0.1:51708",
      "node_config": "C:/deployments/billing/node.json"
    }
  ]
}
```

## Refusals

Each is a refusal rather than a warning, and each names the file. A configuration file that is
present but not understood must never be treated as absent — that is the failure this repository
has already met twice, once as an unreadable node catalogue treated as empty and once as a
wrong-typed node cast away and overwritten.

| Condition | Behaviour |
|---|---|
| File absent | Not an error. Previous resolution applies, and nothing is reported. |
| File present, not parseable | Refuse, naming the file and the parse error. |
| `projects` present but not a list | Refuse. Absent is the only shape meaning "none". |
| `projects` absent or empty | Valid. A machine with no projects configured. |
| An entry is `null` or not an object | Refuse, naming the index. |
| `name` missing or malformed | Refuse, naming the index and the grammar. |
| Two entries with the same `name` | Refuse, naming both indices. |
| Two entries with the same `store` | Refuse. Two brokers on one file breaks the isolation §4.1 relies on. |
| Two entries with the same `endpoint` | Refuse. Only one broker can hold a port. |
| `store` or `node_config` relative | Refuse. A relative path resolves against the caller's directory, which is the defect being removed. |
| `endpoint` unparseable | Refuse when read, not when a connection is attempted. |
| Directory holding `store` absent | Report as misconfigured. The file may be absent; the directory may not. |
| Unknown keys | Preserved on rewrite, not refused. An older tool must not silently drop a newer tool's field. |

## Writing

- Written whole, never appended to. The tool validates the entire manifest before writing any of
  it — the same rule the enrollment manifest follows, for the same reason: a half-written
  configuration is worse than none, because the part that did not land is indistinguishable from
  a part nobody meant to include.
- Indented, because a person reads and edits it.
- Byte-order mark and line endings of an existing file are preserved, so editing one project does
  not produce a whole-file diff.
- Unknown keys round-trip.

## What it never holds

- **Credentials.** Paths and endpoints only. This is what makes a well-known location safe, and
  it is the standing constraint that credentials are references — `env:`, `keychain:`, `file:` —
  resolved at invocation and never persisted.
- **Anything the protocol reads.** No broker, no agent and no message is aware this file exists.
- **Recorded reachability.** Whether a broker is running is observed by looking.
