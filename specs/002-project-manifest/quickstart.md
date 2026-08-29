# Quickstart: running several projects on one machine

**Feature**: 002-project-manifest

This is documentation that tells somebody what to type, so it is held to the same standard as the
code — every command here is checked by a test.

## You have one project and it works

Nothing to do. With no manifest, every command resolves exactly as it does today. This page is
for the second project.

## Adding projects

```
parley project add orders  --db C:/deployments/orders/parley.db  --listen http://127.0.0.1:51707 --node-config C:/deployments/orders/node.json
parley project add billing --db C:/deployments/billing/parley.db --listen http://127.0.0.1:51708 --node-config C:/deployments/billing/node.json
```

The manifest is created for you, at the per-user configuration location for your platform. You do
not need to know where that is; `parley project list` will tell you.

Reusing a port or a store is refused, naming the project that already has it. Two brokers cannot
hold one port, and two brokers on one file would break the isolation the whole design rests on.

## Seeing what a machine runs

```
parley project list
```

```
orders    http://127.0.0.1:51707  reachable
          store  C:/deployments/orders/parley.db
          node   C:/deployments/orders/node.json
billing   http://127.0.0.1:51708  not reachable
          store  C:/deployments/billing/parley.db
          node   C:/deployments/billing/node.json

manifest: C:/Users/you/AppData/Roaming/Parley/projects.json
```

Reachability is checked by looking, not recorded — a written-down status is a claim that goes
stale the moment it changes.

## Starting them

```
parley serve
```

One process, one broker per project. If one fails — a corrupt store, a port already held — the
others keep serving, the failure is named, and the exit code says a subset is running rather than
reporting success. Ctrl+C stops all of them.

To start one only:

```
parley serve --project orders
```

To have them start at login, as before, now without repeating paths:

```
parley autostart install
```

## Using a project

Every command takes `--project`:

```
parley --project orders registry
parley --project billing enroll scan C:/work/billing-repos
```

With exactly one project configured you can leave it out. With several, leaving it out is refused
and the names are listed — the tool will not choose which deployment you meant.

Anything you pass explicitly still wins:

```
parley --project orders registry --broker http://127.0.0.1:9999
```

Commands that are about to write say what they resolved and why:

```
node catalogue: C:/deployments/orders/node.json   (project "orders")
```

That line is the point of the feature. It is what was missing when enrolling ten repositories
would have written consent into a catalogue nothing reads.

## Pointing repositories at a project

`parley enroll` writes each repository's launch configuration with the project's endpoint, so
nothing extra is needed.

A repository may instead name the project:

```json
{ "mcpServers": { "parley": { "command": "parley-mcp",
  "env": { "PARLEY_AGENT": "orders-api", "PARLEY_PROJECT": "orders" } } } }
```

Know the trade before you commit that file. On a machine without an `orders` project the agent
**refuses to start**, naming the project. That is deliberate: the alternative is connecting to
whatever deployment is local, which would be silently wrong and invisible from both ends of a
message. If the repository is shared, prefer the explicit endpoint — and if both are present, the
endpoint wins.

`PARLEY_AGENT` and `PARLEY_OWNS` are unaffected. A project says where to connect, never who is
connecting.

## Removing a project

```
parley project remove billing
```

Removes the entry and nothing else. The store, the node catalogue and every repository's launch
configuration stay exactly where they are, and the command tells you so. If you want the data
gone, delete it deliberately.
