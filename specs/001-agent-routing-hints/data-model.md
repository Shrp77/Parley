# Phase 1: the column, the value, and what carries it

## The column

`agents.owns TEXT` — nullable, no default, no index. It is read whenever the whole row is read
and never queried on, because nothing routes by it.

```sql
CHECK (owns IS NULL
       OR (length(owns) <= 100
           AND owns NOT GLOB '*[' || char(1) || '-' || char(31) || ']*'
           AND owns NOT GLOB '*' || char(127) || '*'
           AND length(owns) = length(owns || '.') - 1)),
```

Four clauses, and the fourth is the one that had to be found rather than written. `char(0)` is
the **empty string** in SQLite, so a GLOB built from it matches everything and would refuse
every statement — the range from `char(1)` therefore cannot express NUL. `length()` stops at a
NUL while concatenation does not, so a value whose length is unchanged by appending a character
is one hiding bytes behind a terminator. The first three clauses passed a NUL through until a
probe put one in.

Nothing is deployed anywhere, so this goes into the initial schema and there is no migration.

## The value

`Parley.Core.OwnershipStatement` — a static rule, no instance, no I/O, so `Parley.Core` keeps
both of its constraints.

| Member | For |
|---|---|
| `MaxCharacters` | 100. Measured; see research.md |
| `IsValid(statement, out error)` | Whether it may be recorded, and why not. Returns rather than throws because both callers want the message |
| `Read(supplied)` | Trims, and collapses blank to absent, so an empty variable and an unset one mean the same |

Control characters are **refused, not stripped**. Stripping the newline would admit the intent
and repair the symptom; refusing tells whoever wrote it that the field is one line.

## Who writes it

```
PARLEY_OWNS  ──►  ServerOptions.Read      refuses an invalid value at start-up (EX_CONFIG)
                        │
                        ▼
             BrokerSession.RegisterAsync
                        │
        ┌───────────────┴───────────────┐
        ▼                               ▼
RegisterIfAbsentAsync            RefreshOwnershipAsync
 (first launch only)              (every launch; skips retired rows)
```

Validated twice, and only one of them is the enforcement. The schema is the guard no caller can
route around; the start-up check exists so a misconfiguration reaches the person who wrote the
launch configuration rather than the model, which can do nothing about it.

**No path writes it from a call.** `ParleyTools` has no parameter named for it, on any
operation, and a test enumerates the surface to keep it that way.

## Who reads it

| Reader | Sees |
|---|---|
| `AgentSummary.Owns` → `parley_agents` | Every peer. The key is **omitted** when absent, so absence reads as unknown rather than as a claim to own nothing |
| `AgentRecord.Owns` → `parley agents` | The operator, beside the workspace, with an explicit `(unset — …)` when there is none |

The peer-facing entry is now `name`, `adapter`, `tier`, `status`, `owns` — and no more. The
workspace, the session handle and the adapter configuration stay withheld, asserted directly
against a rendered listing rather than against the type.
