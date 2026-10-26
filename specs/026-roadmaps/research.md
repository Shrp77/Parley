# Research: Roadmaps, and every agent in the Room

Each decision names the code it rests on. Facts about the code come from a read of the repository on 2026-10-06; verify any of them before building on it.

## R1. A proposal is stored apart from messages, and is not a roadmap

**Decision**: `ProposeRoadmap` writes `roadmap_proposals` and `roadmap_proposal_items`, never a message or an exchange. A proposal opens nothing and is owed no answer.
**Rationale**: spec clarification 5. Putting the list in a message (the other option) would reintroduce the transcript problem the owner named: the list would live in history and the "roadmap" would be a pointer to it. It would also be capped by the message cap and framed as an agent's words to a person, which makes it a Request that §5 says must be answered.
**Alternatives**: promote a message into a roadmap (rejected: above); a Result carrying a list (rejected: a Result answers an exchange and the proposer may have no open exchange with the person).

## R2. Agents never read roadmaps

**Decision**: no agent operation returns roadmap content. The agent learns only the outcome of its own proposal (confirmed, declined, superseded) as a notice, and receives an item as an ordinary Request.
**Rationale**: Principle II (no plan one agent imposes on another) and IV (text that crosses is charged to every agent that reads it) are both kept by absence. A roadmap read by agents would be a shared plan and an uncapped one. `AgentsCannotChangeARoadmapTests` walks every `Agents` message and method and fails on the words `Roadmap` beyond `ProposeRoadmap` and the notice, in the pattern of `AgentsCannotSetTheirOwnLineTests`, which matches words rather than one name because the mistake it guards is an operation added under a name nobody predicted.
**Cost recorded**: an agent cannot ask what the roadmap says. A revision names its roadmap by title only; the broker stores the title as text and the person resolves it at confirmation (spec FR-009b). If it needs to, it asks the person. Revisions are proposed by title, so an agent proposing a revision names a roadmap it knows from the conversation. The person decides whether a proposal is a revision or a new roadmap.

## R3. The notice for a decision rides the existing notice path, and wakes nothing

**Decision**: `roadmap_notices(agent, proposal, outcome, created_utc, delivered_utc)`, drained by `parley_inbox` beside file notices, counted in `Waiting`, never causing a wake.
**Rationale**: spec FR-010 says the proposer is told. A person's reply request would open an exchange the agent owes an answer to, which is the wrong weight for "declined". File notices (SPEC §9.5) are the existing precedent: stored, drained, counted, wake nothing.
**Alternative rejected**: a Request from the person. It turns every decline into agent work.

## R4. Item state is derived in Parley.Client by one function

**Decision**: `ItemStates.Of(roadmap, waiting, me)` returns per item: state, the agent holding it, the blocker if any, and the roadmap's next item. It is the only place state is computed and both surfaces and the CLI call it.
**Rationale**: `Activities` and `WaitingOnYou` already do this for rooms, from `ConversationExport`, `WaitingEdge` and `AgentRecord`. Two surfaces implementing it separately is how they would disagree about what a person has seen (CLAUDE.md, Parley.Client). Spec clarification 1 (no hand-set state) means there is nothing to store.
**Inputs (decided after analysis finding C1)**: `Roadmap` returns, for each item, the facts the broker already holds: whether its conversation exists, how many exchanges are open, whether the open request has been collected, how many agents' latest answer on the person's requests was blocked and the blocker one of those gave, and who closed the conversation. An agent's latest is the one that closed last. `ItemStates` combines those with `DescribeWaiting` and `DescribeRegistry`. No per-item `Export`, so a view costs three calls however long the roadmap. The signature is `ItemStates.Of(roadmap, waiting, me)`.
**States** and how each is derived (all from the item's conversation, absent conversation meaning not started):
- not started: no conversation exists;
- in progress: an open exchange whose request the receiver collected;
- waiting on an agent: an open exchange the agent has not yet collected, or the agent has asked another agent;
- waiting on the person: a wait-for edge whose waited-on is the person;
- blocked: nothing is open, some agent's latest answer on the person's requests is `blocked`, and the conversation is open; beside open work, or once closed, the blocker is still named; the blocker that agent gave (`missing_information`, `conflict`, `permission`) is shown;
- done: nothing open, at least one exchange ended with an agent's Result, none of the person's requests closed before its agent answered, and no agent's latest answer is `blocked` (`done`, `answered` and `acknowledged` all clear a part; a request the person closed first is no answer);
- closed: the person closed the conversation (its participants cannot, §5.7), and the item is not done (a request ended before its agent answered, or a part was blocked, whose blocker is still shown);
- could not be read: the read failed. **Never** shown as not started (FR-018).
**Next item**: the first item in order that is not started and whose dependencies are all done. If none, the roadmap says complete, or that what remains is waiting, blocked or closed, or needs an item that is.

## R4a. Dependencies are validated on final content

**Decision**: `ConfirmProposal` and `EditRoadmap` validate the person's final content: every `needs` names a live item in an earlier position, every intended agent is in the project. A reorder or removal that breaks that is refused with a sentence. A removed item is therefore never a dependency of a live one, and the insert-time trigger is a second layer, not the only one.
**Rationale**: analysis finding C2. A trigger on insert cannot see a reorder.

## R5. Starting an item adds no operation and the broker only recognises the name

**Decision**: the client asks `Roadmap` for the item's reserved conversation id, then calls `SeatVoice.RequestAsync(agent, text, conversationId)` exactly as the room's line does. The broker accepts the first message into a reserved conversation only from the person.
**Rationale**: `SeatVoice.RequestAsync` already opens a Request in a named conversation as the person's seat through `Agents`, and `RequestEveryoneAsync` reuses the first conversation's id for the rest. A `StartItem` operator operation would have to speak as the seat from the operator service, which the design forbids ("no operator operation acts on the seat", CLAUDE.md).
**Mechanism**: the reserved name is stored at confirmation in `roadmap_item_conversations` with no foreign key to `conversations`, which does not yet have the row. A trigger on `messages` refuses the first message into a reserved conversation unless its sender is the person. Without it, any agent could open `rm3-2` first and capture the item.
**Probe owed**: the trigger is written, then an agent is made to open the reserved name and must be refused before the guard is trusted (the review ledger's rule: probe, do not reason).

## R6. A conversation is recognised by the record, never by a prefix or a payload marker

**Decision**: `roadmap_item_conversations` is the recognition. Names are derived (`rm<roadmapNumber>-<itemNumber>`, using small per-project sequence numbers) so a name is legal by construction (`[a-z0-9-]`) and unique.
**Rationale**: FR-024 and §13's rule that a payload is inert data. `OwnershipAsk.Recognises` uses a prefix and role, which is a metadata convention and acceptable there; here the stronger form is free because the table exists, and a prefix would be a way for an agent to *look* like an item conversation.

## R7. Removing an item keeps its conversation

**Decision**: items are never hard-deleted. A removed item carries `removed_rev`; its link row stays, so the conversation renders "formerly item N of <roadmap>". Spec clarification 2.
**Consequence**: roadmap revisions are a counter on the roadmap and per-item `added_rev` and `removed_rev`. No history table is built; what changed is readable from those and from the conversations. An item's text edit overwrites the text and sets `edited_utc`. "Text changed since" is the broker's fact `text_edited_since_started`: the item's `edited_utc` against its first request's time, never a comparison of words (FR-014a).

## R8. The Room reads the registry, always

**Decision**: `RoomRoster.Build(export, registry, me)` returns every non-retired agent of the registry, participants first, each flagged `Took part`. Retired agents appear only if they took part, marked retired, and are never recipients. The terminal's `others` list (shown today only when seated) and the web's `Roll` dictionary are replaced by it.
**Rationale**: both surfaces already read the registry per conversation view for activity marks, so no new call. Participants are today derived from the transcript (`Pairing.Participants`, `Visibility.Participants`) and stay so; the registry adds the rest.
**Mark**: a participant carries a plain `◈` (a text glyph, not colour); non-participants a space, under a heading *In this conversation* and *Elsewhere in the project*. `Activities`' marks (◆ working, ● running, ○ not running) stay as they are. A legend line says what `◈` means. **Narrow rule**: the mark and the name survive before any status text, by the existing wrap in `RoomRegion.Fit`.
**Probe owed**: the person's seat and Parley stay apart from the agents (0.37.7 recorded Parley appearing in a list one line before the filter; the filter goes above the join and the test reads the drawn rows, not the data).

## R9. No cap on roadmap text, but the line-breaking character class is not relaxed

**Decision**: storage carries no length bound on a proposal, a title, an item or their count, following `agents.owns` (a bound that stands down at the top of its range so a deployment could reinstate one). The same character rule that stops text breaking, hiding or reordering a surface's lines applies (`OwnershipStatement`'s class, reused), because roadmap text is drawn by the terminal and the web UI and "a transcript recording words an agent never said" is the failure this class exists to stop.
**Rationale**: spec clarification 4; Principle IV amendment.
**Guidance in place of a number**: the proposing tool and the standing instructions carry a short template (`contracts/operations.md`): a few-word title, one verb-led line per item, `for` and `needs` only where known, no background or narrative. Items are separate structured fields so the shape discourages paragraphs. It is advice, not enforcement, and says so. The owner's call, 2026-10-06.
**Residual risk, recorded**: any attached agent can write an arbitrarily large proposal into the store. That is a disk-fill route that the message cap closes for messages. It is accepted on the spec's decision. The evidence to set a bound later is recorded: proposal size is stored with the proposal.

## R10. The decision to confirm is a single operation that carries the final content

**Decision**: `ConfirmProposal(proposal, content, target)` takes the person's final text, order, agents and dependencies and writes the roadmap (or a revision of the named roadmap) and each item's reserved conversation name in one transaction. There is no "confirm unchanged" operation; unchanged is the same call with the proposal's own content.
**Rationale**: provenance (`proposed`, `edited`) is a comparison done by the broker of the final content against the stored proposal, as 020's `Chosen` does for the ownership line. A flag the surface sets would be right every time somebody remembered to set it.
**Concurrency**: confirming one of two proposals against one roadmap as a revision of it marks the other `superseded` in the same transaction (spec edge case).

## R11. `EditRoadmap` exists because the person must be able to remove an item

**Decision**: the person edits a confirmed roadmap through `EditRoadmap(roadmap, content)`, with the same shape as confirmation and the same revision rule. It is the only way an item is added, reworded, reordered or removed after confirmation, and is the answer to spec clarification 1's "edit the roadmap to remove it".
**Rationale**: spec FR-009 and the edge case on dead items.
**Door**: removing an item whose conversation has an open exchange is allowed (R7 keeps the conversation) but is described first on every surface; a typed-name confirmation is not required, because nothing is deleted. `SurfaceTests` is extended to hold the call in one file per surface.

## R12. Parley is not a proposer

**Decision**: the facilitator has no roadmap operation. See the Assumptions of the spec and the Constitution Check. If the owner wants Parley to propose, that is an amendment argued first (Principle II names the three things it may do).

## R13. The project-level count

**Decision**: the projects view and the room's *waiting on you* gain two counts, proposals waiting and items waiting on the person, from `Proposals` and from `ItemStates`. They add to the existing flags; nothing else changes about how waiting is shown.
**Rationale**: FR-008 and FR-017. `WaitingOnYou` is the existing shared derivation, extended rather than duplicated.

## Open items carried into tasks

- Exact terminal key bindings for roadmaps (follow `NoTwoActionsShareAKeyTests`; Alt plus a letter, per 0.37.0).
- Where on the web a roadmap is reached from (the project page), and its stylesheet.
- Whether `parley-mcp`'s channel nudge counts proposals' decisions: no. File notices never count (§9.5), and these follow them.
