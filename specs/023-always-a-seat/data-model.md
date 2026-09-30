# Data model: The person always has a seat

No new table. `agents` changes shape in one column and gains rules. One Core type is added, and two Core enums
gain values.

## `agents` — the two participants the broker provides

Both are written by the broker when it opens a project's store, never by an attach.

| Column | Person seat | Facilitator (022, renamed) |
|---|---|---|
| `name` | `human-user@<project>` | `parley-facilitator@<project>` |
| `role` | `human-user` | `parley-facilitator` |
| `machine` | **NULL** | **NULL** |
| `adapter` | `parley-console` | `parley-facilitator` |
| `tier` | `pull` | `pull` |
| `status` | `active`, always | `active`, always |
| `connected` | surfaces attached and active: *here* above zero, *away* at zero | always 0 |
| `workspace`, `owns`, `session_ref`, `adapter_config` | NULL | NULL |

Each is found on every start by its role (person) or adapter (facilitator), so an existing row is reused.

### The machine column is empty for these two

The owner's answer to `/speckit-analyze` issue 2: the part after `@` is the project for these two, and they have
**no machine at all**. `machine` is a generated column with a foreign key to `nodes`, and the key is why a machine
entry could not simply be skipped: the row would be refused. A NULL machine is allowed by the key and says what is
true.

```sql
  machine         TEXT
                  GENERATED ALWAYS AS (
                    CASE WHEN substr(name, 1, instr(name, '@') - 1) IN ('human-user', 'parley-facilitator')
                         THEN NULL
                         ELSE substr(name, instr(name, '@') + 1)
                    END) STORED
                  REFERENCES nodes(machine),
  ...
  -- Only the two participants the broker provides have no machine.
  CHECK (machine IS NOT NULL OR role IN ('human-user', 'parley-facilitator')),
  -- Their second half is a project, so it is held to a project's grammar. The machine CHECK above
  -- passes on NULL, so without this the part after '@' would be checked by nothing.
  CHECK (machine IS NOT NULL
         OR (length(substr(name, instr(name, '@') + 1)) BETWEEN 1 AND 32
             AND substr(name, instr(name, '@') + 1) GLOB '[a-z]*'
             AND substr(name, instr(name, '@') + 1) NOT GLOB '*[^a-z0-9-]*')),
  -- Neither owns a repository, so neither has an ownership line. Held here rather than in code because
  -- every attach refreshes `owns` from the launch (RefreshOwnershipAsync), and something attaching under
  -- the person's name would otherwise put its own line in every agent's roster as the person's.
  CHECK (owns IS NULL OR role NOT IN ('human-user', 'parley-facilitator')),
```

`tr_agent_declares_its_machine` gains `WHEN` the role is not one of the two, so no node row is written for them.

**Probed on SQLite 3.50.4** (2026-09-30): both participants admitted with `machine` NULL; no `nodes` row for the
project; an ordinary agent still declares its machine; `LEFT JOIN nodes` gives them `connected = 0`;
`AgentsOnAsync('alpha')` finds neither. The microsoft.data.sqlite bundle's version is checked in T010, since
generated columns need 3.31.

**What reads `machine`**: the two `LEFT JOIN nodes` in `AgentRepository` (NULL joins nothing, which is correct)
and `NodeRepository.AgentsOnAsync` (NULL matches no machine, so no node is ever told it hosts either). In C#,
`AgentName.Machine` still returns the part after `@` from the string, so every reader of it that could meet one
of these two is audited (tasks.md T013a).

## Rules in storage (`001_initial.sql`)

### Phase 2 — keyed on the role, safe while typed seats still exist

```sql
CREATE TRIGGER tr_person_is_never_retired
BEFORE UPDATE OF status ON agents
WHEN substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'human-user' AND NEW.status <> 'active'
BEGIN
  SELECT RAISE(ABORT, 'the person seat cannot be retired');
END;

CREATE TRIGGER tr_person_is_never_purged
BEFORE DELETE ON agents
WHEN substr(OLD.name, 1, instr(OLD.name, '@') - 1) = 'human-user'
BEGIN
  SELECT RAISE(ABORT, 'the person seat cannot be purged');
END;

CREATE UNIQUE INDEX ux_one_person ON agents(role) WHERE role = 'human-user';
```

**Keyed on the role, not the adapter, and that is load-bearing for the order of work.** Until the pair below
lands, typed-name seats still carry `parley-console`. A rule keyed on the adapter would make the broker's own
person row collide with the first typed seat and turn every seat test red at once.

### Phase 7 — the pair tying role to adapter

```sql
CREATE TRIGGER tr_person_role_is_reserved
BEFORE INSERT ON agents
WHEN (substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'human-user')
     <> (NEW.adapter = 'parley-console')
BEGIN
  SELECT RAISE(ABORT, 'the role human-user and the seat adapter belong to the person seat alone');
END;

CREATE TRIGGER tr_person_role_is_reserved_on_update
BEFORE UPDATE OF name, adapter ON agents
WHEN (substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'human-user')
     <> (NEW.adapter = 'parley-console')
BEGIN
  SELECT RAISE(ABORT, 'the role human-user and the seat adapter belong to the person seat alone');
END;
```

**What these cannot see.** Registration is `INSERT … ON CONFLICT(name) DO NOTHING`. The person row exists before
anything attaches, so an attach under its name writes no new row and fires neither trigger. It does raise the
person's `connected` count, and its `owns` refresh is refused by the check above. The refusal at attach is code,
and trusts the adapter the caller reports (spec: *What the reservation does not do*).

**Each rule is probed failing first** (Principle V): insert `human-user@x` as `claude-code`; insert `orders@x` as
`parley-console`; update the person's adapter; retire it; purge it; insert a second person; insert
`orders@alpha` with a NULL-machine trick (impossible by generation — asserted anyway); insert
`human-user@Alpha!`. All refused; removing each rule in turn makes exactly its probe pass.

## Core

- **`PersonSeat`** (new, `src/Parley.Core/PersonSeat.cs`): `Role = "human-user"`, `Adapter = "parley-console"`
  (replacing `Seats.Adapter`), `On(ProjectName)`, `Is(AgentName)`, `IsReserved(string role)`, and the refusal
  sentence. The same shape as `Facilitation`.
- **`Facilitation.On`** takes a `ProjectName` instead of a machine.
- **`AgentPresence`** gains `Here = 3` and `Away = 4`. `AgentLiveness.Read(adapter, agentConnected,
  nodeConnected)` returns them for the person seat and never for anything else, and returns `Running` for the
  facilitator, which §14 calls present whenever its project is served. With no machine, it would otherwise read
  `Stranded`.
- **`WakeOutcome`** gains `WithThePerson = 4`.

## Wire (`proto/parley.proto`)

- `WakeOutcome`: `WAKE_OUTCOME_WITH_THE_PERSON = 5`.
- The presence enum: `HERE` and `AWAY`, numbered after the existing values.

Both are additions. SPEC.md §18 records them as a further amendment to 0.7, which is unreleased.

## State

```text
           surface attaches (open, or first activity after away)
   away  ─────────────────────────────────────────────────────────▶  here
         ◀─────────────────────────────────────────────────────────
           last surface drops its stream (closed, or idle 20 min)
```

Neither transition touches the row's existence, its mail, or what a sender is told.
