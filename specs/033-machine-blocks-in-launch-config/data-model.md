# Data model: One launch configuration for every machine

No table changes. No migration. Everything here lives in a repository's `.mcp.json`.

## Machine section

A top-level member, `parleyMachines`, beside `mcpServers` and `parleyAgentId`.

| Level | Key | Rule |
|---|---|---|
| Machine | Machine name | Must already be in the form `MachineName.Sanitise` produces. |
| Project entry | Launch key (`parley` or `parley-<project>`) | Must exist in `mcpServers`. |
| Setting | `PARLEY_OWNS`, `PARLEY_BROKER` | Nothing else. String values only. A block may be empty: it says this machine is enrolled in that project. |

**Validation.** A section breaking any rule makes the file *could not be read*, for every reader and every machine. That includes a block for another machine: every reader must reach the same answer (FR-005), and a typing mistake must not wait for the day that machine restarts.

Values follow the rules for the same setting elsewhere: `OwnershipStatement` for the line, `BrokerEndpoint` for the address. The folder the file was found in still goes through `LocalStorage` and `OffMachineLinks`.

## Effective settings

`LaunchConfiguration` (Parley.Configuration, which both the adapter and Setup reach) gains one method. Nothing else computes this.

```
input:  the file, machine name, launch key, the launch environment
output: Owns, Broker, Workspace, and whether the broker came from a block
```

For `Owns` and `Broker`, in order:

1. This machine's block for this key.
2. The launch environment, which already holds the shared entry's value after the client merged it.
3. Nothing.

A reader that has only the file (enrolment, the hook's CLI route) uses the entry's written `env` at step 2.

`Workspace` is the folder the file was found in, when the file has a machine section. With no section, it is what it is today.

## Writes

Every write goes through the existing held-file route, so its locking and byte-order mark handling are unchanged.

| Act | Writes |
|---|---|
| Enrol, new entry | Shared: `command`, `PARLEY_AGENT`, `PARLEY_PROJECT`. Block: owns and the address, each if there is one, and created even if empty. Identifier if the file has none. No folder path. `node.json` still gets this machine's folder. |
| Enrol, entry exists | This machine's block. Shared values are untouched (FR-018). **Refused** if the role differs from the entry's and another machine has a block for this key, because that would rename the other machine's agent. |
| Accept, edit or remove a line | This machine's block, that key only. Removing a line takes the value out and keeps the block, because the machine is still enrolled. |
| Withdraw | This machine's block for the key. If no other machine's block names the key, the entry goes as it does today. The identifier goes only with the last entry. A machine with no blocks left is removed, and so is a section with no machines left. |

**Machine for a write:** `agent.Machine`, which `OwnershipLine.NotHere` has already checked is this machine.

## Unchanged

- `agents` rows, `role@machine`, the per-machine identity index.
- `node.json`: still one entry per identity on this machine.
- `LaunchMembership`: roles and projects stay shared, so what a node announces cannot vary by block. It gains the entry's key.
