# Quickstart: one checkout, two projects, one agent

The walk that proves SC-001 and SC-002. It is the owner's own case, reduced to placeholders.

**Run it against a throwaway deployment**, not a live one: tools packed and installed into a scratch path,
each project on its own store and port. 016's walk records why — the terminal refuses a redirected terminal,
and the web console resolves the manifest through the platform's known-folder API, so it cannot be pointed
somewhere else.

## Before

- Two projects on one machine, `alpha` and `beta`, each with its own store and endpoint.
- One repository, `orders-repo`, checked out once.
- No catalogue, no launch configuration — a cold machine.

## Step 1 — Enrol the checkout in `alpha`

```
parley --project alpha enroll apply --workspace <orders-repo> --agent orders
```

**Expect**

1. The repository's launch configuration gains `parley-alpha` with `PARLEY_AGENT=orders`.
2. It gains an identifier — a GUID — that was not there before.
3. The machine's catalogue gains **one** entry, keyed by that identifier, naming the command and the
   workspace.
4. The agent is registered in `alpha` and reachable: `parley --project alpha registry` lists `orders@<machine>`.

**Check**: the catalogue names no project and no agent name. Only the identifier, the command and the path.

## Step 2 — Enrol the same checkout in `beta`, under a different name

```
parley --project beta enroll apply --workspace <orders-repo> --agent shipping
```

**Expect**

1. It is **not refused**. No gate, no "already catalogued for another project", no scan.
2. The launch configuration gains `parley-beta` with `PARLEY_AGENT=shipping`.
3. The identifier is **unchanged**.
4. The catalogue is **unchanged** — still exactly one entry.

**Check**: `orders` and `shipping` are now two names for one identity. This is the step that was impossible.

## Step 3 — Ask it from `beta`, the project whose catalogue never named it

Stop everything the machine started, so nothing is running. Then, as a person seated in `beta`, send
`shipping` a Request.

**Expect**

1. It **starts**. This is the defect, gone.
2. It collects the Request and answers.
3. `parley --project beta registry` shows it attached.

**Check the log**: the node's join line for `beta` announces `shipping`; the line about agents "registered
here but not in the catalogue" does not name it.

## Step 4 — Ask it from `alpha` while it is running

Send `orders` a Request from `alpha`.

**Expect**

1. **No second process.** One process, two adapters, two names.
2. The Request is collected and answered.

**Check**: count the processes for that workspace. Exactly one. This is SC-002, measured rather than argued.

## Step 5 — Two projects at once

With nothing running, send a Request from `alpha` and one from `beta` as close together as a person can.

**Expect**: one process starts, both Requests are collected. The resident table is keyed by identifier and
shared, so the second ask finds the first's process.

## Step 6 — Move the checkout

Stop everything. Move `orders-repo` to a new path. Repoint the one catalogue entry at it.

**Expect**

1. The identifier is unchanged — it moved with the folder.
2. Both names still resolve. Neither project needed touching.
3. A Request from either project starts it at the new path.

**Check**: nothing about `alpha` or `beta` was edited. This is SC-004.

## Step 7 — An agent this machine cannot start

Register an agent on this machine with no catalogue entry — enrol it, then remove the catalogue entry by
hand, which is the state a person reaches by moving a repository and not repointing it.

**Expect**

1. Addressing it in the terminal marks it in the room's participant list.
2. The composer's preview says the message will wait until that repository is opened.
3. Sending is **still allowed**; the report says what will happen to it.
4. The Request is queued, and is collected the moment that repository is opened.

**Check**: the person learned this on the screen they were looking at, not from a log line at startup. This is
SC-006, and it is the half of the original defect that made it invisible.

## Step 8 — The copy

Copy `orders-repo` to a second path on the same machine and try to enrol the copy.

**Expect**: refused, naming **both** paths, because the copy carries the same identifier and the catalogue is
keyed by it. The remedy is a person's decision — re-mint, or point the entry at the right checkout.

**Check**: this is the one thing Parley makes unforgeable, refused by the shape of the file rather than by a
scan somebody has to remember to run.
