# Feature Specification: Soft message limit and message length statistics

**Feature Branch**: `028-soft-message-limit`
**Created**: 2026-11-02
**Status**: Draft
**Input**: Owner, 2026-11-02: "Make the 16k cap soft. Allow messages of any length up to 512k. Log the message length so we can start tracking how long the messages are, how often that happens, what agents they are involved, do longer conversations drift towards longer messages etc." The owner has been unable to send at least three of their own messages because of the 8,000-character cap, and agents hit it reporting the output of commands run on remote systems.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A long message gets through (Priority: P1)

An agent or a person has something long to say, such as the output of commands run on a remote system. They send it. It is delivered whole, whatever its length up to the hard limit, and the sender is told it was long.

**Why this priority**: This is the failure the owner hits. Everything else depends on messages over 8,000 characters being allowed.

**Independent Test**: Send a 100,000-character Result through a real adapter into an upgraded store. The requester collects it whole, and the exchange closes as usual.

**Acceptance Scenarios**:

1. **Given** a message of 16,000 characters or fewer, **When** it is sent, **Then** it is delivered and nothing is said about its length.
2. **Given** a message of 16,001 to 512,000 characters, **When** an agent sends it, **Then** it is delivered whole and the reply tells the sender its length and that it passed the soft limit.
3. **Given** a message over 512,000 characters, **When** it is sent, **Then** it is refused, nothing is delivered, the refusal states both numbers, and the refusal is counted.
4. **Given** a long message waiting for an agent using Claude Code, **When** the agent collects its inbox, **Then** the agent can read the whole message, whether the client shows it directly or through a file.

---

### User Story 2 - A person never loses what they typed (Priority: P1)

The person writes a long reply on the terminal or in the web console. If it is over the hard limit they are told, and what they typed is still there to cut down and send again.

**Why this priority**: The owner reports being unable to send. Losing the words as well would be worse, and the code suggests one path may.

**Independent Test**: On each surface, write a reply over 512,000 characters, press send, and see the refusal with the text intact in the box.

**Acceptance Scenarios**:

1. **Given** the person has typed more than the hard limit, **When** they send on the terminal, **Then** the message is refused, the text remains in the box, and the refusal is counted against the person.
2. **Given** the same in the web console, **When** they submit the form, **Then** the page comes back with the refusal and the text still in the field, and the refusal is counted.
3. **Given** the person has typed 20,000 characters, **When** they send on either surface, **Then** it is delivered and recorded as long.

---

### User Story 3 - The person can see how long messages are (Priority: P2)

The person opens the health report and sees how long messages have been: how many, how often they pass the soft limit, the longest, which agents send and receive long ones, and whether messages get longer as a conversation goes on.

**Why this priority**: The owner asked for this so that the limits can be set from what actually happens, and the design gives up a control in exchange for it.

**Independent Test**: With a store holding messages of known lengths, the report on the terminal and in the web console shows the expected numbers, and still shows them after those conversations are pruned.

**Acceptance Scenarios**:

1. **Given** messages have been sent, **When** the person opens the report, **Then** it shows the count, the median, the longest and how many passed the soft limit, overall and by message type.
2. **Given** several agents, **When** the person reads the report, **Then** each agent shows its sent and received counts, its longest, and how many passed the soft limit.
3. **Given** conversations of various lengths, **When** the person reads the report, **Then** it shows the median message length by position in the conversation (the 1st to 5th messages, 6th to 20th, 21st to 100th, and later), so a drift is visible.
4. **Given** a conversation has been pruned, **When** the person reads the report, **Then** the numbers still include its messages.
5. **Given** a store that existed before this feature, **When** it has been upgraded, **Then** the report includes the messages it held at that moment.

---

### User Story 4 - A live store upgrades in place (Priority: P1)

The person installs the new build. Their stores keep every row and accept long messages from then on.

**Why this priority**: Live stores hold work in flight and are kept. The limit is in the database, so this change rebuilds its largest table.

**Independent Test**: Build a store at the previous schema holding conversations, open exchanges, deliveries and files, upgrade it, and compare every row.

**Acceptance Scenarios**:

1. **Given** a store at the previous schema, **When** the broker starts, **Then** every row, every open exchange and every undelivered message is as it was, and the database's own integrity checks pass.
2. **Given** an upgrade that fails partway, **When** the broker starts again, **Then** the store is as it was before the attempt.

### Edge Cases

- Exactly 16,000 and exactly 512,000 characters are allowed and not flagged as over; one more is over.
- The limits count the message as stored, including the structure around the text, so a text full of line breaks and quotes reaches a limit sooner than its visible length suggests. The refusal gives the stored length, and a person's surface does not claim the text is shorter than that.
- A message of 512,000 characters waiting alongside others: one collection already carries at least one message, so it arrives alone and the rest wait.
- A role request sends one message per receiver, and each is recorded.
- A message over the hard limit is recorded only as a refusal, by length. Its text is stored nowhere.
- Purging an agent or destroying a project removes their length records, as it removes everything else of theirs. Ordinary pruning does not.
- A duplicate refused as a duplicate is not a message, so it is not recorded.
- Screens drawing a very long message stay usable: the transcript does not stall the terminal, and the web page renders it.
- A facilitator reply a person wrote follows the same limits as any other message.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST deliver a message of up to 512,000 characters as stored, whoever sends it and whatever its type.
- **FR-002**: The system MUST refuse a message over 512,000 characters, in storage as well as above it, so that no path can route around the limit.
- **FR-003**: The soft limit MUST be 16,000 characters. Passing it MUST never refuse, delay or change a message.
- **FR-004**: The reply to an agent whose message passed the soft limit MUST say its length and that it passed. The text MUST NOT be more than a sentence or two.
- **FR-005**: The reader of a message MUST NOT be told anything about its length beyond what the message itself shows.
- **FR-006**: A refusal for size MUST be counted against the sender, by type, with the worst and total lengths and the limit it was refused against, for agents and for the person alike.
- **FR-007**: The system MUST record, for every stored message, its length, type, sender, receiver, conversation, position in that conversation and time. It MUST NOT record any of the message's text in that record.
- **FR-008**: Length records MUST survive pruning of the conversation they came from, and MUST go when the agent is purged or the project destroyed.
- **FR-009**: Upgrading a store MUST create length records for the messages it already holds.
- **FR-010**: The person MUST be able to read the statistics of User Story 3 on the terminal and in the web console.
- **FR-011**: No agent operation MAY read the statistics.
- **FR-012**: A person's over-limit message MUST leave what they typed in place on both surfaces.
- **FR-013**: A store MUST be upgraded in place by a new migration that keeps every row, and fails as a whole.
- **FR-014**: Every statement of the 8,000-character cap and of the rule that the cap moves only on evidence MUST be replaced by the new rule across the specification, the documents, the constitution, tool descriptions, refusal sentences and project instructions, recording this as the owner's decision and a reduction in the terseness control.
- **FR-015**: The limits MUST be stated in one place in the code, with storage holding its own copy of the hard limit and a test holding the two together.
- **FR-016**: Tool descriptions and the server's instructions MUST still ask agents to send what the receiver needs for its part, and MUST say that very long output may be better placed as a file.

### Key Entities

- **Message length record**: One per stored message. Length, type, sender, receiver, conversation identifier, position in the conversation, time. Never text. Outlives the message.
- **Size refusal**: The existing count per sender and type of messages refused for size, now against the hard limit.
- **Length report**: What the person reads, derived from the records: overall, per agent, and by position in the conversation.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A 100,000-character command report sent by an agent is read whole by its receiver through a real client, with no extra step by a person.
- **SC-002**: The person can send a 20,000-character reply on both surfaces at the first attempt.
- **SC-003**: In every attempt over the hard limit, on both surfaces, the text is still there afterwards.
- **SC-004**: A store at the previous schema upgrades with no row lost or changed, checked by comparing every table before and after.
- **SC-005**: The person can answer each of these from the report alone: how many messages passed 16,000 characters, which agent sent the longest, and whether later messages in a conversation are longer than earlier ones.
- **SC-006**: Upgrading a store of 100,000 messages finishes in under a minute on the owner's machine.

## Assumptions

- The limits count the stored message, as the cap does today. The owner's numbers are taken as characters on that count.
- Being told about the soft limit is the sender's only signal. The owner asked for messages to pass, not for a warning the sender must act on.
- Position in a conversation is the order of messages in it. Drift is shown by position bands rather than a fitted trend, which is enough to see it and simple to read.
- Length records are kept indefinitely. They are small and hold no words, and a retention setting can follow if they ever matter.
- Statistics are per project, as the store is. The report is read-only and has no export of its own.
- The roadmap and `agents.owns` exceptions to the terseness rule are unchanged. A roadmap item sent as a Request is a message under these limits.
- Agents on clients other than Claude Code are not tested: a client that cannot take a large tool result may lose it. This is recorded in the documents rather than handled.
- This takes the next free migration number. Feature 027, not yet merged, moves after it.
- The owner's decision replaces SPEC §17 question 16's "moves on evidence" as the reason for the numbers; the evidence collected there stays, and the owner's report of three blocked messages is added to it.
