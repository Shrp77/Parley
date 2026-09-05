# Research — 006

Phase 0. Each entry resolves something `spec.md` left open or assumed, from the protocol and the
code rather than from judgement. Where the finding contradicts the specification, that is said
outright and `spec.md` is amended rather than quietly worked around.

---

## R1 — An added agent can be startable immediately, and §12.3 is not bent to do it

**The question.** FR-011 requires that adding a repository as an agent leaves it startable with no
further human act. 005's planning rejected exactly this and amended its own User Story 3 to promise
less, on the grounds that "the registry holds only agents that have registered themselves (§12.3),
and a surface registering on a repository's behalf would be a caller-supplied identity."

**That reasoning is sound and it does not settle the question, because it answers a different one.**
It rules out a *surface* creating a registration. It says nothing about starting the agent so that
the agent creates its own.

**What §12.3 actually says.** Reading it to the end rather than to the sentence that was quoted:

> It does not replace the manifest, and cannot. A self-registered agent exists from its first launch
> onwards, which means it is addressable only once somebody has opened that repository at least
> once … That is sufficient for Full and Pull-only tiers … **nor for spawn-on-demand, where the
> whole point is to reach an agent whose session is not open. Those need the manifest of §12.**

So the specification already names the gap 005 hit, and already names its answer: an agent nobody
has opened is reached through the manifest. The project's node catalogue *is* that manifest in this
implementation.

**What the code already does.** `NodesService.RequestStart` asks a node to launch an agent, and its
only guard is:

```csharp
if (!Startable.Contains(agent.Role))
{
    return false;
}
```

`Startable` is the set the node declared in `JoinRequest.startable_roles` — read from its catalogue.
**There is no registry lookup on this path.** A role the node's catalogue names can be started
whether or not anything has ever registered under it. Today nothing reaches that path except a
question arriving for a registered agent, which is why the capability has gone unused.

**Decision.** A new operator operation asks the node holding a role to start it once. Enrolment
calls it immediately after writing the two files. The node launches the repository's adapter; the
adapter registers the one agent it attaches, which is precisely what §12.3 permits and is the
agent's own act under its own launch environment. Nothing anywhere claims an identity it was not
launched with, so Principle III is untouched.

**Alternatives rejected.**

- *Register from the node's `startable_roles`.* The broker would create registrations from a Join.
  It is one line and it is wrong: a registration would then exist that no launch environment ever
  backed, and §16 concedes that anything able to reach a broker can call Join. That is a
  caller-supplied identity wearing a node's clothes.
- *Treat a catalogue entry as registration at read time, without storing it.* Same objection with
  an extra layer: every consumer of the registry would have to learn the difference between an agent
  that exists and one that is merely promised, and §14's stranded/retired vocabulary would have to
  grow a fourth case that means "we have never met".
- *Leave it and improve the wording.* This is what 005 did. The owner has now used the result and
  asked for the other thing.

**Consequence to accept.** Adding an agent now starts a process on the operator's machine. That is a
visible act and the surface must say it is about to happen, in the same description that names the
two files. It is also bounded: the same idle rules that release any other started agent apply.

---

## R2 — Archive is a column, not a fourth state

**The question.** Where does archived live, given `conversations.state`?

**What exists.** `001_initial.sql:335`:

```sql
CREATE TABLE conversations (
  id          TEXT PRIMARY KEY,
  state       TEXT NOT NULL CHECK (state IN ('OPEN', 'CLOSING', 'CLOSED')),
  opened_utc  TEXT NOT NULL,
  closed_utc  TEXT,

  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
```

**Decision.** `archived_utc TEXT NULL`, a second column, with archived being `archived_utc IS NOT
NULL`. Not a fourth value in `state`.

**Rationale.** `state` is the closure lifecycle and it is a lifecycle: `CHECK ((state = 'CLOSED') =
(closed_utc IS NOT NULL))` binds it to a timestamp, and the participants' votes drive it. Archiving
is orthogonal — an operator may want an open conversation out of the way, and a closed one is not
thereby archived. A fourth value would make those two facts one, and the first conversation that
was both closed and archived would have to lose one of them.

The constitution's fifth principle applies here rather than being satisfied by the column existing:
the invariant to carry in storage is that a destroyed conversation leaves no message behind, which
is a foreign key with `ON DELETE CASCADE` and a test that reconstructs the defect, not a check the
repository makes on the way past.

---

## R3 — What survives destroying a conversation

**The question.** FR-006 requires that an operator asking "what happened to it" is not told nothing,
without saying what is kept.

**The tension.** §9.4 opens *"Parley is a channel, not a store"* and argues that an unbounded log
grows the blast radius of a leaked database. Keeping any of a destroyed conversation's content would
be the opposite of what destroying it is for.

**Decision.** A tombstone holding facts and no content: the conversation's identifier, when it was
destroyed, how many messages were destroyed, and which agents were party to it. No message text, no
payloads, no intents.

**Rationale.** The identifier is the thing an operator will ask about, and it is already public in
every export anybody took before the destroy. Participants and a count are what make the answer
useful — "it held nine messages between three agents and was destroyed on the fifth" — and none of
it is question or answer text, which is the category §9.1 restricts and §9.4 prunes.

It also stops the identifier being silently reused. §9.3's argument that invariants belong in storage
applies: without a tombstone the primary key is free the instant the row goes, and a new conversation
could take the name of one somebody has an export of.

**Alternative rejected.** *Keep nothing.* It makes destroy indistinguishable from a bug: a
conversation somebody exported yesterday is simply absent, with no way to tell a destroy from a
store that lost it. That is the failure mode `parley refusals` and `parley waiting` exist to avoid
elsewhere — an absence that could be either of two very different things.

---

## R4 — An archived conversation that receives a message stays archived

**The question.** FR-016 required this be decided rather than emerge.

**Decision.** It stays archived. Delivery is completely unaffected — archiving is not a mute, and no
agent is told anything about it. What changes is only which operator list the conversation appears
in by default, and an archived row that has had activity since it was archived says so when
*show archived* is on.

**Rationale.** The alternative — new traffic un-archives — reads well until it is put beside
Principle II. Archiving is an operator's judgement. Auto-un-archiving lets any agent reverse an
operator's decision by sending a message, which is an agent overruling an operator: the one
direction of authority this protocol does not have. §10.2's whole existence is the asymmetry running
the other way.

The obvious objection is that a busy conversation could hide. It is answered in the view rather than
in the state: the count of archived conversations is always shown, and the ones with activity since
archiving are marked, so the fact is one keystroke away and never silently lost.

**Alternative rejected.** *Refuse messages into an archived conversation.* This would make an
operator's filing decision visible to agents and able to fail their work, which turns a view
preference into a protocol-level block and would need §7.3 refusal semantics of its own.

---

## R5 — A conversation's name is its identifier; nothing new is introduced

**The question.** `spec.md` FR-013 says the list shows "the conversation's name". §10.2's
`conversations()` returns "the identifier".

**Finding.** They are the same thing. `001_initial.sql:342` says so: *"A conversation is named by an
agent and exported by name (SPEC.md 10.2)"* — the primary key is the name, constrained against `/`,
`:` and runs of dots precisely because it reaches filenames and headings.

**Decision.** No naming scheme is added. "Sort by name" is an ordinal sort on the identifier.

**Amendment to `spec.md`.** None needed in substance, but the Key Entities section is corrected to
say the identifier is the name so nobody reads FR-013 as asking for a new field.

---

## R6 — `conversations()` already returns everything the list and the detail panel need

**Finding.** §10.2 already specifies:

> `conversations(participant?, since?, until?, page)` — the identifier, the participants derived from
> their exchanges, how many messages each carries, when it was first and last active, and whether
> anything in it is still open. Paged, and the broker bounds the page whatever is asked for.

**Decision.** FR-013 and FR-014 need **no new broker operation**. Name, last activity, participants
and message count are all there. The operation gains an archived flag and an option to include
archived conversations, and nothing else.

This is worth recording because it is the second time 003's forward work has paid: `ListConversations`
was specified before it was built, and this is its third reader.

---

## R7 — Colour is derived from the name and is global, which is stronger than the spec asked for

**The question.** `spec.md` assumed colour assignment is per conversation.

**Finding.** `Parley.Client.Rendering.Palette` already exists, is shared by both surfaces, and
deliberately does the opposite:

> Derived from the name rather than from position in a list, so an agent is the same colour on every
> page and in every conversation. Position would repaint the transcript whenever somebody new joined,
> which is the one thing that would make the colour actively misleading.

Eight slots, with the collision case argued: *"a collision is survivable because the name is always
written, and an indistinguishable pair of colours quietly invites the reader to trust the wrong
one."*

**Decision.** Keep `Palette` as it is. FR-017's "consistent within the conversation" is satisfied by
something stronger — consistent everywhere — and the spec's assumption is amended rather than the
code.

This also answers the edge case *"participants outnumber the distinguishable colours"*: two agents
may share a colour, every message names its sender in words, and FR-018's requirement that headers
survive colour removal is what makes that safe.

---

## R8 — Local time is a display concern; the export stays UTC

**The question.** FR-019 says timestamps display in local time and remain stored in UTC. Where does
the conversion belong, given `Transcript.Write` is shared?

**Finding.** `Transcript.Write` formats with `"u"` and `CultureInfo.InvariantCulture`. It is what
`parley export` emits — a file somebody takes away and reads elsewhere, possibly on another machine
in another timezone.

**Decision.** The conversion happens at the two views, not in `Transcript.Write`. The export keeps
UTC and keeps saying so.

**Rationale.** A file that says `14:03` without saying whose `14:03` is worse than one that says
`14:03Z`. The reader of an export is by definition not the person who ran the command. The reader of
a screen is.

**Consequence.** The shared client gains a formatting helper that both surfaces call, so the two
cannot disagree about how a local time is written — the same reason seating and transcripts were
pulled into `Parley.Client` in the first place.

---

## R9 — The keystroke is a function key, and the address is built from routes the console already serves

**Findings.** Every single-letter key is spoken for by an action, and `?` by the action list. The
console serves `/Projects/Agents`, `/Conversations/Detail` and `/Conversations/Index` as Razor pages
with query strings.

**Decision.** `F8`. The address is the console's own route for the thing on screen, built from a
single place in the shared client so the terminal cannot deep-link to a route the console has
renamed.

**Note.** The address itself was a defect until today: `Shell.ConsoleAddress` was a literal `5045`
while every other statement of the default said `5280`, so the existing "open in browser" action
opened a dead port and reported success. It now takes `ConsoleHost.LoopbackAddress`. Building deep
links from a second hand-written copy of a route would be the same defect one layer up, which is why
they come from one place.

---

## R10 — Both surfaces, and what the drift test already forces

**Finding.** 005 added `DriftTests`, which reflects every public `*Async` on `BrokerClient` against
both surface trees and the operations contract. Four operations — `Refusals`, `Clarifications`,
`Referrals`, `Notifications` — are bound by the console and not by the terminal, recorded in
`contracts/operations.md` as deliberate.

**Decision.** The four new operations are bound by both surfaces. `contracts/operations.md` is
extended rather than replaced, and the four existing one-sided operations stay one-sided and stay
recorded — this feature does not widen the terminal into a survey tool.
