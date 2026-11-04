# Contract: the launch configuration and the commands around it

## File

```jsonc
{
  "parleyAgentId": "6f1c…",
  "mcpServers": {
    "parley-acme": {
      "command": "parley-mcp",
      "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme" }
    }
  },
  "parleyMachines": {
    "laptop": {
      "parley-acme": {
        "PARLEY_OWNS": "Answers questions about the infrastructure. Changes nothing."
      }
    },
    "server": {
      "parley-acme": {
        "PARLEY_OWNS": "Makes infrastructure changes on this host.",
        "PARLEY_BROKER": "http://127.0.0.1:19100"
      }
    }
  }
}
```

**Precedence, one rule for every reader:** this machine's block for this entry, then the launch environment or the entry's `env`.

**The workspace is not in the file.** When the file has a machine section, the workspace is the folder the file was found in. A node starts an agent from the folder in that machine's `node.json`.

## Commands

| Command | On a machine that does not describe the project |
|---|---|
| `parley enroll <dir> --project acme --broker <address>` | Asks the broker. Goes ahead if it serves `acme`. Writes this machine's block, including the address. Creates no project, store or port. |
| The same, broker serves another project | Exit 78: names both projects. Nothing written. |
| `parley enroll <dir> --broker <address>` | Exit 78: asks for `--project`. Replaces today's crash. |
| `parley up --project acme --broker <address>` | Asks the broker, as above, then joins. |
| `parley up --broker <address>` | Joins, announcing only the names for the project the broker serves. |
| `parley --launch-key parley-acme waiting <role>` | Uses this repository's effective address for that entry, or its project if there is none. For the hook. |
| Any other verb with `--project acme` | Refused as today. |
| Any verb, run in a repository, nothing else named | If exactly one entry has a block address for this machine, uses that address. |

## An agent-only machine

| Command | Does |
|---|---|
| `./install.sh --agent-only`, `.\install.ps1 -AgentOnly` | Installs `parley`, `parley-mcp` and the terminal. No broker, no broker entry. |
| The same with `--only` or `--reset` | Refused, saying why. |
| A plain install on a machine with `parley-node-*` entries | Stops and asks for `--agent-only` or `--force`. |
| `parley autostart install --project acme --broker <address>` | Confirms the broker serves `acme`, then installs and starts `parley-node-acme`. Says if it will stop at logout, with the command that fixes it. |
| `parley autostart status` | The broker entry, if any, and each node entry. |
| `parley autostart remove --project acme` | Removes that entry. With one entry on the machine, the project may be left out. |
| `parley up --keep-trying` | Waits and retries while the broker cannot be reached. Exits `Config` (78) on a refusal. |

## Messages

Each names what to fix. Wording is settled in the code. The content is the contract.

| Situation | Says |
|---|---|
| Disallowed setting in a block | The file, the machine, the key, the setting, and the two that are allowed. For `PARLEY_WORKSPACE`, also that each machine works its folder out from where the file is. |
| Machine name that can never be chosen | The file, the name as written, and the form it would need. |
| Block for a key with no entry | The file, the machine and the key. |
| Adapter cannot reach a project, and the file has blocks | The existing refusal, plus this machine's name and the machines the file has blocks for. |
| Enrol would change a role another machine uses | The file, both roles, and the other machine. |
| Broker serves another project | Both project names and the address. |
| Node on another protocol version | Both versions, and to install on both machines. |
| Entry will stop at logout (Linux) | That it will, and `loginctl enable-linger <user>`. |
| Entry needs a sign-in (Windows, macOS) | That it starts at logon, so a machine nobody signs in to will not run it. |
| Plain install over node entries | The entries found, and `--agent-only` or `--force`. |

## Wire (protocol 0.13)

```proto
message AttachRequest {
  // ...existing fields...
  string expected_project = N; // empty unless a machine block supplied the address
}

service Nodes {
  // ...existing calls...
  rpc ServedProject(Empty) returns (ServedProjectReply); // the project this broker serves
}

message JoinRequest {
  // ...existing fields...
  string protocol_version = N; // refused when it differs, naming both
}
```

`Attach` with a non-empty `expected_project` that is not this broker's project fails with `FailedPrecondition`, before any row is written, naming both. An `Agents` reply still carries no turn information, and `Agents` gains no operation.
