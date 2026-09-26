# Data model: What an agent owns, asked rather than assumed

**Schema 12**, by `012_ownership_is_asked.sql`. Two changes: `agents.owns` loses its length bound and
keeps everything else, and one new table records what a person accepted and where it came from.

Nothing here is a wire change to how a message is addressed, and nothing here is written by an agent.

---

## `agents.owns` — rebuilt, with one clause moved

### What changes

```
-  CHECK (owns IS NULL
-         OR (length(owns) <= 100
+  CHECK (owns IS NULL
+         OR (length(owns) <= 2147483647
```

**That is the whole of the change to the clause**, and the rest of the CHECK — the character class of
FR-022 and the embedded-NUL clause after it — is reproduced **verbatim**. It was derived by sweeping the
whole of Unicode against `OwnershipStatement` and `OwnershipTests` re-runs that sweep, so a
hand-retyped class is a class that has drifted. The migration copies the live text and rewrites one
number in it.

`2147483647` is how 006 and 007 spell *off*. Zero was not available to either of them and is not
available here: a bound of zero would refuse every statement. At `2147483647` the clause never binds
and a deployment that wants a bound writes one, still in storage, still below anything that could route
around it (Principle V).

### Why the table is rebuilt rather than altered

SQLite cannot alter a CHECK, `ALTER TABLE … DROP COLUMN owns` is refused because `owns` appears in one,
and a trigger can refuse but never permit. `research.md` R1 has each probe and what it printed.

### The rebuild, in the order it must happen

```
PRAGMA foreign_keys = OFF;          -- outside a transaction, or this is a no-op

DROP TRIGGER tr_agent_declares_its_machine;   -- on agents
DROP TRIGGER tr_result_is_well_formed;        -- on messages
DROP TRIGGER tr_placer_is_registered;         -- on shared_files

CREATE TABLE agents_new ( … ) STRICT;         -- 001's definition, one number changed
INSERT INTO agents_new (name, adapter, tier, workspace, session_ref,
                        adapter_config, owns, connected, changed_utc, status)
  SELECT  name, adapter, tier, workspace, session_ref,
          adapter_config, owns, connected, changed_utc, status
  FROM agents;                                -- named columns: role and machine are GENERATED
DROP TABLE agents;
ALTER TABLE agents_new RENAME TO agents;

-- every trigger back, verbatim
PRAGMA foreign_keys = ON;
PRAGMA foreign_key_check;                     -- must return no rows
PRAGMA integrity_check;                       -- must return ok
```

**Three properties of that sequence are load-bearing and each was probed:**

| Property | Why it is there |
|---|---|
| Outside a transaction | `PRAGMA foreign_keys = OFF` is accepted and ignored inside one. Probed: it still reads `1` and the DROP still fails |
| Triggers down first | The RENAME validates every trigger in the schema. With them standing it fails: `error in trigger tr_result_is_well_formed: no such table: main.agents` |
| Named columns | `role` and `machine` are `GENERATED ALWAYS … STORED` and cannot be inserted |

**The cost, stated where somebody will read it before running it.** This is the first migration here
that runs outside a transaction, so **a failure part-way leaves the store with no `agents` table and no
rollback**. `SchemaMigrator` gains an opt-out for this one named migration, not a general capability,
and the store is copied beside itself before it starts.

---

## `agent_ownership_accepted` — new

One row per agent: the last statement a person accepted, and the evidence of how it was arrived at.

```sql
CREATE TABLE agent_ownership_accepted (
  agent            TEXT    NOT NULL PRIMARY KEY
                           REFERENCES agents(name) ON DELETE CASCADE,
  statement        TEXT    NOT NULL,
  -- Which of the three FR-015 admits. A person's characters are never attributed to a model.
  source           TEXT    NOT NULL CHECK (source IN ('proposed', 'edited', 'composed')),
  -- The length of the AGENT'S OWN message, or NULL where there was no proposal. FR-026's
  -- evidence, and never a length a person wrote: 0.29.0's second gate finding was exactly
  -- that, a retry writing into size_refusals a length no agent composed.
  proposed_length  INTEGER          CHECK (proposed_length IS NULL OR proposed_length >= 0),
  -- The discussion, so FR-011 is answerable from the agent rather than from somebody's memory.
  conversation_id  TEXT             REFERENCES conversations(id),
  accepted_utc     TEXT    NOT NULL,

  -- The same rule the registry applies, applied here too, because this text is shown on a
  -- screen beside other agents' lines before it is ever written to a file. One implementation
  -- would be better and is not available: this is SQL and that one is C#. OwnershipTests holds
  -- them to each other.
  CHECK (length(statement) >= 1),
  CHECK (source <> 'composed' OR proposed_length IS NULL),
  CHECK (source =  'composed' OR conversation_id IS NOT NULL)
) STRICT;
```

### What each column is for

| Column | Fact |
|---|---|
| `agent` | Whose line. Cascades on a purge, so §10.2's purge takes the record with the agent, as `size_refusals` already does |
| `statement` | What the person agreed to. **Not what peers read** — that is `agents.owns` |
| `source` | `proposed` (the agent's words, untouched), `edited` (the agent's proposal, changed by the person), `composed` (no ask; the person wrote it) |
| `proposed_length` | FR-026. The agent's own message length, `NULL` when `composed` |
| `conversation_id` | FR-011. `NULL` only when `composed`, because there was no discussion |
| `accepted_utc` | When. Local rendering is each surface's, as 015 settled |

### The two facts a surface compares, and why there is no third

**FR-020 is answered by a comparison, not by a flag.** `agent_ownership_accepted.statement` is what was
agreed; `agents.owns` is what peers read, refreshed from the launch environment when the agent attaches
(§12.3). Where they differ the line is pending, and a surface says so.

A `pending` boolean was rejected: it would be a third fact about the same thing, and it would have to be
cleared by whoever notices the restart. Both halves here are observations, so nothing has to remember to
clear anything and the state cannot be wrong. **This is the shape 0.29.1 argued for and 0.25.3 argued
for before it** — who could see a message is fixed when it is written, whether it was collected is a
fact about the deployment now, and collapsing two questions into one is what made both unanswerable.

### What is deliberately not recorded

**A proposal nobody accepts.** The only act that reaches the broker is the acceptance, because an ask is
an ordinary message and the broker cannot tell one from any other (`research.md` R2). So FR-026 is met
for accepted lines and not for abandoned ones, and that limit is written here rather than discovered.
Recording every proposal would mean telling the broker which messages are proposals, which is the thing
R2 refuses to do.

---

## What does not change

- **`OwnershipStatement` in `Parley.Core`** keeps its rule and loses its number. `MaxCharacters` becomes
  the same sentinel the schema uses, named rather than spelled, on 0.25.5's precedent — `parley` must
  never print the integer that means *no cap*, which would tell somebody a bound was set at a value
  nobody chose. Core still does no I/O and takes no package dependency.
- **The registry is still filled from the launch environment on attach**, and no operation accepts an
  `owns` line from the agent it describes (FR-018, FR-029, Principle III).
- **The field is still advisory and still optional.** Nothing routes on it, nothing is refused because
  of it, two agents may claim one ground, and an agent with no line is listed without one.
- **`conversations`, `exchanges` and `messages` are untouched.** The discussion is an ordinary
  conversation and this feature adds nothing to it.
