# Research: Work comes back when the session that took it is gone

## R1. How the broker identifies a session today

**Finding.** It does not, below the server's memory.

- Storage has `agents.connected`, a count raised on attach and lowered on detach. No row names a
  session.
- `BrokerRuntime` keeps an in-memory list of `AttachedAgent` per agent name, one per open Attach
  stream. Each holds its own `Broker` instance.
- Every unary agent call (`DrainInbox` included) carries only the agent's name.
  `BrokerRuntime.RunAsync` runs it on *the newest* session for that name ("any of them... they differ
  only in when each attached"). So even the server cannot say which session collected.
- `AttachRequest.session_ref` is an adapter's resume handle (§14 lazy spawn), stored on the agent row.
  `Broker.StartAsync` passes `sessionRef: null`. It is not a per-connection identity and is not
  suitable: it is supplied by the adapter, may be blank, and two windows may share one.

**Decision.** The broker issues each session an identifier when its Attach stream opens, returns it in
the `Attached` event, and the adapter sends it with every collection.

**Rationale.** Only the broker knows when a stream opens and closes, so only it can say which sessions
are live. An identifier the adapter made up could collide or be reused.

**Alternatives rejected.**
- *Reuse `session_ref`.* Adapter-chosen, optional, and shared between windows.
- *Identify the session by the gRPC connection.* HTTP/2 multiplexes; the MCP adapter's unary calls and
  its Attach stream are not guaranteed to share a connection, and the transport is meant to be
  disposable (constitution: *the transport is disposable*).
- *Send it as gRPC metadata.* Workable, but a field is visible in `proto/parley.proto`, which
  `Parley.Spec.Tests` holds against SPEC.md. Metadata would be a contract the spec tests cannot see.

## R2. Where sessions live

**Decision.** A `sessions` table with one row per session, open or closed. A row is inserted when a
stream opens and marked closed (`closed_utc`) when it ends. It is kept while any message refers to it
and deleted once none does. `agents.connected` stays, maintained by triggers on `sessions` (insert
raises it, closing lowers it), so it can no longer disagree with the rows. Broker start closes every
open row (the process that held them is gone) and deletes the closed rows nothing refers to.

**Rationale.**
- A closed row is needed: a controlled end may arrive after the stream has already dropped
  (`HandBack` on a closed session, R8), a reclaim needs the old session's agent and place in line
  (R9), and the newer-session rule compares places (R10). A deleted row can answer none of these.
- With rows kept, `messages.delivered_session` can be a real foreign key (Principle V).
- `connected` keeps its readers (§14's liveness table, the registry, reports) unchanged.

**Alternatives rejected.**
- *Delete the row on close.* Simpler, but loses the agent and the place in line of exactly the
  sessions the rules have to reason about.
- *Keep the live set in memory.* Leaves storage unable to say which deliveries are orphaned, and a
  second reader of the store (export, reports) would see deliveries pointing at nothing.
- *Replace `connected` with a count over `sessions`.* Rewrites every liveness reader for no behaviour
  change.

## R3. When work is given back

A session ends in one of three ways (spec, *Why this exists*). The broker learns of the first from
the adapter and of the other two only by the stream ending, and cannot tell those two apart without a
timer, which the rules forbid.

**Decision.**
- **Controlled end** (R8): the adapter's `HandBack` returns the session's work at once.
- **Abrupt end, the process comes back** (R9): the reconnecting adapter names its old session and the
  new one takes over its deliveries.
- **Abrupt end, nobody comes back** (R10): decided at collection. A collecting session receives
  another session's work only if that session is closed and the collector is newer than the
  collection.
- The step on attach (`StartAsync`'s redelivery loop and `GetAbandonedOwedByAsync`) is removed.

**Rationale.** Each case is decided by a fact the broker holds: a call it received, a name it issued,
an order it assigned. None depends on which of two events the broker heard first.

**Alternatives rejected.**
- *Any closed session's work goes to the next collector* (the first plan). A connection blip hands
  one window's work to the other, which the old count-based rule prevented. Found by `/speckit-analyze`
  (C1): `BrokerLink` re-attaches on any dropped stream, not only after a broker restart.
- *Wait N seconds before giving work back.* A timeout; forbidden.
- *Read the end's cause from the HTTP/2 layer* (cancel versus reset). Ties the rule to gRPC, and the
  transport is meant to be disposable. A killed process and a dead network look alike there anyway.
- *Abrupt work waits for the stall flag.* Then a fast restart after a kill, which may be every
  editor close (see R11), is not fixed at all.

## R4. A collection without a live session

**Decision.** `DrainInbox` with an empty, unknown or other agent's session is refused
(`FailedPrecondition`, naming the fix: attach first). Every collection path today already holds an
Attach stream: `parley-mcp` (`ParleyTools`) and the person's surfaces (`SeatVoice`).

**Rationale.** FR-001 says every delivery records its session. Accepting a collection with no session
would create deliveries that are given back on every collection.

## R5. The session identifier

**Decision.** 32 lowercase hex characters from a cryptographic random source, issued by the server.
Schema checks length and character class.

**Rationale.** Unguessable so one agent cannot name another's session by accident. **It is not a
credential**, and the plan says so: §16 already concedes that anything reaching a broker can claim any
name. A session also must belong to the calling agent; the server checks the pair.

## R6. Wire and version

- `Attached` gains `session`; `exchanges_redelivered` is removed (nothing is given back on attach any
  more, so it would always be 0). `DrainInboxRequest` gains `session`.
- `AttachRequest` gains `previous_session` (R9). `Agents` gains `HandBack` (R8).
- **Protocol 0.8 → 0.9.** `specs/026-roadmaps/plan.md` also claims 0.9 and has not landed. Whichever
  merges first takes 0.9; the other moves to the next number. Nothing is released, so no compatibility.
- **`VersionPrefix` 0.42.x → 0.43.0**: a breaking wire change while the major is zero moves the minor.

## R7. The race test

A broker-level test makes the race deterministic with no timing: attach session A, collect (delivered
to A), attach session B *while A is still attached* (the broker has not heard A leave), detach A,
collect from B. Today B receives nothing; after the change it receives the Request. Mutation check:
remove the give-back step and watch it fail. A server-level twin over the loopback socket holds the
wire half (the session travels on `DrainInbox`).

Each rule gets its own deterministic case, all with no timing: a controlled end hands back (R8); a
reclaim keeps the work (R9); an older window never takes an abrupt end's work, a newer one does (R10);
a broker restart under two windows keeps each window's work in either reconnect order (R9 and R10).

## R8. A controlled end: `HandBack`

**Decision.** A new `Agents` operation, `HandBack(agent, session)`. It returns, in one transaction,
the latest owed message of each open exchange delivered to that session: both delivery columns are
cleared, so the agent's next collection from any session receives it. It is accepted for an open or
a closed session of the calling agent, because the adapter's stream may already have dropped when it
shuts down. Handing back twice does nothing the second time.

- `parley-mcp` calls it in `BrokerLink.DisposeAsync`, before cancelling the stream.
- The person's surfaces call it when they drop a seat on purpose (`SeatBook`'s 20-minute idle drop and
  closing the surface). The person comes back on another surface and finds the work there.
- It is not a `Detach`. §14 forbids one because an agent that dies without calling it would still look
  present. Presence is still only the stream; `HandBack` says nothing about presence, and a missing
  `HandBack` is read as an abrupt end, never as lost work (FR-013).

**Alternatives rejected.**
- *A flag on the Attach stream's last message.* The stream is server-streaming; the adapter cannot
  send on it.
- *Name it `Leave` or `Detach`.* Both read as presence, which is exactly what it must not claim.

## R9. Reclaiming a session after a dropped connection

**Decision.** `AttachRequest.previous_session` names the session this process had. When the new
session opens, in the same transaction:
- if the named session exists and belongs to the same agent, its deliveries move to the new session
  (`delivered_session` updated; nothing is delivered again), and the new session takes the old one's
  place in line (`sessions.place`);
- otherwise it is ignored, and the session opens fresh. A store rebuilt since, or a name from another
  agent, is not an error worth refusing an attach over.

The old session may still be open, because the broker has not yet seen its stream drop. The move
happens anyway: it is the same process saying it is the same worker.

`BrokerLink` keeps the last session it was given and sends it on every re-attach. `SeatHold` does the
same for the person's surfaces.

**Not a credential.** Naming another window's session takes that window's work. The identifier is 32
random hex characters and the server checks the agent, so it cannot happen by accident; on purpose it
is something §16 already concedes, because anything reaching the broker can claim any name.

## R10. The newer-session rule

**Decision.** Every session gets a sequence number when it opens (`sessions.seq`, `AUTOINCREMENT`, so
it is never reused even across broker restarts). Its *place* is its own `seq`, or the place of the
session it reclaimed. Each delivery records a *horizon*: the highest `seq` that existed when it was
delivered.

At collection by session C, the latest owed message of each open exchange owed by C's agent is given
back when it was delivered to a **closed** session and **C's place is above the delivery's horizon**.

- A restarted process is a fresh session opened after the collection: its place is above the horizon,
  so it gets the work, whichever order the broker heard the close and the open (FR-004).
- A window already open when the work was collected has a place at or below the horizon: it never
  takes it.
- A process that reconnected and reclaimed keeps its old place, so after a broker restart a surviving
  window does not take a sibling's work by reconnecting first.

**Rationale.** A sequence, not a clock: §13 already avoids ordering by wall time, and a clock that
steps back would reorder sessions.

**Residual, accepted.** A window opened *after* the work was collected, collecting while the holder's
connection is down, receives it; if the holder then reconnects, both have it. It needs a second window
opened after the collection and an outage at once. The first answer closes the exchange.

## R11. Does the editor end the adapter cleanly?

**Unknown, and measured during implementation.** `parley-mcp` disposes its link in a `finally` around
`host.RunAsync()` (`Program.cs:168-174`), which runs when the MCP host stops on its input closing. An
editor that kills the process instead skips it. The design does not depend on the answer: a kill is an
abrupt end, and R10 still gives the work to the restarted process. The measurement decides only which
rule serves the common fast restart, and is recorded in this file.
