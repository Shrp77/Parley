# Research: An agent is an identity, not a name

Eight decisions. Each records what was rejected, because the rejected option is the part that cannot be
recovered by reading the result.

---

## R1 — The identifier is a GUID, and it is the catalogue's key

**Decision.** A GUID, written lowercase and hyphenated. It is the key of the machine's catalogue and is
otherwise opaque: nothing parses it, sorts by it, or derives anything from it.

**Rationale.** The owner's requirement is "an ID that is not going to collide across boundaries", and the
boundaries here are machines, projects and repositories. A GUID needs no authority to allocate and no registry
to deduplicate, which matters because enrolment happens on a machine with no guarantee of reaching anything
else.

**Alternatives considered.**

- **Key the catalogue by workspace path** (012's D2). This also fixes today's defect, and costs no new concept.
  It was rejected because a path is not stable: moving a checkout mints a new agent under D2, and the thing
  the owner asked for is an identity that survives exactly that. It is also the wrong key for what comes
  next — 012's D4 wants a consent record in the store, and a record keyed by a path is one that a move
  invalidates.
- **A hash of something** — the repository's origin URL, the first commit, the folder name. Every one of them
  is derivation, which FR-002 forbids, and each has a case where two distinct agents hash alike (a fork, a
  template repository, two clones).
- **A short human-readable identifier.** Rejected because anything short enough to read is short enough to
  collide, and anything a person reads is a thing a person will start typing — which is the name's job.

---

## R2 — The repository carries it, and that makes it repository-scoped

**Decision.** Enrolment writes the identifier into the repository's own launch configuration, beside the
per-project entries that already carry the name it answers to in each project.

**What follows, stated because it is not obvious.** A launch configuration is frequently committed, so the
identifier travels to every machine that checks the repository out. **That is correct rather than a
collision**: the identifier identifies *this repository as an agent*, and what distinguishes the running
instances is the machine half of the agent's name, which it has always distinguished. Each machine's catalogue
holds its own entry, keyed by the same identifier, pointing at its own path.

**The case it forbids, which is the point.** One machine cannot catalogue two checkouts of one repository,
because they carry one identifier and the catalogue is keyed by it. That is the copied-repository case the
enrolment help already calls out as the one thing Parley makes unforgeable, and refusing it structurally is
better than refusing it by a check. A person who genuinely wants two checkouts as two agents re-mints: that is
a deliberate act, which is what it should be.

**Alternatives considered.**

- **Hold the identifier only in the catalogue**, machine-side. Then a moved checkout cannot be recognised as
  the same agent — re-cataloguing it mints a new one — which gives up the property R1 exists for.
- **Hold it in a file of its own** in the repository. Rejected: a second file to keep in step with the first,
  and the launch configuration is already the thing that says what this repository is to Parley.

---

## R3 — The node learns names from each catalogued workspace, not from the catalogue

**Decision.** The catalogue records only what is needed to start: identifier → command, workspace. For each
entry, the node reads that workspace's launch configuration to learn which projects it answers in and what it
is called in each. It announces, to each broker it joins, the names claimed for that broker's project.

**Rationale.** This is 012's D3 applied: *"the repository declares which projects it answers in and what it is
called in each. The catalogue must not also carry membership, or the drift this feature exists to remove is
rebuilt one layer down."* It is also what makes a moved repository self-describing — the names travel in the
folder.

**Cost, stated.** A lookup gains a hop, and the node reads N small files at join and whenever the catalogue
changes. N is the number of catalogued checkouts on one machine: tens. 012's own costs section predicted this
and called it cheap.

**Alternatives considered.**

- **Record membership in the catalogue too.** Rejected by D3's argument: two places to say one thing is the
  drift this removes.
- **Ask the broker.** The broker knows which agents are registered on this machine, which is what it already
  sends at join — but that is a list of names with no way back to a checkout, which is precisely the gap that
  produced the defect.

---

## R4 — Two workspaces claiming one name refuses the project, and names both paths

**Decision.** When two catalogued workspaces claim the same name for one project, the node announces nothing
for that project and reports both paths.

**Rationale.** An agent's name is the one thing Parley makes unforgeable, and two checkouts answering to it in
one project is either a copied repository or an enrolment mistake. Starting *either* would be guessing at
identity, which Principle III exists to prevent. Refusing the project rather than the pair is deliberate: a
node that announced the rest would leave a person addressing a name that resolves to nothing, with no reason
given.

**Alternatives considered.**

- **First one wins.** Rejected: the winner depends on the order a directory was read, which is a different
  agent on a different day.
- **Refuse the machine.** Rejected as disproportionate — one project's ambiguity should not take down the
  others, and the report names the paths either way.

---

## R5 — The resident table is passed in, not made static

**Decision.** The table of running agents is created once by whatever hosts the nodes and handed to each, keyed
by identifier. It is not a static field.

**Rationale.** Serving several projects in one process is what makes the sharing both necessary and possible:
today each node has its own table as a local, so two projects asking for one checkout start two processes. A
static would work and is refused on this repository's own grounds — test classes run in parallel, and a
process-wide singleton is the thing `Shell.Opener` was made an instance property to avoid.

**What it does not close.** Two *processes* each hosting nodes could still start one checkout twice. That is
narrower than it sounds: a machine already holds one node per broker, so the second process would have to
serve a different set of projects. It is recorded rather than fixed, because closing it needs a lock on disk
and 012's step 2 removes the case by having one node.

---

## R6 — The manifest keeps broker coordinates and loses the catalogue

**Decision.** A project entry keeps its name, store and endpoint, and loses its catalogue path. The machine's
catalogue is found once, beside the manifest.

**Rationale.** A per-project binding for a machine-scoped file is a way to say the same thing several times
and eventually differently. 012 predicted this line: *"the per-project catalogue binding in `projects.json`,
which shrinks to broker coordinates."*

**Cost.** `serve`'s per-project node resolution simplifies, and the refusal for "one explicit catalogue offered
to several projects" is deleted along with the reason for it. `--node-config` and `PARLEY_NODE_CONFIG` keep
meaning "the catalogue", now unambiguously machine-wide.

---

## R7 — Enrolment mints once and never gates

**Decision.** Enrolment mints an identifier if the repository has none, writes one catalogue entry keyed by
it, and adds the per-project name to the repository. Enrolling the same checkout into a further project adds a
name and nothing else.

**What this deletes.** The cross-project catalogue scan, its refusal, and the rule that a second project gets
no catalogue line. All three existed to prevent one workspace being catalogued twice for unattended start;
keyed by identifier, twice is not expressible.

**The machine-wide lock stays, and this decision was corrected while building.** An earlier draft of R7 said
it went too, quoting 012: *"a lock exists to serialise writers that do not know about each other"*. That
argument belongs to 012's **D6**, which makes the node the catalogue's sole long-lived writer — and D6 is
explicitly out of scope here. Until it lands, enrolment is still a writer that appears, writes and vanishes,
and two of them running at once would still race on one file. What the scan protected is gone; what the lock
protects is not.

**Alternatives considered.** Deleting the lock now and adding it back with D6 was rejected: a lost catalogue
entry is silent, and the window is exactly the moment somebody is setting a machine up.

---

## R8 — What a person is told, and where

**Decision.** An agent registered on this machine with no catalogue entry is marked in the room's participant
list, and the composer's preview says the message will wait until that repository is opened. The message is
still accepted and queued; the report says what became of it.

**Rationale.** This is the half of the defect that made it invisible. The broker has always known — it prints
it at every startup — and a log is not where somebody addressing an agent is looking. The rule this surface
already keeps is that nothing is hidden without being said, and 017 added a preview whose whole argument is
that what Enter will do is read before it is pressed.

**Alternatives considered.** Refusing the send was rejected outright: a queued Request is collected the moment
that repository is opened, so refusing would throw away a message that was going to be delivered.
