-- Parley schema, migration 001: the whole of it.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- SPEC.md §9 is the authority for everything below, and tests/Parley.Spec.Tests executes that
-- document's fragments against SQLite in the order written. The two must agree; the document is
-- the specification and this is the implementation of it.
--
-- ============================================================================================
-- THIS FILE WAS ELEVEN FILES, AND THE COLLAPSE IS THE OWNER'S CALL OF 2026-09-26
-- ============================================================================================
--
-- Migrations 002 to 011 were folded in here and deleted, and the schema number went from 11 to 1.
-- The ground that permits it is the only one that ever permits it: **nothing has been released and
-- no store outside a test process holds anything worth keeping.** The owner's words were "nothing
-- we have is worth keeping right now".
--
-- It is the same ground 011 was edited on rather than being followed by a 012 — 0.29.0 was
-- installed nowhere, so it had been applied in no store that mattered. This is that argument
-- applied to the whole sequence at once, and it is the last time it will be available: the first
-- release makes every future change a migration, because from then on somebody's conversations are
-- in a file on their disk.
--
-- **A store written by an older build is not upgraded and is not readable.** There is no migration
-- path from 11 to 1 and there is deliberately no attempt at one; `reset.ps1` and `reset.sh` at the
-- repository root clear a developer's stores, which is what they exist for.
--
-- **THE STRUCTURE BELOW WAS DERIVED, NOT HAND-MERGED.** A store was migrated through all eleven
-- and its schema read back out of `sqlite_master` in creation order, because hand-merging eleven
-- files is how a collapse quietly loses a trigger — and four of the tables here (`messages`,
-- `exchanges`, `size_refusals`, `shared_files`) existed in two shapes across that sequence, with
-- fourteen triggers dropped and recreated between them.
--
-- **IT WAS VERIFIED BY A ONE-TIME PROBE, AND THAT PROBE CANNOT BE KEPT.** A store built from this
-- file was compared object by object against one built by the eleven, before this file was
-- trusted: **41 shared objects, with identical names and identical creation order**, plus the one
-- table this feature adds. What it produces is 20 tables of ours beside SQLite's own
-- `sqlite_sequence`, 7 indexes and 14 triggers. Twenty-two objects differed on raw bytes and twenty-one of those differed
-- only by carriage returns inside the text SQLite stores in `sqlite_master` — the later migrations
-- were LF files and this one is CRLF, which changes nothing a reader or a constraint can observe.
-- **Exactly one object differed for any other reason: `agents`, by the one number this feature
-- changes.** The transcript is in `REVIEW-LEDGER.md`.
--
-- That comparison is not a test and can never be one again, because the thing it compared against
-- has been deleted. Saying otherwise in a comment here would be a claim nothing checks. What is
-- checked from now on is narrower and is what `SchemaConformanceTests` and
-- `tests/Parley.Spec.Tests` already do: this file and SPEC.md §9 agree, and every fragment in the
-- document executes.
--
-- ============================================================================================
-- WHAT THE DELETED FILES ARGUED, WHERE IT IS STILL TRUE
-- ============================================================================================
--
-- Reasoning attached to a column or a constraint came with it and is inline below. What follows is
-- the reasoning that lived in those files' headers and is still true of this schema. **What is not
-- carried forward is every file's rebuild mechanics** — the order to drop triggers in, which
-- generated column to leave out of a copy, which AFTER INSERT trigger would double-count a
-- referral if the copy ran under it. Those were instructions for rebuilds that no longer happen,
-- and keeping them in a file that creates each table exactly once would be archaeology dressed as
-- guidance. They are in the git history of the deleted files.
--
-- **002 — an operator's instruction is not a message, and could not have been one.**
-- `messages.from_agent` is a foreign key to `agents(name)` and an operator is not an agent. That
-- is the key doing its job rather than an obstacle to route around: relaxing it to admit one
-- sender that is not registered would admit every other unregistered sender in the same edit. So
-- `instructions` is its own table, and it cascades on the agent the way `size_refusals` does,
-- because §10.2's purge removes an agent and everything that referred to it.
--
-- **003 — the message cap is 8,000 and it is the first number here set from measurement.**
-- §17 question 16 asked for `refusals()` showing "the same agents turned away repeatedly at
-- similar sizes, rather than one agent once", and that is what it showed: six agents, forty-three
-- refusals, worst case 6,195 characters, means between 4,328 and 4,556. Every refusal ever
-- recorded fits under the number. It is a CHECK rather than a limit the broker enforces because
-- §9.3 puts invariants in storage where application code cannot route around them, and that
-- decision is worth its cost rather than regretted. It stays provisional on the same terms: move
-- it on that record, not because a message did not fit.
--
-- **004 — the destroy invariant of §14 needs no cascade, and adding one would weaken it.**
-- Every column referencing `conversations(id)` does so *without* `ON DELETE CASCADE`, which is
-- exactly what the deletion relies on: the deletes are ordered inside one transaction with foreign
-- keys deferred to commit, so a table nobody remembered fails the whole destroy rather than leaving
-- a message pointing at a conversation that is gone. A cascade would remove that guard while
-- looking like it strengthened it.
--
-- **005 — an agent's reply to an instruction carries the same cap as the instruction, and the
-- review gate is why.** Lens 2 found `instructions.reply` bounded by nothing at all, on any path:
-- a node — or anything able to reach the broker and claim to be one, which §16 says is anything on
-- the network — could write an arbitrary blob into the operator's own record, and a
-- 200,000-character reply was stored and read back whole. The same number as `text`, because what
-- the cap defends is how much of somebody's context one piece of text can fill and a reply fills
-- the same context an instruction does. Two numbers would also mean two arguments the next time
-- evidence moves one.
--
-- **006 and 007 — two caps are off by default, and `2147483647` is how "off" is spelled.**
-- §7.1's open-question cap and §5.2's clarification cap. Nothing cuts a line of work off before its
-- participants are finished (principle 2.8), and **a cap censors the evidence that would justify
-- it** — the reasoning this repository has now used three times. Zero was not available to either:
-- for clarifications it already means no clarification at all, which a deployment may want, and
-- the largest integer means nothing else. **What survives is the range**: a real cap is still held
-- to its band by the triggers below, so a bug writing a large number is still refused rather than
-- silently switching the rule off, and the one value beyond the band has to be written on purpose.
-- 020 adds a third to the list and the constitution was amended to 3.0.0 to say so — see the
-- `owns` column, whose bound is off on the same reasoning and at a cost recorded there.
--
-- **008 — protocol 0.5 made every message answered, and nothing times out.** `ORPHANED`,
-- `ghosted` and the delivery and working windows are gone; an exchange that stops moving is
-- flagged to a person rather than closed by a clock. An announcement is a Request answered
-- `acknowledged`, which is why there is no standalone notification shape here.
--
-- **010 — `name_key` is `upper(name)` and that retired a caveat.** The key used to fold every
-- letter so that two spellings of one accented name were one row. With a shared file's name held
-- to A–Z, digits, '.', '_' and '-', SQLite's own `upper()` computes the same key — and a key that
-- disagreed with its name would be a second name for one file.
--
-- **011 — protocol 0.7 reduced a Request to the ask, and no row was ever touched.** The old
-- requirements lived in one BEFORE INSERT trigger, and a message is never updated: §9.4 prunes
-- text, it does not rewrite it. Replacing the trigger decided what may be written from then on and
-- said nothing about what had been written, because rewriting stored payloads would destroy words
-- a participant wrote, which is the one thing the log exists to keep. That property is why this
-- collapse is safe to do *now* and would not have been safe after a release.
--
-- `tr_result_is_well_formed` was deliberately left alone by 011 and is unchanged here. It never
-- named an item identifier: it requires at least one outcome, holds a blocked outcome to naming its
-- blocker, checks every source against the registry, and lets only the broker write 'closed'. All
-- four are as true of one outcome named 'request' as of five named by a Request's items.
--
-- ============================================================================================

CREATE TABLE nodes (
  -- Detected, never configured, per §4.1.1. The primary key rather than a surrogate, because
  -- the machine's identity is the thing agents are named after.
  machine        TEXT    PRIMARY KEY
                 CHECK (length(machine) BETWEEN 1 AND 64
                        AND machine GLOB '[a-z0-9]*'
                        AND machine NOT GLOB '*[^a-z0-9.-]*'),
  -- Reported for the operator, and used for nothing. A machine is reached through the
  -- connection its node already holds, never by an address the broker dials, so none of this
  -- is a route in -- it is what somebody reading a fleet listing needs in order to know which
  -- box they are looking at. Recording an address the broker then connected to would invert
  -- §13's trust direction, letting a registration tell the broker where to send traffic.
  os             TEXT,
  os_version     TEXT,
  address        TEXT,
  domain         TEXT,
  -- The same observation agents.connected records, one level up, and the one that decides
  -- whether an agent here can be started at all. An agent on a disconnected node is not
  -- unreachable in some derived sense; there is simply nothing left that could start it.
  connected      INTEGER NOT NULL DEFAULT 0 CHECK (connected IN (0, 1)),
  changed_utc    TEXT,
  first_seen_utc TEXT    NOT NULL
) STRICT
;;

CREATE TABLE agents (
  -- role@machine, per §4.1.1. Stored whole because it is what every other table references
  -- and what every operator types, and split as well because a role question (§5.5) has to
  -- find every agent sharing a role, which a substring match on the whole name cannot do
  -- safely: 'infra' is a prefix of 'infrastructure'.
  name            TEXT    PRIMARY KEY,     -- logical name, NOT a session id
  -- Generated rather than supplied, so the halves cannot disagree with the name they came from.
  -- They exist as columns at all because a role question (§5.5) has to find every agent sharing
  -- a role, and a substring match on the whole name cannot do that safely -- 'infra' is a
  -- prefix of 'infrastructure'. Storing them as ordinary columns would make that indexable and
  -- make it possible for a writer to set role='infra' on an agent named 'billing@server-b'.
  --
  -- A name with no '@' produces an empty role, which the CHECK below refuses. That is the
  -- malformed case failing at the point of insertion rather than becoming a row nothing can
  -- address.
  role            TEXT    NOT NULL
                  GENERATED ALWAYS AS (substr(name, 1, instr(name, '@') - 1)) STORED,
  -- The node that reported this agent. Detected there and never configured, so two machines
  -- cannot both claim to be server-b -- see §4.1.1.
  machine         TEXT    NOT NULL
                  GENERATED ALWAYS AS (substr(name, instr(name, '@') + 1)) STORED
                  REFERENCES nodes(machine),
  adapter         TEXT    NOT NULL,
  tier            TEXT    NOT NULL CHECK (tier IN ('full', 'pull', 'headless')),
  workspace       TEXT,
  session_ref     TEXT,                    -- adapter-specific resume handle
  adapter_config  TEXT,                    -- opaque to the broker (see §12)
  owns            TEXT,                    -- one line, read by peers (see §17 q10)
  -- How many of this agent's adapters are connected right now, raised on connect and lowered
  -- on disconnect, and written by nothing else. Not derived from a timestamp: §4.0 gives the
  -- broker a held connection, so this is an observation rather than the guess a last-seen
  -- column forces on every reader -- and the guess is what let a session killed on Tuesday
  -- read as healthy on Friday.
  --
  -- A count rather than a flag, because an agent may have two sessions open. With a flag the
  -- second one closing says the agent is gone while the first is still mid-answer, and the
  -- next attach releases work somebody is doing -- which is exactly the failure the timestamp
  -- lease this replaced was invented to avoid. Counting keeps that property without needing an
  -- expiry to guess at.
  connected       INTEGER NOT NULL DEFAULT 0 CHECK (connected >= 0),
  -- When that last changed. For reports, never for decisions; nothing routes on it.
  changed_utc     TEXT,
  -- Two values, because there are only two judgements a person makes about a registration:
  -- it stands, or it is finished. Everything else that used to live here was a description
  -- of reachability, which is now observed rather than stored -- see §14.
  status          TEXT    NOT NULL CHECK (status IN ('active', 'retired')),

  CHECK (adapter_config IS NULL
         OR (json_valid(adapter_config) AND length(adapter_config) <= 4000)),

  -- What this agent owns, in one line, written by whoever configured its launch. Bounded
  -- here because terseness is a security control (principle 2.2) and this text is charged
  -- against every agent's working memory once per peer, every time the roster is listed. The
  -- number is measured: a roster entry costs about 75 characters of structure before the
  -- statement, so ten peers at 100 cost roughly what one message is allowed to.
  --
  -- It must also render as what it says, on one line, because it is put into a list
  -- of peers that every agent reads: anything able to break the line, hide itself, or
  -- reorder what follows lets one entry imitate the entries around it.
  --
  -- The class below is every code point .NET reports as Control, Format, LineSeparator or
  -- ParagraphSeparator, collapsed into ranges. It is exact rather than approximate because
  -- it was derived by sweeping the whole of Unicode and comparing against OwnershipStatement,
  -- and OwnershipTests re-runs that sweep so the two cannot drift. Testing C0 alone did not
  -- hold and neither did guessing at blocks: probing found U+2028 and U+2029, which are line
  -- breaks wherever this is rendered; U+202E and U+061C, which reorder the display of what
  -- follows; U+00AD and U+FEFF, which are invisible; and U+E0020-U+E007F, the tag characters
  -- used to smuggle text past a reader entirely. Every one of them was accepted before.
  --
  -- The last clause catches an embedded NUL, which no range can: char(0) is the empty string
  -- in SQLite, so a class built from it matches everything and would refuse every statement.
  -- length() stops at a NUL while concatenation does not, so a value whose length is
  -- unchanged by appending a character is hiding bytes behind a terminator.
  CHECK (owns IS NULL
         OR (length(owns) <= 2147483647
             AND owns NOT GLOB '*[' ||
             char(1) || '-' || char(31) || char(127) || '-' || char(159) ||
             char(173) || '-' || char(173) || char(1536) || '-' || char(1541) ||
             char(1564) || '-' || char(1564) || char(1757) || '-' || char(1757) ||
             char(1807) || '-' || char(1807) || char(2192) || '-' || char(2193) ||
             char(2274) || '-' || char(2274) || char(6158) || '-' || char(6158) ||
             char(8203) || '-' || char(8207) || char(8232) || '-' || char(8238) ||
             char(8288) || '-' || char(8292) || char(8294) || '-' || char(8303) ||
             char(65279) || '-' || char(65279) || char(65529) || '-' || char(65531) ||
             char(69821) || '-' || char(69821) || char(69837) || '-' || char(69837) ||
             char(78896) || '-' || char(78911) || char(113824) || '-' || char(113827) ||
             char(119155) || '-' || char(119162) || char(917505) || '-' || char(917505) ||
             char(917536) || '-' || char(917631)
             || ']*'
             AND length(owns) = length(owns || '.') - 1)),

  -- The grammar of §4.1.1, enforced here rather than in the server for the reason §9.3
  -- gives: an invariant the server holds is an invariant only while the server is the only
  -- writer. There is no clause tying the name to its parts, because generation does that: the
  -- parts are computed from the name and cannot be set independently of it.
  --
  -- What is left bounds each part and refuses anything outside its class, which is what stops a
  -- name carrying a space, a control character, or a second '@' that would make "which part is
  -- the machine" a question with two answers.
  CHECK (length(role) BETWEEN 1 AND 32
         AND role GLOB '[a-z]*'
         AND role NOT GLOB '*[^a-z0-9-]*'),
  CHECK (length(machine) BETWEEN 1 AND 64
         AND machine GLOB '[a-z0-9]*'
         AND machine NOT GLOB '*[^a-z0-9.-]*')
) STRICT
;;

CREATE TABLE agent_ownership_accepted (
  -- What a person agreed this agent owns, and the evidence of how they arrived at it.
  --
  -- SEPARATE FROM agents.owns ON PURPOSE, and the distinction is the whole of FR-020. This is
  -- what somebody accepted; agents.owns is what peers actually read, and it is filled from the
  -- launch environment when the agent attaches (SPEC.md §12.3). Until the agent restarts the
  -- two differ, and a surface says so by comparing them rather than by keeping a third flag
  -- somebody has to remember to clear. Both halves here are observations, so the state cannot
  -- be wrong -- which is the shape 0.29.1 and 0.25.3 both argued for after collapsing two
  -- questions into one made both unanswerable.
  agent            TEXT    NOT NULL PRIMARY KEY
                           REFERENCES agents(name) ON DELETE CASCADE,
  statement        TEXT    NOT NULL,
  -- Which of the three routes FR-015 admits. A person's characters are never attributed to a
  -- model: 0.29.0's second review finding was a retry writing a length no agent composed into
  -- size_refusals, which is the table §17 question 16 reads.
  source           TEXT    NOT NULL CHECK (source IN ('proposed', 'edited', 'composed')),
  -- The length of the AGENT'S OWN message, or NULL where there was no proposal. This is
  -- FR-026's evidence, and the reason the bound above could be switched off at all: the
  -- number that replaces it has to be chosen from what agents actually propose, the way the
  -- message cap's 8,000 was chosen from `parley refusals`.
  proposed_length  INTEGER          CHECK (proposed_length IS NULL OR proposed_length >= 0),
  -- The discussion, so FR-011 is answerable from the agent rather than from somebody's memory.
  conversation_id  TEXT             REFERENCES conversations(id),
  accepted_utc     TEXT    NOT NULL,

  CHECK (length(statement) >= 1),
  -- A composed line had no proposal and no discussion; anything else had both.
  CHECK (source <> 'composed' OR proposed_length IS NULL),
  CHECK (source =  'composed' OR conversation_id IS NOT NULL)
) STRICT;

CREATE TABLE agent_credentials (
  agent     TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  name      TEXT NOT NULL,                -- the adapter's name for it: api_key, org_token
  reference TEXT NOT NULL,

  PRIMARY KEY (agent, name),
  CHECK (length(name) BETWEEN 1 AND 64 AND name NOT GLOB '*[^a-z0-9_]*'),
  CHECK (reference LIKE 'env:_%'
         OR reference LIKE 'keychain:_%'
         OR reference LIKE 'file:_%')
) STRICT
;;

CREATE TABLE invocations (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  agent         TEXT    NOT NULL REFERENCES agents(name),
  -- Before and after, so the record can say whether the turn changed anything. An agent that
  -- ran and is still owed as much as it was is the one worth noticing.
  --
  -- "Waiting" rather than "owed", because the two stopped being the same thing: an answer to a
  -- question this agent asked leaves no debt at all, and an uncollected message is a reason to
  -- give an agent a turn that no count of debts can see. That distinction is what let an agent
  -- sit connected beside an uncollected message for twenty minutes with nothing wrong anywhere.
  waiting_before INTEGER NOT NULL CHECK (waiting_before >= 0),
  waiting_after  INTEGER NOT NULL CHECK (waiting_after >= 0),
  started_utc   TEXT    NOT NULL,
  elapsed_ms    INTEGER NOT NULL CHECK (elapsed_ms >= 0),
  -- The turn's verdict: zero when it ran to a clean finish. Named for a process exit code
  -- because it once was one, back when a turn and a process were the same thing.
  exit_code     INTEGER NOT NULL,
  -- A timeout is not a failure of the agent: it is a turn that took longer than the supervisor
  -- was willing to wait for, and the two need different responses. It does not end the agent --
  -- see §11.1.
  timed_out     INTEGER NOT NULL DEFAULT 0 CHECK (timed_out IN (0, 1)),
  -- What the runner charged, as the runner reported it.
  cost_usd      REAL    NOT NULL DEFAULT 0.0 CHECK (cost_usd >= 0.0),
  -- How many model turns went into one Parley turn. The count separates an agent that answered
  -- a question from one that went and did an afternoon's work on the strength of it.
  model_turns   INTEGER NOT NULL DEFAULT 0 CHECK (model_turns >= 0),
  -- Tool calls refused for want of permission. A resident agent runs with nobody at the
  -- keyboard, so a call needing approval is not paused for a human -- it is refused, and the
  -- agent answers anyway and worse. Nothing else in the record distinguishes that from an agent
  -- that has simply become unhelpful.
  permission_denials INTEGER NOT NULL DEFAULT 0 CHECK (permission_denials >= 0)
) STRICT
;;

CREATE INDEX ix_invocations_agent ON invocations(agent, started_utc)
;;

CREATE TRIGGER tr_agent_declares_its_machine
BEFORE INSERT ON agents
BEGIN
  INSERT INTO nodes (machine, connected, first_seen_utc)
  VALUES (substr(NEW.name, instr(NEW.name, '@') + 1), 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
  ON CONFLICT(machine) DO NOTHING;
END
;;

CREATE TABLE conversations (
  id          TEXT PRIMARY KEY,
  state       TEXT NOT NULL CHECK (state IN ('OPEN', 'CLOSING', 'CLOSED')),
  opened_utc  TEXT NOT NULL,
  closed_utc  TEXT, archived_utc TEXT, closed_by TEXT
  CHECK (closed_by IS NULL OR closed_by IN ('consensus', 'person')),

  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  -- A conversation is named by an agent and exported by name (SPEC.md 10.2), so this value
  -- reaches a filename, a report heading or a console. No '/' and no ':', because both are
  -- path syntax, and no '..' anywhere, because a run of dots is path syntax without needing a
  -- separator. This is the authoritative name: exchanges reference it and messages carry a copy
  -- held in agreement by a composite foreign key, so constraining it here is what constrains it
  -- everywhere.
  --
  -- It was previously responder_model's character set, which permits '/'. That set is right for
  -- a model identifier -- 'some-org/some-model-9' is a real name -- and wrong for this, and the
  -- comment claiming to keep path separators out sat directly above a GLOB that admitted them.
  -- '../../etc/passwd' was a legal conversation id.
  CHECK (length(id) BETWEEN 1 AND 64
         AND id NOT GLOB '*[^a-zA-Z0-9._-]*'
         AND id NOT GLOB '*..*')
) STRICT
;;

CREATE TABLE conversation_participants (
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  agent           TEXT NOT NULL REFERENCES agents(name),
  joined_utc      TEXT NOT NULL,
  vote            TEXT CHECK (vote IN ('done', 'not_done')),
  polled_utc      TEXT,
  voted_utc       TEXT,

  PRIMARY KEY (conversation_id, agent),

  -- A vote without a poll is an agent answering a question nobody asked.
  CHECK (vote IS NULL OR polled_utc IS NOT NULL),
  CHECK ((vote IS NULL) = (voted_utc IS NULL))
) STRICT
;;

CREATE TRIGGER tr_no_vote_after_closure BEFORE UPDATE ON conversation_participants
WHEN NEW.vote IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END
;;

CREATE TABLE destroyed_conversations (
  id            TEXT PRIMARY KEY,
  destroyed_utc TEXT NOT NULL,
  messages      INTEGER NOT NULL CHECK (messages >= 0),
  participants  TEXT NOT NULL
) STRICT
;;

CREATE TABLE "instructions" (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  agent        TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  text         TEXT    NOT NULL,
  outcome      TEXT    NOT NULL
               CHECK (outcome IN ('pending', 'taken', 'declined', 'undeliverable', 'failed')),
  reply        TEXT,
  issued_utc   TEXT    NOT NULL,
  settled_utc  TEXT,

  -- The same bound as a message (rule 8.5), and deliberately the same number rather than a
  -- second one. SpecConsistencyTests holds the two together, because SQLite cannot say "the same
  -- as that one" and they would otherwise drift at the worst moment -- when evidence moves the
  -- message cap and this is left behind at a number nobody argued for.
  CHECK (length(text) <= 8000),

  -- And the agent's half of the same exchange, which had no bound at all. An operator's words are
  -- typed by a person and an agent's are produced by a model; if either needed the cap less, it
  -- was the one that already had it.
  CHECK (reply IS NULL OR length(reply) <= 8000),

  -- Settled and unsettled are one fact, so they move together or the row is lying about one of
  -- them. A reply belongs to a settled instruction: an agent that has not been given the turn
  -- yet cannot have said anything, and a row claiming otherwise would be read as an answer.
  CHECK ((outcome = 'pending') = (settled_utc IS NULL)),
  CHECK (outcome <> 'pending' OR reply IS NULL)
) STRICT
;;

CREATE INDEX ix_instructions_agent ON instructions(agent, issued_utc)
;;

CREATE TABLE "exchanges" (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  pair_key            TEXT    NOT NULL,
  requester           TEXT    NOT NULL REFERENCES agents(name),
  receiver            TEXT    NOT NULL REFERENCES agents(name),
  owed_by             TEXT    NOT NULL REFERENCES agents(name),
  state               TEXT    NOT NULL CHECK (state IN ('OPEN', 'CLOSED')),
  request_group       TEXT
                      CHECK (request_group IS NULL
                             OR (length(request_group) BETWEEN 1 AND 64
                                 AND request_group NOT GLOB '*[^a-zA-Z0-9._-]*')),
  clarify_depth       INTEGER NOT NULL DEFAULT 0,
  clarify_depth_cap   INTEGER NOT NULL DEFAULT 2147483647,
  open_pair_cap       INTEGER NOT NULL DEFAULT 50
                      CHECK (open_pair_cap BETWEEN 1 AND 50),
  uncollected_flag_ms INTEGER NOT NULL,
  stalled_flag_ms     INTEGER NOT NULL,
  opened_utc          TEXT    NOT NULL,
  delivered_utc       TEXT,
  closed_utc          TEXT,
  request_hash        TEXT    NOT NULL,
  outcome             TEXT    CHECK (outcome IN ('complete', 'blocked', 'closed')),
  conversation_id     TEXT    NOT NULL REFERENCES conversations(id),
  version             INTEGER NOT NULL DEFAULT 0,

  CHECK (requester <> receiver),
  CHECK (owed_by IN (requester, receiver)),
  CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
  CHECK (clarify_depth_cap BETWEEN 0 AND 1000 OR clarify_depth_cap = 2147483647),
  CHECK (uncollected_flag_ms > 0 AND stalled_flag_ms > 0),
  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  CHECK ((state = 'CLOSED') = (outcome IS NOT NULL)),
  CHECK (pair_key = min(requester, receiver) || char(31) || max(requester, receiver))
) STRICT
;;

CREATE TABLE "messages" (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  exchange_id     INTEGER NOT NULL REFERENCES "exchanges"(id),
  type            TEXT    NOT NULL CHECK (type IN ('request', 'clarify', 'result')),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  to_agent        TEXT    NOT NULL REFERENCES agents(name),
  responder_model TEXT    NOT NULL,
  payload_json    TEXT    NOT NULL,
  content_hash    TEXT    NOT NULL,
  conversation_id TEXT    NOT NULL REFERENCES conversations(id),
  created_utc     TEXT    NOT NULL,
  dedup_bucket    INTEGER GENERATED ALWAYS AS (unixepoch(created_utc) / 60) STORED,
  delivered_utc   TEXT,

  CHECK (from_agent <> to_agent),
  CHECK (length(payload_json) <= 8000),
  CHECK (json_valid(payload_json)),
  CHECK (length(responder_model) BETWEEN 1 AND 64
         AND responder_model NOT GLOB '*[^a-zA-Z0-9._:/-]*'),
  CHECK (length(content_hash) = 64
         AND content_hash NOT GLOB '*[^0-9a-f]*'),
  CHECK (unixepoch(created_utc) IS NOT NULL),

  FOREIGN KEY (exchange_id, conversation_id)
    REFERENCES "exchanges"(id, conversation_id)
) STRICT
;;

CREATE TABLE "size_refusals" (
  agent       TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  type        TEXT    NOT NULL CHECK (type IN ('request', 'clarify', 'result')),
  refusals    INTEGER NOT NULL CHECK (refusals > 0),
  worst       INTEGER NOT NULL CHECK (worst > 0),
  total       INTEGER NOT NULL CHECK (total > 0),
  cap         INTEGER NOT NULL CHECK (cap > 0),
  last_utc    TEXT    NOT NULL,

  PRIMARY KEY (agent, type),

  CHECK (worst > cap),
  CHECK (total >= worst)
) STRICT
;;

CREATE UNIQUE INDEX ux_exchange_conversation ON exchanges(id, conversation_id)
;;

CREATE UNIQUE INDEX ux_open_request
  ON exchanges(requester, receiver, request_hash) WHERE state = 'OPEN'
;;

CREATE UNIQUE INDEX ux_message_dedup
  ON messages(content_hash, dedup_bucket)
;;

CREATE TABLE conversation_cycles (
  exchange_id     INTEGER PRIMARY KEY REFERENCES exchanges(id),
  conversation_id TEXT    NOT NULL REFERENCES conversations(id),
  path            TEXT    NOT NULL CHECK (length(path) BETWEEN 1 AND 4000),
  recorded_utc    TEXT    NOT NULL
) STRICT
;;

CREATE TABLE result_items (
  message_id      INTEGER NOT NULL,
  exchange_id     INTEGER NOT NULL REFERENCES exchanges(id),
  item            TEXT    NOT NULL CHECK (length(item) BETWEEN 1 AND 64),
  outcome         TEXT    NOT NULL
                  CHECK (outcome IN ('done', 'answered', 'acknowledged', 'blocked', 'closed')),
  blocker         TEXT    CHECK (blocker IN ('missing_information', 'conflict', 'permission')),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  to_agent        TEXT    NOT NULL REFERENCES agents(name),
  responder_model TEXT    NOT NULL,
  created_utc     TEXT    NOT NULL,

  PRIMARY KEY (message_id, item),

  CHECK ((outcome = 'blocked') = (blocker IS NOT NULL))
) STRICT
;;

CREATE TABLE result_sources (
  message_id      INTEGER NOT NULL,
  exchange_id     INTEGER NOT NULL REFERENCES exchanges(id),
  item            TEXT    NOT NULL CHECK (length(item) BETWEEN 1 AND 64),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  source          TEXT    NOT NULL REFERENCES agents(name),
  created_utc     TEXT    NOT NULL,

  PRIMARY KEY (message_id, item, source),

  CHECK (source <> from_agent)
) STRICT
;;

CREATE INDEX ix_result_sources_source ON result_sources(from_agent, source)
;;

CREATE TRIGGER tr_open_pair_cap BEFORE INSERT ON exchanges
WHEN NEW.state = 'OPEN' AND NEW.open_pair_cap < 50
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND requester = NEW.requester AND receiver = NEW.receiver)
         >= NEW.open_pair_cap;
END
;;

CREATE TRIGGER tr_closed_exchange_stays_closed BEFORE UPDATE OF state ON exchanges
WHEN OLD.state = 'CLOSED' AND NEW.state <> 'CLOSED'
BEGIN
  SELECT RAISE(ABORT, 'a closed exchange stays closed');
END
;;

CREATE TRIGGER tr_an_outcome_has_its_result BEFORE UPDATE OF outcome ON exchanges
WHEN NEW.outcome IS NOT NULL AND OLD.outcome IS NULL
BEGIN
  SELECT RAISE(ABORT, 'an exchange closes on a result from its receiver')
   WHERE NEW.outcome <> 'closed'
     AND NOT EXISTS (SELECT 1 FROM messages
                      WHERE exchange_id = NEW.id AND type = 'result'
                        AND from_agent = NEW.receiver
                        AND responder_model <> 'parley:broker');

  SELECT RAISE(ABORT, 'a closed exchange carries the broker''s closed result')
   WHERE NEW.outcome = 'closed'
     AND NOT EXISTS (SELECT 1 FROM messages
                      WHERE exchange_id = NEW.id AND type = 'result'
                        AND responder_model = 'parley:broker');
END
;;

CREATE TRIGGER tr_closed_conversation_stays_closed BEFORE INSERT ON exchanges
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END
;;

CREATE TRIGGER tr_result_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'result' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a result carries an outcome for each item')
   WHERE json_type(NEW.payload_json, '$.outcomes') IS NOT 'array'
      OR json_array_length(NEW.payload_json, '$.outcomes') = 0;

  SELECT RAISE(ABORT, 'every outcome names its own item')
   WHERE (SELECT count(*) FROM json_each(NEW.payload_json, '$.outcomes'))
      <> (SELECT count(DISTINCT json_extract(value, '$.id'))
            FROM json_each(NEW.payload_json, '$.outcomes'));

  SELECT RAISE(ABORT, 'an outcome is done, answered, acknowledged, blocked or closed')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE json_extract(value, '$.outcome') IS NULL
                     OR json_extract(value, '$.outcome')
                        NOT IN ('done', 'answered', 'acknowledged', 'blocked', 'closed'));

  SELECT RAISE(ABORT, 'a blocked outcome names its blocker, and no other outcome does')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE (json_extract(value, '$.outcome') = 'blocked')
                     <> (json_extract(value, '$.blocker') IS NOT NULL
                         AND json_extract(value, '$.blocker')
                             IN ('missing_information', 'conflict', 'permission')));

  SELECT RAISE(ABORT, 'a source names a participant that is not registered')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes') o,
                               json_each(o.value, '$.sources') s
                  WHERE s.type <> 'text' OR s.value NOT IN (SELECT name FROM agents));

  SELECT RAISE(ABORT, 'a result does not name its own sender as a source')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes') o,
                               json_each(o.value, '$.sources') s
                  WHERE s.value = NEW.from_agent);

  SELECT RAISE(ABORT, 'only the broker records a closed outcome')
   WHERE NEW.responder_model <> 'parley:broker'
     AND EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE json_extract(value, '$.outcome') = 'closed');

  SELECT RAISE(ABORT, 'the broker records only closed outcomes')
   WHERE NEW.responder_model = 'parley:broker'
     AND EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE json_extract(value, '$.outcome') <> 'closed');
END
;;

CREATE TRIGGER tr_result_recorded AFTER INSERT ON messages
FOR EACH ROW WHEN NEW.type = 'result' AND json_valid(NEW.payload_json)
BEGIN
  INSERT INTO result_items
      (message_id, exchange_id, item, outcome, blocker, from_agent, to_agent, responder_model,
       created_utc)
  SELECT NEW.id, NEW.exchange_id, json_extract(o.value, '$.id'),
         json_extract(o.value, '$.outcome'), json_extract(o.value, '$.blocker'),
         NEW.from_agent, NEW.to_agent, NEW.responder_model, NEW.created_utc
    FROM json_each(NEW.payload_json, '$.outcomes') o;

  INSERT INTO result_sources (message_id, exchange_id, item, from_agent, source, created_utc)
  SELECT DISTINCT NEW.id, NEW.exchange_id, json_extract(o.value, '$.id'), NEW.from_agent,
         s.value, NEW.created_utc
    FROM json_each(NEW.payload_json, '$.outcomes') o,
         json_each(o.value, '$.sources') s;
END
;;

CREATE TABLE conversation_folders (
  conversation_id TEXT PRIMARY KEY REFERENCES conversations(id),
  -- Unique ignoring case, because Windows and macOS hold 'Pricing' and 'pricing' as one folder.
  -- NOCASE folds only ASCII, which is enough here: an identifier and its suffix are ASCII.
  folder          TEXT NOT NULL COLLATE NOCASE UNIQUE
                  CHECK (length(folder) BETWEEN 1 AND 72
                         AND folder NOT GLOB '*[^a-zA-Z0-9._~-]*'),
  created_utc     TEXT NOT NULL,

  -- The identifier itself, or the identifier and a suffix of 2 or more. '~' cannot occur in an
  -- identifier, so a suffixed folder can never be another conversation's own name.
  CHECK (folder = conversation_id COLLATE BINARY
         OR (substr(folder, 1, length(conversation_id) + 1) = conversation_id || '~'
             AND substr(folder, length(conversation_id) + 2) NOT GLOB '*[^0-9]*'
             AND substr(folder, length(conversation_id) + 2) NOT GLOB '0*'
             AND CAST(substr(folder, length(conversation_id) + 2) AS INTEGER) >= 2))
) STRICT
;;

CREATE TRIGGER tr_conversation_folder_is_fixed BEFORE UPDATE ON conversation_folders
BEGIN
  SELECT RAISE(ABORT, 'a conversation''s folder keeps its name');
END
;;

CREATE TABLE file_fetches (
  agent           TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  name_key        TEXT NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  name            TEXT NOT NULL,
  sha256          TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  fetched_utc     TEXT NOT NULL,

  PRIMARY KEY (agent, conversation_id, name_key)
) STRICT
;;

CREATE TABLE file_notices (
  agent           TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id TEXT    NOT NULL,
  name_key        TEXT    NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  name            TEXT    NOT NULL,
  sha256_had      TEXT    NOT NULL
                  CHECK (length(sha256_had) = 64 AND sha256_had NOT GLOB '*[^0-9a-f]*'),
  -- NULL when the file is gone, and then so is its size.
  sha256_now      TEXT    CHECK (sha256_now IS NULL
                                 OR (length(sha256_now) = 64 AND sha256_now NOT GLOB '*[^0-9a-f]*')),
  size_bytes      INTEGER CHECK (size_bytes IS NULL OR size_bytes >= 0),
  changed_utc     TEXT    NOT NULL,

  -- At most one per agent per file, carrying the latest state.
  PRIMARY KEY (agent, conversation_id, name_key),

  -- A notice that nothing changed cannot exist: a change back to what was fetched deletes the row.
  CHECK (sha256_now IS NULL OR sha256_now <> sha256_had),
  CHECK ((sha256_now IS NULL) = (size_bytes IS NULL))
) STRICT
;;

CREATE INDEX ix_file_notices_collection ON file_notices(agent, changed_utc)
;;

CREATE TABLE file_placements (
  agent      TEXT    NOT NULL PRIMARY KEY,
  placements INTEGER NOT NULL CHECK (placements > 0),
  bytes      INTEGER NOT NULL CHECK (bytes >= 0),
  last_utc   TEXT    NOT NULL
) STRICT
;;

CREATE TABLE "shared_files" (
  conversation_id TEXT    NOT NULL REFERENCES conversation_folders(conversation_id),
  -- The name in upper case. Every name is ASCII, so SQLite's upper() is the key, and every key on a
  -- file is this: 'Readme.md' and 'README.md' are one row, as they are one file on the disks of
  -- Windows and macOS.
  name_key        TEXT    NOT NULL CHECK (name_key = upper(name)),
  -- As placed, after renaming. Made of letters A to Z, digits, '.', '_' and '-', which every disk on
  -- the three platforms accepts and which carries no text of the placer's beyond a word, ending .md
  -- or .txt so that nothing placed has a handler that runs a program (§13). The folder, a separator
  -- and the name together are bounded by tr_shared_file_path_fits.
  name            TEXT    NOT NULL
                  CHECK (length(name) BETWEEN 4 AND 254
                         AND (lower(name) GLOB '*.md' OR lower(name) GLOB '*.txt')
                         AND name NOT GLOB '*[^A-Za-z0-9._-]*'
                         AND name NOT GLOB '.*'
                         AND upper(substr(name, 1, instr(name, '.') - 1)) NOT IN
                             ('CON', 'PRN', 'AUX', 'NUL',
                              'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
                              'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')),
  size_bytes      INTEGER NOT NULL CHECK (size_bytes >= 0),
  -- The broker's SHA-256 of the stored bytes, never the placer's.
  sha256          TEXT    NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  -- An agent, or NULL for a file the person put there. Text rather than a key: purge deletes these
  -- rows by name and reports them (§14), where a key would cascade silently or refuse the purge.
  placed_by       TEXT,
  placed_utc      TEXT    NOT NULL,
  changed_utc     TEXT    NOT NULL,
  -- The file's last-write time when it was last reconciled, so a change made outside Parley is
  -- noticed on the next list or fetch without hashing every file again.
  modified_ticks  INTEGER NOT NULL,

  PRIMARY KEY (conversation_id, name_key)
) STRICT
;;

CREATE TRIGGER tr_placer_is_registered BEFORE INSERT ON shared_files
WHEN NEW.placed_by IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'a file is placed by a registered agent or by the person')
   WHERE NOT EXISTS (SELECT 1 FROM agents WHERE name = NEW.placed_by);
END
;;

CREATE TRIGGER tr_shared_file_stays_put
BEFORE UPDATE OF conversation_id, placed_by, placed_utc ON shared_files
WHEN NEW.conversation_id IS NOT OLD.conversation_id
  OR NEW.placed_by IS NOT OLD.placed_by
  OR NEW.placed_utc IS NOT OLD.placed_utc
BEGIN
  SELECT RAISE(ABORT, 'a file keeps its conversation, its placer and when it was placed');
END
;;

CREATE TRIGGER tr_shared_file_path_fits BEFORE INSERT ON shared_files
BEGIN
  SELECT RAISE(ABORT, 'a file''s path inside the location is at most 255 characters')
   WHERE (SELECT length(folder) FROM conversation_folders
           WHERE conversation_id = NEW.conversation_id) + 1 + length(NEW.name) > 255;
END
;;

CREATE TRIGGER tr_shared_file_path_fits_on_rename BEFORE UPDATE OF name ON shared_files
BEGIN
  SELECT RAISE(ABORT, 'a file''s path inside the location is at most 255 characters')
   WHERE (SELECT length(folder) FROM conversation_folders
           WHERE conversation_id = NEW.conversation_id) + 1 + length(NEW.name) > 255;
END
;;

CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a request carries an ask')
   WHERE json_type(NEW.payload_json, '$.ask') IS NOT 'text'
      OR length(trim(json_extract(NEW.payload_json, '$.ask'),
                     char(9, 10, 11, 12, 13, 32, 133, 160, 5760,
                          8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202,
                          8232, 8233, 8239, 8287, 12288))) = 0;
END
;;
