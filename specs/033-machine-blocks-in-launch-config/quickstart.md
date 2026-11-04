# Quickstart: try it on two machines

This walks the owner's example end to end. `laptop` runs the broker for project `acme`. `server` has no projects. For one machine, use two folders, a second `PARLEY_CONFIG` root and `PARLEY_MACHINE`. Bind nothing but loopback.

## 1. On `laptop`

```
parley enroll C:\src --project acme
```

Pick `infra`. Check `.mcp.json`: it holds no folder path, and `parleyMachines.laptop.parley-acme` exists. Check this machine's `node.json`: it holds the folder. Commit `.mcp.json` and push.

## 2. On `server`

You need the .NET 10 SDK, Git, a checkout of Parley, and the agent's client signed in.

```
./install.sh --agent-only          # from the Parley checkout
ssh -N -L 19100:127.0.0.1:19100 you@laptop      # keep this running your own way
git pull                           # in the infra checkout
parley enroll /srv --project acme --broker http://127.0.0.1:19100
parley autostart install --project acme --broker http://127.0.0.1:19100
```

If the last command says the node stops at logout, run the command it gives.

Check that:

- No broker is running, and `parley autostart status` lists only `parley-node-acme`.
- `parley project list` still shows no projects, and the Parley root has no `parley.db`.
- `parleyMachines.laptop` is unchanged, and `parleyMachines.server.parley-acme` is new and holds the address.

Commit it and push, then pull on `laptop`.

## 3. Give each its line

On each machine, accept that machine's line from a surface running there. Check `git diff`: only that machine's block moved.

## 4. See them apart

`parley registry` on `laptop` lists `infra@laptop` and `infra@server`, each with its own line and folder.

## 5. Ask each

- A Request to `infra@laptop` reaches only the laptop agent. Stop the agent first: the node starts it.
- A Request to `infra@server` starts the server's agent through the tunnel.
- A Request to `infra` opens one exchange with each.

## 6. Break it on purpose

| Do this | Expect |
|---|---|
| Point the server's block at another project's port | The adapter joins that broker: the address is the membership. `infra@server` shows in that project's roster. |
| Add `PARLEY_AGENT` to a block | Every reader says the file cannot be read and names the setting. The node carries on with its other repositories. |
| Stop the tunnel | The adapter says it cannot reach that address. `infra@server` stays registered. The node keeps trying and keeps its running agent. |
| Start the tunnel again | The node rejoins within a minute, by itself. The agent's process is the same one. |
| Restart the server | The node comes back. A Request to `infra@server` starts the agent once the tunnel is up. |
| Install the entry with another project's port | It is refused, naming both projects, and nothing is installed. |
| Run `./install.sh` with no flag | It stops, names `parley-node-acme`, and asks for `--agent-only` or `--force`. |
| Run `./install.sh --agent-only` again | The node stops, the tools are replaced, and the node starts again. |
| Add `PARLEY_WORKSPACE` to a block | The same refusal, and it says each machine works its folder out from where the file is. |
| Move the checkout on `server` and open it by hand | The attach is refused, naming both folders. Give it a different role or purge the old name, then enrol again from the new folder. |
| `parley enroll --broker <address>` with no project | Exit 78 with a sentence, not a crash. |
| Withdraw on `laptop` | `laptop`'s block goes. The entry, the identifier and `server`'s block stay. |
