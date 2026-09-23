# Quickstart: One place, and one way in

A walk, not a tour. It measures the two claims most worth doubting:

1. **One name brings a project up.** No path, no port, at any point.
2. **An existing deployment moves without losing a conversation.** Counted before and after, not eyeballed.

Everything runs against tools packed and installed into a scratch path, with a root of its own, so the
machine's real deployment is untouched. That is the same discipline 015 and 016 were walked under, and it
exists because a walk that writes into a live catalogue is how 018 put thirty-five junk entries into one.

## Before you start

```powershell
$scratch = Join-Path $env:TEMP "parley-019-walk"
$root    = Join-Path $scratch "root"
New-Item -ItemType Directory -Force $root | Out-Null
$env:PARLEY_CONFIG = $root
```

**Check the isolation before anything else, because every later check depends on it.**

```powershell
$live = "$env:APPDATA\Parley\node.json"
$before = (Get-FileHash $live -Algorithm MD5).Hash
```

Take it again at the end. If it changed, the walk was not isolated and nothing it reported means anything.

---

## Claim 1 — one name brings a project up

### Step 1: name a project, and type nothing else

```
parley project add orders
```

| Check | Expected |
|---|---|
| 1.1 | Exits 0 |
| 1.2 | Reports an endpoint it chose |
| 1.3 | Reports a store location it chose |
| 1.4 | **Nothing in the command line is a path or a port** |
| 1.5 | `<root>/projects/orders/project.json` exists |
| 1.6 | It contains an `endpoint` and **no `name`, no `store`, no `node_config`** |
| 1.7 | `<root>/ports.json` exists and names `orders` |
| 1.8 | The port is inside the band, and **outside** 32768–60999 and 49152–65535 |

### Step 2: it serves

```
parley serve --with-node
```

| Check | Expected |
|---|---|
| 2.1 | Serves `orders` at the endpoint step 1 reported |
| 2.2 | `<root>/projects/orders/parley.db` now exists |
| 2.3 | `parley project list` names it |
| 2.4 | **No option exists that would serve fewer projects** — `--db` and `--listen` are refused |

### Step 3: a second project, while the first is running

Leave the broker up. In another shell:

```
parley project add billing
```

| Check | Expected |
|---|---|
| 3.1 | Its endpoint differs from `orders`' |
| 3.2 | `ports.json` names both |
| 3.3 | **Within ten seconds**, and with no restart, the running broker serves `billing` |
| 3.4 | Anything attached to `orders` never disconnected |

### Step 4: two at once — the claim, not the probe

This is the check the whole port design exists for, so run it enough times that a one-in-many failure
would show. Twenty pairs:

```powershell
1..20 | ForEach-Object {
  $a = Start-Job { parley project add "race-$using:_-a" }
  $b = Start-Job { parley project add "race-$using:_-b" }
  Wait-Job $a, $b | Out-Null
  Receive-Job $a, $b
}
```

| Check | Expected |
|---|---|
| 4.1 | **No two projects anywhere hold the same endpoint** |
| 4.2 | Every pair either both succeeded with different ports, or one refused **and created no folder** |
| 4.3 | No refusal names a file-in-use error; contention is reported as contention |

### Step 5: the record is bookkeeping, not authority

```powershell
Remove-Item "$root\ports.json"
```

| Check | Expected |
|---|---|
| 5.1 | The running broker keeps serving everything |
| 5.2 | Every project keeps its endpoint |
| 5.3 | `parley project add rebuilt` succeeds, and its port collides with nothing |
| 5.4 | `ports.json` is back, naming every project |

**This is SC-009 and it is the one most likely to be got wrong.** A file that can hold a deployment
hostage is a file that should not exist.

---

## Claim 2 — an existing deployment moves without losing a conversation

### Step 6: build a deployment in the old shape

The walk must move a *real* one, so build it the way the previous version did — a `projects.json` naming
stores outside the root — and put traffic in it. Two agents, a conversation, an answered request, and a
placed file.

**Count what is there before, and write the numbers down:**

| Counted | How |
|---|---|
| conversations | `parley open`, plus the archived ones |
| messages | export each conversation and count |
| registered agents | `parley registry` |
| shared files | `parley files list --full` |

### Step 7: move it

```
parley project migrate
```

| Check | Expected |
|---|---|
| 7.1 | **Before anything changes**, it says it must stop the broker and what that costs |
| 7.2 | It does not proceed without acceptance |
| 7.3 | With a broker still running it refuses, **naming the process that holds the store** |
| 7.4 | After accepting: every project's files are under its own folder |
| 7.5 | `.lock` and `.owner` at the old path are gone; the new ones are fresh |
| 7.6 | `.files` and `.files-incoming` moved as directories, with their contents |
| 7.7 | A project already in the right place is reported as already correct |
| 7.8 | A project whose port is inside the ephemeral range is **reported**, and **not renumbered** |

### Step 8: nothing was lost

Re-count all four. **Every number must match step 6 exactly.**

| Check | Expected |
|---|---|
| 8.1 | Same conversation count |
| 8.2 | Same message count, and the text of one message compared verbatim |
| 8.3 | Same registered agents |
| 8.4 | Same shared files, and one fetched and its hash compared |

### Step 9: interrupt it and run it again

Restore the step-6 deployment and kill the move part way — after a copy, before the configuration is
rewritten.

| Check | Expected |
|---|---|
| 9.1 | The deployment still starts |
| 9.2 | The configuration names exactly one copy, and that copy is intact |
| 9.3 | Re-running finishes the job |
| 9.4 | Re-running overwrites nothing at a destination |
| 9.5 | Counts after the resumed move match step 6 |

---

## Step 10: the old spelling still starts

The one that protects a machine nobody has reinstalled.

```
parley serve --config <root>\projects.json
```

…where that file does not exist, because 019 deleted it.

| Check | Expected |
|---|---|
| 10.1 | It resolves to `<root>` and serves every project |
| 10.2 | It is **not** reported as a missing file |
| 10.3 | `parley autostart status` on an old-form entry says so, and names what to run |

---

## Step 11: the removed verbs

| Check | Expected |
|---|---|
| 11.1 | Each of the sixteen exits **64** |
| 11.2 | Each names the surface that does that thing |
| 11.3 | **None prints the string `parley <verb>` for its own removed verb** — the drift test reads `Program.cs` by regex |
| 11.4 | `install.ps1` and `install.sh` run end to end |
| 11.5 | The plugin hook still reports counts in an enrolled repository |
| 11.6 | `/parley:files-list` still works |

---

## Step 12: isolation held

```powershell
(Get-FileHash $live -Algorithm MD5).Hash -eq $before
```

**Must be true.** If it is not, the walk wrote into the real deployment and every check above is void —
which is exactly what happened in 018 and is why this step exists.

---

## What this walk cannot check

Recorded rather than claimed, the way 016's walk recorded that step 1 could not be driven:

- **The logon entry itself.** Registering a scheduled task writes to the machine, not to the scratch root.
  Steps 10.3 and 11.4 check what the entry *would* say; whether a real logon serves the scratch root is
  not driven here.
- **Linux and macOS.** The port hold is an exclusive `flock` there and a sharing refusal on Windows, and
  step 4 exercises only the second. The other two need the walk run on those platforms, or CI — which this
  branch has never reached.
