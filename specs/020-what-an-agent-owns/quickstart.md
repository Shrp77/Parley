# Quickstart: What an agent owns, asked rather than assumed

A walk, not a tour. It measures the two claims most worth doubting:

1. **An operator obtains a line without composing one.**
2. **No line reaches the registry that a person did not accept.**

The second is the one that matters. The first is the feature; the second is the guarantee, and it is the
one SPEC.md §17 question 10 spent four grounds arguing about.

Everything runs against tools packed and installed into a scratch path, with a root of its own, so the
machine's real deployment is untouched. That discipline exists because 018's walk put thirty-five junk
entries into a live catalogue, and 019's walk was built to be isolated for the same reason.

## Before you start

```powershell
$scratch = Join-Path $env:TEMP "parley-020-walk"
$root    = Join-Path $scratch "root"
$repo    = Join-Path $scratch "orders"
New-Item -ItemType Directory -Force $root, $repo | Out-Null
$env:PARLEY_CONFIG = $root
git -C $repo init | Out-Null
```

**Check the isolation before anything else.**

```powershell
$live   = "$env:APPDATA\Parley\node.json"
$before = if (Test-Path $live) { (Get-FileHash $live -Algorithm MD5).Hash } else { 'absent' }
```

Take it again at the end. If it changed, the walk was not isolated and nothing it reported means
anything. **If it is `absent` both times, say so** — 019's walk recorded that half of its own isolation
check was vacuous on a machine with no `node.json`, and reporting "isolation held" without that
distinction is what the ledger exists to prevent.

---

## Claim 1 — a line without composing one

### Step 1: a project, an enrolled repository, and an agent that answers

In the terminal, which since 019 is the only place a project is named. Press `c`, type `orders`. Then
enrol `$repo` from the agents pane, and start it.

| Check | Expected |
|---|---|
| 1.1 | The enrolment asks for **no ownership line** — the worksheet 019 removed is gone and nothing replaced it in the form |
| 1.2 | The agent registers, and the roster shows it **with no line at all** (FR-031) |
| 1.3 | Nothing on that screen says the deployment is misconfigured. An agent with no line is ordinary |

### Step 2: ask it

Take a seat. In the agents pane, ask what it owns.

| Check | Expected |
|---|---|
| 2.1 | A conversation is opened named `owns-orders-<timestamp>` |
| 2.2 | The Request carries **both halves** and says the line is read by every agent on every roster listing (FR-003, FR-004) |
| 2.3 | The payload carries **no marker** — nothing in it identifies it as an ownership ask |
| 2.4 | Nothing is written anywhere by the ask itself |
| 2.5 | While it has not answered, the surface says **not answered yet**, never *declined* (AC US1.5) |

### Step 3: the answer, beside what it already has

| Check | Expected |
|---|---|
| 3.1 | The answer is shown **attributed to the agent** and distinguishable from text the surface wrote (FR-013) |
| 3.2 | The line it currently holds is shown beside it — here, none (FR-014) |
| 3.3 | The other agents' lines in that project are shown (FR-017) |
| 3.4 | The proposal shown is the **latest** thing the agent said, not the first |

### Step 4: disagree, and settle it

Reply disagreeing, in your own words. The agent revises. Accept the revision.

| Check | Expected |
|---|---|
| 4.1 | The reply goes as an ordinary message in the same conversation (FR-008) |
| 4.2 | A question from either side does not close the exchange (FR-009) |
| 4.3 | What is written is the **agreed** line, not the first proposal (FR-010) |
| 4.4 | `$repo\.mcp.json` now carries it under `PARLEY_OWNS` |
| 4.5 | The surface says it takes effect **when the agent next starts**, and offers the restart (FR-019) |
| 4.6 | Until that restart, the roster marks the line **pending** and says peers still read the old one (FR-020) |
| 4.7 | After the restart, the roster shows the new line and the pending mark is gone |
| 4.8 | The conversation is still readable, and findable from the agent (FR-011) |

### Step 5: the bulk ask states its cost first

Enrol two more repositories. Ask all of them.

| Check | Expected |
|---|---|
| 5.1 | The count and *each costs that agent a turn* are shown **before anything is sent** (FR-007) |
| 5.2 | One conversation per agent, one exchange each — no one-to-many operation (FR-006) |
| 5.3 | **Every ask is sent before anything is waited on** — checked by ordering, not by timing, as 0.25.1's test is |
| 5.4 | An agent that cannot be asked is named and the rest proceed |
| 5.5 | Answers can be acted on as they arrive, without waiting for the slowest |

---

## Claim 2 — no line reaches the registry that a person did not accept

**This is the claim question 10 is about, and every check here is a probe rather than a reading.**

### Step 6: the agent cannot set its own line

| Check | Expected |
|---|---|
| 6.1 | No tool in `parley-mcp`'s list sets, proposes or alters `owns` for any agent |
| 6.2 | The `Agents` gRPC service has **no method** that writes it |
| 6.3 | `Operators.AcceptOwnership` exists on `Operators` and nowhere else |
| 6.4 | An agent's own answer, left unaccepted, leaves `.mcp.json` and the registry unchanged — walked by asking, reading the answer, and closing the surface |

### Step 7: acceptance is the only writer, and it is a person's act

| Check | Expected |
|---|---|
| 7.1 | Declining writes nothing and leaves the existing line exactly as it was (FR-016) |
| 7.2 | A line edited before accepting is recorded as `edited`, and `proposed_length` is **the agent's** message length, not the edited one (FR-015, `research.md` R4) |
| 7.3 | A line composed with no ask at all is recorded as `composed`, with no conversation and no proposed length |
| 7.4 | `agents.owns` is written by **nothing** but an attach. Probed by accepting a line and confirming the registry does not change until the agent restarts |

### Step 8: the character rule did not move when the number did

| Check | Expected |
|---|---|
| 8.1 | A 400-character statement is accepted — the bound is off (FR-023) |
| 8.2 | A statement carrying U+202E is **refused**, naming the position (FR-022) |
| 8.3 | So is U+2028, U+00AD, U+FEFF and a tag character from U+E0020–U+E007F |
| 8.4 | So is an embedded NUL |
| 8.5 | Each refusal happens **where the operator can see it and act**, and nothing is silently shortened (FR-025) |
| 8.6 | With a cap reinstated at 300, a 412-character statement is refused **naming 300** — never the sentinel |

> 8.2 to 8.4 are the clause the migration had to reproduce verbatim. `OwnershipTests` re-runs the whole
> Unicode sweep that derived it, and this walk checks that the rebuilt table still refuses what the old
> one did — which is the one thing a migration of this shape could quietly lose.

### Step 9: the machine check, on both surfaces

| Check | Expected |
|---|---|
| 9.1 | Accepting for an agent whose machine is not this one is **refused**, naming the machine and what to do there |
| 9.2 | The same refusal, in the same words, from the web UI |
| 9.3 | A launch configuration naming a different agent under that key is refused, and nothing is written (005's finding) |
| 9.4 | A write that fails leaves the file **unchanged**, not partly written (FR-021) |

---

## Step 10: the schema, which is no longer a migration

**This step was written against a migration that does not exist, and it is rewritten rather than
deleted — what it demanded is still worth checking, but the thing it demanded it of has gone.**

It asked for `012_ownership_is_asked.sql` to be walked on a store that had carried traffic, because 006
records that rebuilding `agents` succeeds on every empty fixture and fails on every store that has ever
been used. The owner's decision of 2026-09-26 removed the question: *"don't worry about migration. Go
ahead and make the changes and call it schema #1. Nothing we have is worth keeping right now."*
Migrations 002 to 011 were folded into `001_initial.sql` and deleted, so **the schema is 1 and there is
nothing to upgrade from.**

That is available exactly once and has now been used. From the first release every schema change is a
migration again, which is what `MigrationDiscoveryTests` moving off 1 exists to notice.

```powershell
# A fresh store, because there is no earlier shape to carry forward.
```

| Check | Expected |
|---|---|
| 10.1 | A new store opens and `user_version` is **1** |
| 10.2 | `PRAGMA foreign_key_check` returns **no rows** |
| 10.3 | `PRAGMA integrity_check` returns `ok` |
| 10.4 | All **fourteen** triggers are present, including the two on tables other than `agents` |
| 10.5 | `role` and `machine` still generate correctly from `name` |
| 10.6 | A 400-character statement is accepted, and every character in 8.2–8.4 is still refused |
| 10.7 | `agent_ownership_accepted` exists, and `conversation_id` is a real foreign key — a row naming a conversation that does not exist is refused |

**What cannot be checked here, and is recorded instead:** that the collapsed file matches what eleven
migrations produced. It was verified once, object by object, against a store built the old way — 41
shared objects with identical names and creation order — and **the comparand has been deleted**, so no
test can hold it. `001_initial.sql` says so in its own header rather than claiming a guard that does not
exist.

**An existing store cannot be opened by this build, and that is the cost of the decision above.**
`SchemaMigrator` refuses a store whose `user_version` is greater than the latest migration it knows, so
a store at 11 meets *"This store is at schema 11 and this Parley only knows 1"* — advice that then tells
somebody to upgrade Parley, which is the opposite of what happened. The data loss was accepted; the
misleading sentence is a defect and is recorded in `REVIEW-LEDGER.md`.

---

## Step 11: isolation held

```powershell
(if (Test-Path $live) { (Get-FileHash $live -Algorithm MD5).Hash } else { 'absent' }) -eq $before
```

**Must be true**, and say whether it was a real comparison or two `absent`s.

---

## What this walk cannot check

Recorded rather than claimed:

- **A real model's answer.** Every step above can be walked with a scripted agent, and what a scripted
  agent cannot tell you is whether the ask produces a *useful* line. That is the whole of the feature's
  value and it is not measurable here. It is measurable by asking this repository's own agent and
  recording the answer, which SPEC.md §17 question 10 is the place for.
- **Anchoring.** The residual risk R1 and the spec both name — that a fluent, reasoned proposal anchors
  a person harder than a quoted README line — cannot be measured by a walk at all. It is why FR-013's
  attribution and FR-017's neighbours exist, and neither is a measurement of it.
- **macOS.** As in 019: bash 3.2 and `launchctl`, untested on this branch.
- **The web UI's script half.** This project has no browser, which 015 recorded about its own follow
  script.
