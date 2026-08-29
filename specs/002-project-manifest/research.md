# Research: Machine-level project manifest

**Feature**: 002-project-manifest | **Date**: 2026-08-28

Every unknown carried by the specification, resolved. Where a claim could be checked on this
machine it was checked rather than reasoned about, per the constitution's *probe, do not reason*.

---

## R1. Where the manifest lives

**Decision**: `Environment.SpecialFolder.ApplicationData` + `Parley/projects.json`, overridable by
`--config` and by `PARLEY_CONFIG`.

**Rationale**: One runtime call gives a per-user configuration directory on all three platforms
with no per-platform branch, which is the only way this stays correct on a machine nobody tests
on. Verified on Windows: `ApplicationData` resolves to `C:\Users\<user>\AppData\Roaming`, so the
manifest lands at `…\AppData\Roaming\Parley\projects.json`.

**Alternatives considered**:

- `LocalApplicationData` (`AppData\Local`) — excluded from roaming profiles. A manifest describes
  paths on *this* machine, so roaming it is arguably wrong; but the paths inside are already
  machine-specific and a roamed manifest naming a store that does not exist is a clearer failure
  than a manifest that silently is not there. Weak either way; `ApplicationData` chosen because
  it is the conventional answer for user configuration and the difference is not load-bearing.
- `~/.parley/` on every platform — one path to document, but wrong on Windows, where dotfiles in
  the profile root are a Unix habit rather than a convention.
- Beside the tool on `PATH` — machine-wide rather than per-user, and needs elevation to write.

**Open on non-Windows**: .NET's mapping of `ApplicationData` on Linux and macOS was **not
verified here** — this machine is Windows only. Linux is expected to honour `XDG_CONFIG_HOME`
falling back to `~/.config`; macOS is expected to behave the same way rather than using
`~/Library/Application Support`, which is the surprising part and the reason it is written down.
**A test must assert the resolved path is under the user's profile and is absolute, and CI runs
on all three platforms**, so the assumption is checked where it matters rather than here.

**Resolved on Linux, and it found a defect.** GitHub Actions was unavailable when this landed — a
billing block, not a failing build — so the suite was run against the .NET 10 SDK image in a
container instead. `Environment.GetFolderPath(ApplicationData)` returned **the empty string**
there, which `Path.Combine` turned into the *relative* `Parley/projects.json`: a manifest resolved
against whatever directory the command was typed in, which is the exact ambiguity this feature
exists to remove, reintroduced above everything it fixes. `GetFolderPath` is documented to do this
when it cannot locate the folder, and nothing on Windows can reach it. The one line that said so
was the "is absolute" assertion this section asked for. `DefaultPathFrom` now falls back to the
home directory and refuses when there is no home either.

**macOS is still unverified.** No macOS machine was available and none can be simulated, so the
expectation above stands as an expectation. The code takes it from the runtime rather than a
hardcoded path, so a different mapping there is correct rather than broken — but the assertion
has not been run, and this note should be closed by the first green macOS CI leg rather than
assumed closed by this one.

---

## R2. File format: JSON, not YAML

**Decision**: JSON. Amend SPEC.md §4.1's `parley.yaml` illustration in the same change.

**Rationale**: Every file in this system is already JSON — the node catalogue, the launch
configuration, the enrollment manifest — and the runtime parses JSON with no package. YAML would
add a dependency, and `Directory.Packages.props` requires every transitive pin to carry a written
justification and a removal condition; adding a parser for one configuration file does not earn
that. The constitution's *specification is the product* principle makes the amendment mandatory
rather than optional: leaving §4.1 describing a file the implementation will not produce is
exactly the code-and-spec disagreement it forbids.

**Alternatives considered**: YAML for comments, since a hand-edited configuration benefits from
them. Rejected: FR-025 makes the tool the author, so hand-editing is the fallback rather than the
path — and the enrollment manifest already solved the same problem with a `_readme` array of
strings, which is honest JSON and needs no parser.

---

## R3. Hosting several brokers in one process

**Decision**: Feasible with no restructuring. Start one `ParleyServer` per configured project.

**Rationale**: Checked against the code rather than assumed. `ParleyServer.StartAsync` builds its
own `WebApplication`, takes its own `BrokerRuntime` and its own listen address, and returns an
instance; nothing about it is static or singleton. Several can coexist, each with its own Kestrel
host, its own connection and its own store. This matches SPEC.md §4.1 — *"A single service
constructs one broker per configured project, each with its own connection and endpoint, and the
isolation is unaffected — it lives in the storage layer, not the process boundary."*

**Risk found while checking, and it is not hypothetical**: starting a `WebApplication` installs
the generic host's console lifetime, which registers a `Console.CancelKeyPress` handler and sets
`Cancel = true`. That is the exact mechanism behind the Ctrl+C defect fixed in the preceding
change — `serve` waited on a token nobody passed while the host swallowed the signal. Starting
*N* applications registers *N* such handlers. The interrupt must therefore be owned once, above
all of them, and shutdown must stop every broker started (FR-021) rather than relying on any
host's own lifetime. **A test must start two brokers and confirm one interrupt stops both.**

**Alternatives considered**: one process per project, supervised separately. Rejected because
SPEC.md §4.1 already argues it — systemd template units make it nearly free while Windows needs a
separate service registration per instance, so a single service is the configuration with
cross-platform parity. It also multiplies the autostart entries this repository added specifically
to stop *"nothing works until somebody runs `parley serve`"* being a thing to remember.

---

## R4. Partial start, and what an exit code means

**Decision**: A project that fails to start is reported and named; the others continue; the
process reports that it is serving a subset and does not exit zero as though all were up.

**Rationale**: §4.1 requires containment — *"A failing project must be contained: a corrupt
database or a failed migration stops that broker and is logged, while the others keep running."*
What it does not settle is what the process tells its supervisor, and that is the part an operator
cannot see afterwards. This repository has already been bitten twice by a run that reported
success while doing less than it claimed: an unreadable node catalogue treated as empty, and ten
repositories written to a catalogue nothing reads. The same shape must not be reintroduced at the
process level.

**Alternatives considered**: fail the whole process if any project fails. Rejected — it makes one
corrupt store take down two healthy deployments, which is the coupling §4.1 exists to prevent.

---

## R5. Precedence, and how an agent resolves a project

**Decision**: explicit argument → environment variable → named project → previous default. An
agent's launch configuration may carry `PARLEY_PROJECT`, an explicit `PARLEY_BROKER`, or both;
the explicit endpoint wins.

**Rationale**: Additive by construction, so FR-010 and FR-018 hold without a migration: a machine
with no manifest resolves exactly as it does today, because the manifest is only consulted at the
step where the previous default would have applied.

**The failure this must avoid is specific.** A repository carrying `PARLEY_PROJECT` is committed
and cloned; a teammate's machine has no such project. Falling back to the local default endpoint
would connect that agent to *a different deployment* — plausibly a working one, silently wrong,
and undetectable from either side of a message. FR-023 therefore requires a refusal at launch
naming the project and the manifest consulted. This is the same argument the enrollment work
made about a wrong-typed configuration node: absent is a state, and a state that is not absent
must not be treated as one.

**Identity is untouched** (constitution III). The project supplies *where to connect*, never
*who is connecting*: `PARLEY_AGENT` and `PARLEY_OWNS` still come from the launch environment,
and no operation accepts either. Resolution happens in the adapter before the channel is opened,
so nothing about a project crosses the wire and FR-015 holds unchanged — the broker cannot tell
how an agent was configured to find it, and must not be able to.

**Alternatives considered**: project-only, no endpoint in `.mcp.json`. Rejected by the operator as
Q2; it would also make a committed launch configuration unresolvable on any machine without a
matching manifest, which is most of them.

---

## R6. Managing the manifest, and what removal means

**Decision**: `add`, `remove`, `list`. Removal takes out the entry and deletes nothing.

**Rationale**: The feature exists to stop operators maintaining paths by hand; a manifest that
must itself be hand-created in a location they have to look up keeps most of the problem. Writing
it through the tool also lets FR-026 validate the whole file before it is written — duplicate
endpoint, duplicate store — which is the class of error that otherwise surfaces as a broker that
will not start.

**Removal is withdrawal, not destruction**, mirroring the distinction the operator commands
already draw between `retire` and `purge`: one withdraws a registration, the other destroys. A
configuration command that deleted a store as a side effect would be the most destructive
operation in the tool wearing the least alarming name. FR-028 requires it to say what it left
behind and where, so the operator can delete deliberately if that is what they meant.

**Alternatives considered**: hand-authored only, and `add`/`list` with removal by hand. The first
was rejected above; the second is asymmetric for no gain once removal is non-destructive.

---

## R7. What this must not become

Recorded because the constitution requires a contradiction to be argued rather than assumed, and
none of these is contradicted — the note exists so a later change cannot drift into one without
noticing.

- **The protocol gains no notion of a project** (FR-015). No message field, no schema column, no
  scoped query. Membership stays "which endpoint an agent connected to", enforced by a connection
  that cannot see another file.
- **Agent names still collide freely across projects** (FR-016). Two projects may each have an
  `orders`. A manifest that made names unique across projects would reintroduce the global naming
  convention §1 exists to prevent.
- **Nothing new crosses between agents**, so the terseness control (constitution IV) is not
  engaged: the manifest is read on one machine by one operator's tools and never returned by an
  operation.
- **Credentials stay references** (constitution). The manifest holds paths and endpoints only,
  which is what keeps it safe to write to a well-known location.
