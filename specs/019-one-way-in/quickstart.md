# Quickstart: One place, and one way in

A walk, not a tour. It measures the claim most worth doubting:

**One name brings a project up.** No path, no port, at any point.

Everything runs against tools packed and installed into a scratch path, with a root of its own, so the
machine's real deployment is untouched. That is the same discipline 015 and 016 were walked under, and it
exists because a walk that writes into a live catalogue is how 018 put thirty-five junk entries into one.

## What this document used to say, and why it does not any more

**Rewritten on 2026-09-26, against the feature as delivered rather than as planned.** Three things it
walked were withdrawn while 019 was being built, and walking them would have measured nothing:

- **`parley project add` was removed** on 2026-09-25, with `project set` and `project remove`, on the
  owner's call: *"if we're doing everything in the terminal app, then we DO everything in the terminal
  app."* Steps 1, 3 and 5.3 typed it. **A project is created in the terminal now, and only there.**
- **Claim 2 — an existing deployment moves without losing a conversation — is gone**, with steps 6 to 9.
  US6, US7 and FR-062 to FR-064 were withdrawn because nothing has been released: there is nothing to
  move and nothing to strand. `reset.ps1` and `reset.sh` clear a developer's pre-019 layout instead.
- **Step 10 asserted that the old `projects.json` spelling still resolves.** FR-062 went on 2026-09-23
  and the old spelling is now *refused by name*, pointing at the directory to use and at the reset
  scripts. The step is kept and inverted, because the behaviour still needs checking — it is the
  opposite behaviour.

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

**In the terminal, which is the only place this happens now.**

```
parley
```

Press `c`. Type `orders`. Leave the description blank or fill it in; nothing else is asked for.

| Check | Expected |
|---|---|
| 1.1 | The form has **two fields** — a name and an optional description — and no others |
| 1.2 | A capital is corrected as it is typed rather than refused afterwards |
| 1.3 | The message line reports the project was named, and that **nothing starts it yet** |
| 1.4 | **Nothing typed anywhere is a path or a port** |
| 1.5 | `<root>/projects/orders/project.json` exists |
| 1.6 | It contains an `endpoint` and **no `name`, no `store`, no `node_config`** |
| 1.7 | `<root>/ports.json` exists and names `orders` |
| 1.8 | The port is inside 19100–19355, and **outside** 32768–60999 and 49152–65535 |

> **This step cannot be driven from a script, and that is a consequence rather than an oversight.**
> `parley terminal` refuses a redirected stream by design, so a walk run by a program cannot press `c`.
> The same properties are driven by
> `Parley.Terminal.Tests.NamingTests.A_new_project_is_checked_as_it_is_typed_and_needs_nothing_but_a_name`,
> which fills the form, finishes it, and asserts the project reads back with a store derived from the
> name and a port inside the band. 016's walk recorded the same kind of limit about its own step 1;
> this is that, from the other side.

### Step 2: it serves

```
parley serve --with-node
```

| Check | Expected |
|---|---|
| 2.1 | Serves `orders` at the endpoint step 1 reported |
| 2.2 | `<root>/projects/orders/parley.db` now exists |
| 2.3 | `parley project list` names it, says whether it **answered**, and says separately that **only this machine can address it** |
| 2.4 | **No option exists that would serve fewer projects** — `--db` and `--listen` are refused by name |
| 2.5 | With no project openable at all, it **stays up and says why**, because it is watching (FR-033) |

### Step 3: a second project, while the first is running

Leave the broker up. In another terminal, press `c` again and name `billing`.

| Check | Expected |
|---|---|
| 3.1 | Its endpoint differs from `orders`' |
| 3.2 | `ports.json` names both |
| 3.3 | **Within ten seconds**, and with no restart, the running broker serves `billing` |
| 3.4 | Anything attached to `orders` never disconnected |

> 3.3 and 3.4 are also driven, and 3.4 is the half a restart would silently satisfy on the surface:
> `ManifestWatchTests.A_project_named_while_serving_is_started_within_seconds` and
> `AttachmentsSurviveAChangeTests.An_agent_attached_to_one_project_survives_another_being_added`, which
> holds an attach stream open across the change because a stream is what a restart cannot preserve.

### Step 4: two at once — the claim, not the probe

This is the check the whole port design exists for. **It can no longer be run from a shell**, because
the command that raced is gone; two people naming projects in two terminals is the real case and is not
scriptable.

It is driven instead, and the driven version is stronger than the walk was: `PortLedgerTests` claims
concurrently under the real hold, and the design was **mutation-checked** — with the hold opened
`FileShare.ReadWrite` instead of `None`, twenty-four concurrent claimants were handed 19100 nineteen
times.

| Check | Expected |
|---|---|
| 4.1 | **No two projects anywhere hold the same endpoint** |
| 4.2 | A claim that cannot be made **creates no folder** |
| 4.3 | Contention is reported as contention, not as a file-in-use error |

### Step 5: the record is bookkeeping, not authority

```powershell
Remove-Item "$root\ports.json"
```

| Check | Expected |
|---|---|
| 5.1 | The running broker keeps serving everything |
| 5.2 | Every project keeps its endpoint |
| 5.3 | Naming a third project in the terminal succeeds, and its port collides with nothing |
| 5.4 | `ports.json` is back, naming every project |

**This is SC-009 and it is the one most likely to be got wrong.** A file that can hold a deployment
hostage is a file that should not exist. The reconciliation that makes it true —
every endpoint found in a `project.json` is spoken for whether or not the record lists it — is driven by
`PortLedgerTests.A_port_a_project_already_serves_on_is_spoken_for_even_when_the_record_forgot_it`.

---

## Step 10: the old spelling is refused, by name

The one that protects a machine nobody has reinstalled — **inverted.** It used to resolve; FR-062 went
on 2026-09-23, and a machine with an old entry is now told rather than quietly carried.

```
parley serve --config <root>\projects.json
```

| Check | Expected |
|---|---|
| 10.1 | It is **refused**, naming `projects.json` as the old spelling |
| 10.2 | The refusal names the **directory** to point at instead |
| 10.3 | It names the reset script, so a developer with a pre-019 layout has somewhere to go |
| 10.4 | It is **not** reported as a missing file, which would send somebody to create one |

---

## Step 11: the removed verbs

| Check | Expected |
|---|---|
| 11.1 | Each of the sixteen exits **64** |
| 11.2 | Each names the surface that does that thing |
| 11.3 | **None prints the string `parley <verb>` for its own removed verb** — the drift test reads `Program.cs` by regex |
| 11.4 | `parley project add`, `set` and `remove` are refused the same way, and `project list` still works |
| 11.5 | `install.ps1` and `install.sh` run end to end |
| 11.6 | The plugin hook still reports counts in an enrolled repository |
| 11.7 | `/parley:files-list` still works |

11.5 is confirmed rather than walked here: Windows by a real install against a live broker, Linux by
`confirm-installers.sh` beside this file. **macOS is covered by neither**, and `EnabledAsync`'s
launch-agent branch is unrun code.

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

- **Creating a project, from a script.** Step 1 and step 5.3 need a person at a terminal. The properties
  are driven; the walk is not the thing that measures them any more, and pretending otherwise by
  hand-writing a `project.json` would measure the reader rather than the claim.
- **The logon entry itself.** Registering a scheduled task writes to the machine, not to the scratch
  root. Step 11.5 checks what the entry *would* say; whether a real logon serves the scratch root is not
  driven here.
- **macOS.** bash 3.2 and `launchctl`, neither of which this branch has been run against. Linux is
  covered by a container; Windows by real installs. CI would cover the third and this branch has never
  reached it.
