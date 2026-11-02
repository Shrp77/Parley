# Data model: Soft message limit

## message_lengths (new)

| Column | Type | Notes |
|---|---|---|
| message_id | INTEGER PRIMARY KEY | The message's id. No key to `messages`: the record outlives it. |
| conversation_id | TEXT NOT NULL | Plain text, for the same reason. |
| position | INTEGER NOT NULL, > 0 | Its order in the conversation. |
| type | TEXT NOT NULL | request, clarify or result |
| from_agent | TEXT NOT NULL | References agents, on delete cascade |
| to_agent | TEXT NOT NULL | References agents, on delete cascade |
| characters | INTEGER NOT NULL, >= 0 | `length(payload_json)` |
| created_utc | TEXT NOT NULL | |

Never holds text. Filled by a trigger on insert into `messages`, and by migration 005 for what a store already holds. Survives pruning. Goes with a purged agent or a destroyed project.

## messages, ownership_questions (changed)

`length(payload_json) <= 512000`; `answer` and `flattened` the same. Nothing else changes.

## size_refusals (unchanged)

Existing rows keep their evidence. A new refusal sets `cap` to 512,000; `worst > cap` still holds, because `worst` takes the larger value.

## MessageLengthReport

Totals (count, median, longest, over the soft limit), per type; per agent (sent, received, longest, over the soft limit); per position band (count, median, longest).
