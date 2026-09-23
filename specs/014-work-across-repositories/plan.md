# 014 — Work across repositories (protocol 0.5)

**What this implements.** SPEC.md was rewritten top down on 2026-09-13 because it defined Parley as a
question-and-answer channel and told every agent, on every delivery, that a peer could not direct its
work. Agents followed that and declined the work they were asked for. The rewritten document is the
specification for this feature; §18's "0.5 — work across repositories" entry lists what a second
implementation will observe. This plan says how the reference implementation gets there. It does not
restate the arguments, which live in the document.

The owner's decisions this rests on, in their words where they gave them:

- Parley exists to enable work to be done across agents in different repositories. Any instruction to
  the contrary is wrong.
- Any agent or person may request work; the owner changes only its own repository.
- An agent declines only for a concrete blocker, and pursues the blocker: the agent who can provide
  missing information, then the seated person, then the originator.
- One ask is one conversation, however far it travels. The broker attaches the conversation.
- Every message is answered — even if only "received". Notify and Ask merge.
- A conversation ends by consensus or by the person. Nothing times out, nothing is ghosted.
- The command line, the terminal and the web UI have full parity.
- No over-engineering.

## Vocabulary

| 0.4 | 0.5 |
|---|---|
| Question, Notify | **Request** (`items` of kind `work` or `question`, required `done_when`) |
| Answer | **Result** (per item: `done`, `answered`, `acknowledged`, `blocked`; `closed` only by the broker) |
| Ghost | gone |
| asker / answerer | requester / receiver |
| `sub_questions`, `consequences` | `items`, `done_when` |
| `resolution` (answered, out_of_scope, unknown, ghosted) | item `outcome`; exchange rolls up to `complete`, `blocked` or `closed` |
| `ORPHANED`, grace period, delivery/working windows, `expires_utc` | gone; two flag thresholds stamped per exchange |
| `is_decision`, `decision_ref` | gone (§17 q3) |
| referral, `redirect_to`, `out_of_scope` | gone; `sources` on a Result item, `blocker` on a `blocked` item |
| would-deadlock refusal | admitted, recorded in `conversation_cycles` |
| `referrals()`, `notifications()` | `results()` and `flags()` |

## Decisions made in planning

1. **Schema 8 rebuilds `exchanges`, `messages` and `size_refusals`.** A CHECK cannot be altered, and
   006/007 recorded why a rebuild inside the migrator's transaction fails on a store carrying traffic:
   dropping a parent under enforced foreign keys. The rebuild here drops the *children first*
   (`message_referrals`, then `messages`), so the parent has no referencing rows when it goes, and
   renames the new tables into place — SQLite rewrites the references in the renamed children. Probed
   against a populated store before it is trusted.
2. **Old payloads are copied verbatim, and new shapes are enforced by triggers rather than CHECKs.** A
   0.4 question lacks `done_when` and would fail a table CHECK during the copy. Rows written under 0.4
   were valid under 0.4; triggers guard every new write. Readers that need item ids fall back to
   `sub_questions` for those rows.
3. **0.4 notifications and ghost notices are not carried into 0.5.** Neither had an exchange, 0.5 has no
   message without one, and synthesising an exchange would record a Result nobody sent. Their counts
   are reported by the migration's caller. Standalone 0.4 exchanges get a conversation named
   `exchange-<id>`, because 0.5 has no exchange outside a conversation.
4. **Consensus closure writes `closed` results too.** §5.0 polls participants holding open exchanges, so
   a conversation can close by consensus with work open. Those exchanges end `closed`, attributed to
   `parley:broker`, saying the participants closed it. §5.3's table is widened to say so.
5. **A person may always start a new conversation.** §2.10: "Only a person, or an agent holding no
   request, starts a new conversation." A person is a seat, recognised by the console adapter.
6. **Coalesced waking applies to every message type.** §5.4's bound is per receiver across every sender.
7. **Restart waking happens when a node joins.** That is the first moment an agent can be started. Under
   `always` its agents that owe open work are woken, subject to the resume gate.
8. **No role group-size cap.** §17 q12 says instrument first; 32 was a guess that refuses work.
9. **A delivered Result carries the original Request** in a second block of the same envelope (§4.2).

## Order

Each phase ends with a green build and every test project passing when run directly.

1. **Protocol.** Core, Store (migration 008), Broker, proto, Server, MCP, Headless; SPEC §9 schema and
   prose, §10 tables, header 0.5. Client, terminal and web changed only as far as the protocol forces.
2. **Parity.** Every §10.2 operation on the command line, the terminal and the web UI, including
   `force_conclude`, `instruct`, `prune`, `flags` and `results`. `DriftTests` and `SurfaceTests` stop
   carrying exclusions and assert parity instead.
3. **What agents and people read.** MCP instructions and tool descriptions, the node's turn prompt, the
   Peers block, plugin commands and skill, README, constitution Principle II, CLAUDE.md.
4. **Review gate** per CLAUDE.md, after the last edit.

`VersionPrefix` 0.26.0 — a breaking change while the major is zero. `ProtocolVersion.Current` 0.5.
