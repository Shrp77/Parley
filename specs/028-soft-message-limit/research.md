# Research: Soft message limit

## How a large message reaches an agent

- **Decision**: Deliver it through `parley_inbox` as any other message. Add no paging tool.
- **Rationale**: Claude Code's documentation (code.claude.com/docs/en/mcp, read 2026-11-02) says a successful MCP result over 25,000 tokens, or over 50,000 characters for a tool that sets no `anthropic/maxResultSizeChars`, is saved to a file and replaced by a message naming the path. The agent reads it when it needs it. One collection already carries about 64,000 characters and at least one message, so a 512,000-character message arrives alone.
- **Not verified**: Nothing has yet run this through a real client. The documentation does not say which limit applies first, and a collection of about 64,000 characters is over the 50,000-character one.
- **Alternatives**: A file written by the sender (more steps for every long report); a paging tool (a second way to read a message).

## Rebuild rather than edit the schema

- **Decision**: Rebuild `messages` and `ownership_questions` in migration 005, following 002's pattern.
- **Rationale**: A SQLite CHECK cannot be altered. SQLite documents `writable_schema` for this, which avoids copying rows, but one mistake corrupts a live store, and rebuilding is what the project already does.
- **Traps, each tested**:
  1. `AUTOINCREMENT`: the new table's counter would start from the highest surviving id, and retention removes messages, so an id could be reused. The old `sqlite_sequence` value is copied across.
  2. Foreign keys stay on inside a migration. Nothing references `messages` or `ownership_questions` by key, so dropping them cascades nowhere.
  3. `ALTER TABLE ... RENAME` validates every trigger in the schema. `tr_an_outcome_has_its_result` on `exchanges` names `messages`, so it is dropped before and recreated after.
  4. Dropping a table drops its triggers and indexes. Each is recreated from its latest text.
  5. The copy must not fire the moved triggers, so they are created after it.
  6. `dedup_bucket` is generated, so it is not copied.

## Recording lengths in a trigger

- **Decision**: `AFTER INSERT ON messages`, in storage.
- **Rationale**: No path that stores a message can forget it: agents, the person, the facilitator and the broker's own closed Results all pass here.
- **Position**: One more than the records already held for the conversation, so pruning does not renumber it. An index on `(conversation_id, position)` keeps it cheap.
- **Keys**: `from_agent` and `to_agent` reference `agents(name)` `ON DELETE CASCADE`. A purge removes them, and rename follows them because rename walks every reference to `agents`. `conversation_id` is plain text: a record outlives its conversation.

## The report

- **Decision**: Compute on read with SQL. Median by ordered offset. Position bands: 1 to 5, 6 to 20, 21 to 100, 101 and later.
- **Rationale**: Read rarely, by a person. No summary table to keep right.

## Soft-limit note

- **Decision**: The adapter adds a `long_message` sentence to a successful reply when the payload passed 16,000.
- **Rationale**: The adapter already measures the payload to apply the cap. Nothing else needs to know.

## A person's words

- **Finding**: The conversation page and the terminal keep what was typed, and tests prove it. The web inbox did not: its boxes came back empty after any refused send, so a reply over the limit was lost. Fixed here, and held by reading the page rather than driving it, because no web test console holds a live seat with work owed.
- **Limits on the way**: ASP.NET's form value limit is 4 MB, Kestrel's body limit about 30 MB, gRPC's default receive 4 MB. A 512,000-character message fits.
