# Data model: Roadmaps

> The store rule below was replaced on 2026-10-24: live stores are kept. Schema 1 is frozen as this feature left it, and every later schema change is a migration (CLAUDE.md, SPEC.md §9.2).

Schema 1, edited in place in `001_initial.sql` and mirrored in SPEC.md §9.2. A store built before this edit lacks the tables until it is reset. That is the owner's standing rule (no compatibility before release, see 020 and 023). Tables are `STRICT`. Per-project store, so no project column.

## Entities

### roadmap_proposals
| Column | Notes |
|---|---|
| id | integer PK |
| proposer | FK `agents(name)`. Trigger: must not be the person or facilitator role. |
| source_conversation | nullable FK `conversations(id)`: where the agent was when it proposed |
| target_title | nullable text: the title of the roadmap it proposes to revise. A hint only; the person resolves it at confirmation. |
| title | text, not blank (no length bound; character class applies) |
| size | integer, characters in the whole proposal, kept as evidence for a later bound |
| state | `waiting`, `confirmed`, `declined`, `superseded` |
| created_utc, decided_utc | decided_utc set iff state is not `waiting` (CHECK with `IS`, not `=`, since 025 found `=` lets NULL through) |

### roadmap_proposal_items
`(proposal_id, position)` PK; `text` not blank; `intended_agent` nullable FK `agents(name)` ON DELETE SET NULL (a purge leaves the item, meant for nobody); `needs` is a list of earlier positions, held in `roadmap_proposal_needs(proposal_id, position, needs_position)` with CHECK `needs_position < position` (a dependency may only point backwards, which makes cycles impossible by construction and removes the need for a cycle check).

### roadmaps
`id` integer PK; `number` unique small integer per store (the `rm<number>` of item conversation names); `title`; `origin` (`proposed`, `edited`); `origin_proposal` nullable FK; `proposer` kept as text so a purged agent's name survives; `revision` integer, starts at 1; `created_utc`; `updated_utc`.

### roadmap_items
`id` integer PK; `roadmap_id` FK; `number` integer, unique within the roadmap and never reused; `position` integer; `text` not blank; `intended_agent` nullable FK `agents(name)` ON DELETE SET NULL; `added_rev`; `removed_rev` nullable (CHECK `removed_rev IS NULL OR removed_rev > added_rev`); `edited_utc` nullable. Unique `(roadmap_id, number)`. Items are never deleted (R7). Unique `(roadmap_id, position)` among live items, as a partial unique index where `removed_rev IS NULL`.

### roadmap_item_needs
`(item_id, needs_item_id)`; trigger: both items belong to the same roadmap, neither removed, and the needed item's position is earlier.

### roadmap_item_conversations
`item_id` PK FK; `conversation_id` text unique, legal conversation id (the same character rule `conversations.id` carries); derived as `rm<roadmaps.number>-<items.number>` at confirmation, or the first free name after it (`-2`, `-3`, ...) when a conversation, a destroyed conversation or another reservation already holds that one. **No FK to `conversations`**: the row is a reservation made before the conversation exists. A conversation exists once a message is in it.
Trigger on `messages`: if the message's conversation is reserved here and the conversation has no earlier message, the sender must be the person (`human-user@…`). Refused otherwise.

### roadmap_notices
`id` PK; `agent` FK; `proposal_id` FK; `outcome` (`confirmed`, `declined`, `superseded`); `created_utc`; `delivered_utc` nullable (same pairing rule the other notices carry). Drained with the inbox; wakes nothing.

## Derived, never stored

Item state, the holding agent, the blocker, the roadmap's next item, the count of items waiting on the person. All from `ItemStates` (R4). The roadmap table carries no status column and a test asserts the schema has none.

## State rules

Proposal: `waiting` to `confirmed | declined | superseded`, once, never back. A confirmed proposal's roadmap or revision commits in the same transaction. Roadmap: revision rises by one on each edit or confirmed revision. An item: live, then removed (never live again; a person re-adding the same text makes a new item with a new number, so its conversation is new too).

## Item facts returned by `Roadmap`

Per live item: conversation exists; count of open exchanges; whether every open request was collected; how many closed exchanges ended complete or with no Result; how many agents' latest closed exchange on the person's request ended blocked, and the blocker one of those named; who closed the conversation (consensus or person); whether the conversation was destroyed; whether the item's text was edited after its first request was sent, from `edited_utc` (FR-014a). These are facts, not states.

## Retention and destruction

Roadmaps follow the conversations they own: destroying a conversation (the existing operation) leaves the item, whose state then reads *closed* (never *not started*) with the conversation recorded as destroyed (`destroyed_conversations` already exists). Destroying a project's store removes everything, as now. Retention (§9.4) never removes a roadmap, an item, or a proposal that is still waiting. Proposals decided more than the retention horizon ago go with the rest; the roadmap that came of them keeps its `origin_proposal` as plain text so removal does not lose provenance.

## Export

`Export` of a conversation names the item and roadmap it belongs to (a line in the header, in the export's own words, never from the payload). A project export includes roadmaps, their items, revisions and unresolved proposals (FR-029). `ConversationSummary` gains the item reference so lists can mark item conversations (FR-026).
