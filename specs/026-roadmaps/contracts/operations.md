# Contract: operations

Which operation lives on which service, which surfaces bind it, and the guard that holds each boundary. Written before the proto and SPEC.md §10, and to be amended in them if the three disagree.

## Agent side (`Agents` service, SPEC.md §10.1)

| Operation | Takes | Returns | Notes |
|---|---|---|---|
| `ProposeRoadmap` | title, ordered items (text, optional intended agent, optional earlier items it needs), optional conversation it comes from, optional title of a roadmap it proposes to revise (a hint; the person resolves it) | proposal id, or a refusal with the reason | The proposer is the attached caller; no operation takes a sender. Refused for the person and the facilitator. An empty list is refused. No length bound. |
| (inbox) | | roadmap notices alongside file notices | Drained, counted in `Waiting`, wakes nothing. |

**The template the tool carries** (its description and the standing instructions; guidance, not a check):

```
title:   a few words            e.g. "Billing export"
items:   one line each, verb first, the thing to be done
         1. Add CSV export endpoint            for: billing
         2. Call the endpoint from the report  for: orders   needs: 1
         3. Document the new column            needs: 2
rules:   facts and actions only; no background, no reasons, no narrative;
         "for" and "needs" only where known; detail goes in the item's
         conversation once the person starts it.
```

Each item is a structured field (text, agent, needs) and not a block of prose, so the shape itself discourages paragraphs.

The MCP adapter gains one tool, `parley_propose_roadmap`. Its description says what a proposal is not: it does not create a roadmap, assigns nobody, and nothing happens until the person confirms.

**There is no agent operation that reads, changes, reorders, deletes or marks done a roadmap.** `AgentsCannotChangeARoadmapTests` fails if a method or message on `Agents` mentions a roadmap beyond the two above.

## Operator side (`Operators` service, SPEC.md §10.2, `IParleyBrokerClient`)

| Operation | Kind | Takes | Returns |
|---|---|---|---|
| `Roadmaps` | read | none | summaries: id, title, revision, item count, counts by derived-input facts the broker holds (live items, started items) |
| `Roadmap` | read | roadmap id | items in order with text, intended agent, needs, reserved conversation id, removed ones marked, and each live item's **facts** (data-model.md): conversation exists, open exchanges, collected, counts of closed exchanges that ended complete, blocked or with no Result, the blocker, who closed it, destroyed, text first sent |
| `Proposals` | read | optional state filter | proposals with their items, proposer, source conversation, target roadmap, size |
| `ConfirmProposal` | write | proposal id, final content, target (new, or a roadmap) | the roadmap and its revision; provenance (`proposed` or `edited`) is computed by the broker; any other waiting proposal whose title hint matches the roadmap confirmed becomes `superseded`; the content is validated first (FR-009a) |
| `DeclineProposal` | write | proposal id | writes the notice for the proposer |
| `EditRoadmap` | write | roadmap id, expected revision, final content | the new revision. Refused if the expected revision is not current (two surfaces editing). Origin becomes `edited`. |

State is not an operation. The broker returns facts; `ItemStates.Of(roadmap, edges, registry)` in `Parley.Client` derives states, so a view costs `Roadmap`, `DescribeWaiting` and `DescribeRegistry` and never an export per item.

Existing reads gain a field, not an operation: `ListConversations` returns the item reference on `ConversationSummary`; `Export` names item and roadmap in its header.

## Starting an item

No operation. The client reads the item's reserved conversation id from `Roadmap` and sends through `SeatVoice.RequestAsync(agent, ask, conversationId)` on the person's seat. The broker's storage guard accepts the first message into a reserved conversation only from the person.

## Surfaces (every operator operation on both, per FR-028)

| Operation | Terminal | Web UI |
|---|---|---|
| `Roadmaps`, `Roadmap` | roadmaps list and a roadmap view | project page entry, roadmap page | `parley roadmaps`, `parley roadmap show <id>` |
| `Proposals`, `ConfirmProposal`, `DeclineProposal` | proposal review dialog (edit, confirm, decline) | review page with edit form | `parley roadmap proposals`, `parley roadmap confirm <id> [--file <edited>]`, `parley roadmap decline <id>` |
| `EditRoadmap` | edit dialog | edit page | `parley roadmap edit <id> --file <f>` |

`parley roadmap ... --project <p>` follows the existing selection rules.

## Guards that must be extended or added

| Guard | Change |
|---|---|
| `ContractSeparationTests` | the agent contract still holds one method per its rule; `ProposeRoadmap` is added to the declared list, no operator method leaks, and the two interfaces still share no method names |
| `SurfaceBoundaryTests` | the declared public surface of `Broker` gains `ProposeRoadmap`; of `OperatorConsole` gains the six operator operations; operations naming a roadmap also name the person |
| `AgentsCannotChangeARoadmapTests` | new, reflection over the generated `Agents` client, in the pattern of `AgentsCannotSetTheirOwnLineTests` |
| `DriftTests` | every `BrokerClient` operation bound by both surfaces; `contracts/operations.md` of this feature names them all |
| `SurfaceTests` | `EditRoadmap` and `DeclineProposal` each in exactly one file per surface, carrying their door |
| `EveryActionHasADoorTests` (web) | each new page action is linked and described on a GET before it is performed on a POST |
| `PluginTests` | unchanged: the command line adds no verb, so the skill and the tool's source stay equal |
| `GraphTests` | unchanged and must stay green: nothing here may give Client, Web, Terminal or Setup a route to a store |
