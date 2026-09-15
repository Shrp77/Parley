# Data model: Parley file store

Schema 9, migration `src/Parley.Store/Migrations/009_file_store.sql`. File bytes are on disk under the location
(`research.md` R2); these tables hold what is known about them. Every rule marked **(storage)** is a constraint, index or
trigger, per Constitution Principle V, and is written into SPEC.md §9.2 so `Parley.Spec.Tests` executes it.

## On disk

```text
<store path>.files/
├── tax-inclusive-pricing/        one folder per conversation that has files
│   ├── PriceSnapshot.cs.txt
│   └── price-snapshot.md
├── pricing/                      a conversation called "pricing"
├── Pricing~2/                    a conversation called "Pricing": one folder with "pricing" on Windows and macOS (R2)
└── con~2/                        a conversation called "con", a reserved device name on Windows (R2)

<store path>.files-incoming/      placements in progress, beside the location and never inside it (R2);
                                  emptied when the broker starts
```

## `conversation_folders`

One row per conversation that has ever had a folder (`/speckit-analyze` H4).

| Column | Type | Rule |
|---|---|---|
| `conversation_id` | TEXT | primary key; → `conversations(id)`, not cascading: destroy and purge delete it by name and report it, as SPEC.md §14 requires of every reference to a conversation **(storage)** |
| `folder` | TEXT | its id, or its id plus `~N` when unsafe or colliding (R2); unique ignoring case — `COLLATE NOCASE` suffices, because identifiers and the suffix are ASCII **(storage)**; a trigger refuses any update **(storage)** |

- Written in the transaction that places a conversation's first file, and kept when its last file is removed or the
  location is pruned, so the folder name is never recomputed and a file copied into that folder by hand is still traced
  to its conversation. Deleted only with the conversation: destroyed, or purged with an agent that took part in it.
- A folder in the location that no row names belongs to no conversation; its files are ignored. When a folder name is
  chosen, every folder already on disk counts as taken, even one no row names.

## `shared_files`

One row per file in the location.

| Column | Type | Rule |
|---|---|---|
| `conversation_id` | TEXT | → `conversation_folders(conversation_id)`, so every file has a folder; not cascading, for the same reason **(storage)** |
| `name_key` | TEXT | the name case-folded for every letter by `SharedFileName.Key` (research.md R4); every table's key uses it **(storage)** |
| `name` | TEXT | as placed; ending `.md` or `.txt` (case-insensitive); the conversation's `folder` + separator + `name` at most 255 chars (a trigger reading `conversation_folders`) **(storage)**; no control characters or `/ \ : * ? " < > \|`, not `.`/`..`, no leading/trailing space or dot, not a reserved device name **(storage)** |
| `size_bytes` | INTEGER | ≥ 0 **(storage)** |
| `sha256` | TEXT | exactly 64 lowercase hex characters **(storage)** |
| `placed_by` | TEXT NULL | an agent name, or NULL for the person; a non-NULL value must name a registered agent **when inserted** (trigger) and is not a foreign key, because purge deletes the rows explicitly and counts them (research.md R3) |
| `placed_utc` | TEXT | when the file was first placed |
| `changed_utc` | TEXT | when the current content arrived, by any route |
| `modified_ticks` | INTEGER | the file's last-write time as last reconciled (R6) |

- **Key**: `(conversation_id, name_key)` unique **(storage)** — one winner for concurrent placements of a new
  name, and one row per file on case-insensitive filesystems. It is also the order the lists are paged in (R17).
- **A placement may create the conversation** (FR-006): the `conversations` row, its `conversation_folders` row and a
  `conversation_participants` row for the placer are written in the same transaction as the `shared_files` row, so a conversation never exists without the
  participant who started it.
- **Replacement** updates `size_bytes`, `sha256`, `changed_utc`, `modified_ticks`; never `placed_by` or `placed_utc`
  (FR-028). A trigger refuses an update that changes `placed_by` **(storage)**.
- **Rename** (seen by the watcher or the start-up scan) updates `name`, `name_key` and `modified_ticks` in place and keeps
  `placed_by`. It is never a delete and an insert, so `placed_by` and `placed_utc` are untouched (FR-018). Fetchers of the old name get "gone" notices in the same transaction.
- **Person's files**: `placed_by` NULL. Only the operator operations may remove them; an agent's removal is refused
  because NULL is not its name.

## `file_fetches`

That one agent fetched one file, and what it received.

| Column | Type | Rule |
|---|---|---|
| `agent` | TEXT | → `agents(name)` ON DELETE CASCADE **(storage)** |
| `conversation_id` | TEXT | → `conversations(id)`, not cascading (SPEC.md §14) **(storage)** |
| `name` | TEXT | as `shared_files.name` |
| `name_key` | TEXT | as `shared_files.name_key` **(storage)** |
| `sha256` | TEXT | the hash of the content the agent received (64 hex) **(storage)** |
| `fetched_utc` | TEXT | |

- **Key**: `(agent, conversation_id, name_key)` **(storage)** — a later fetch replaces the record (FR-019).
- Written only after the adapter reports the fetched file verified, so a failed fetch records nothing; the broker accepts
  that report only for a fetch it streamed to that agent, with the hash it streamed (FR-019).
- Not a foreign key to `shared_files`: a record must survive its file's removal long enough to raise a "gone" notice,
  and is deleted in the same transaction as the notices are written.

## `file_notices`

At most one waiting notice per agent per file.

| Column | Type | Rule |
|---|---|---|
| `agent` | TEXT | → `agents(name)` ON DELETE CASCADE **(storage)** |
| `conversation_id` | TEXT | no key: survives the conversation being destroyed, so the agent is told |
| `name` | TEXT | |
| `name_key` | TEXT | as `shared_files.name_key` **(storage)** |
| `sha256_had` | TEXT | what the agent fetched |
| `sha256_now` | TEXT NULL | the current hash, or NULL when the file is gone |
| `size_bytes` | INTEGER NULL | NULL when gone |
| `changed_utc` | TEXT | when it last changed or went |

- **Key**: `(agent, conversation_id, name_key)` **(storage)** — FR-022's "at most one".
- **Check**: `sha256_now IS NULL OR sha256_now <> sha256_had` **(storage)** — a notice saying nothing changed cannot
  exist; a change back to the fetched content deletes the row instead.
- **Raised for** every `file_fetches` row of the file whose agent is not retired (FR-020); purged agents have no rows.
- **Collected** at most 200 per `DrainInbox`, oldest `changed_utc` first, and deleted as they are returned (FR-021,
  FR-023); an index on `(agent, changed_utc)` serves that order.

## `file_placements`

| Column | Type | Rule |
|---|---|---|
| `agent` | TEXT | primary key; not a foreign key, so counts survive purge like `size_refusals` does |
| `placements` | INTEGER | ≥ 0 |
| `bytes` | INTEGER | ≥ 0 |
| `last_utc` | TEXT | |

Updated in the transaction that records a placement or a replacement with new content. An identical placement is not
counted.

## State of a shared file

```text
             place (new name)
   (none) ─────────────────────► PRESENT ──── remove / prune / destroy / purge ► (gone: notices raised)
                                   │  ▲
         place by its agent,       │  │  hash unchanged: nothing happens
         edit in folder, rename ───┘  │
         (hash changes: notices) ─────┘
```

While one or more downloads are in progress a PRESENT file is additionally **held**: the stored file is opened without
write sharing, and replacement, removal and prune wait for the hold count to reach zero (R5). The hold is in memory in
`BrokerRuntime`, not in the store: a broker restart ends every download and so every hold.

## Domain types (`Parley.Core`, no I/O)

- `SharedFileName` — validates and normalises a name against R4; used by the adapter, the broker and every surface, so
  what a surface offers and what the store refuses cannot disagree.
- `ContentHash` — a 64-hex SHA-256 value type with parsing and comparison.
- `SharedFile` — conversation, name, size, hash, placer (agent or person), placed and changed instants.
- `FileNotice` — conversation, name, hash had, hash now or gone, size, changed instant.
- `FileLocation` — the broker's machine name and the absolute path asked about.
- `StoreFiles.Files` — the `.files` suffix.
