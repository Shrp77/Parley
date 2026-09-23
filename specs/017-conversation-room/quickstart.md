# Quickstart: A conversation is a room

Seven steps, each something a person does with what must be true afterwards. Walk it against a throwaway project
with two agents attached, and one conversation that already has traffic in both directions — the derivation cannot
be seen on an empty one.

**Setting up**: a scratch manifest naming one project, a broker from a build of this branch, two agents attached
through `parley-mcp` as `billing` and `orders`, and a seat taken as a person. `parley-terminal --version` reports
this build.

## 1. The room opens

Open a conversation from the project view.

- **Then**: two columns. The narrow one lists the participants, each in its own colour, what must be addressed,
  and the agents' status. The wide one holds the transcript with one input line across the bottom.
- **And**: every message still names its sender in its header, so nothing is carried by colour alone.

## 2. Typing answers what was asked

As `billing`, send the seat a request. Take delivery, then type an answer into the line and read the preview
before pressing anything.

- **Then**: the preview says it will answer and close the exchange, and names the key that would ask back instead.
- Press Enter.
- **Then**: the exchange is closed, the transcript shows the answer, and `parley results` counts one.

## 3. Typing to somebody who owes you clarifies

Send `orders` a request from the line, then — while it is unanswered — type again.

- **Then**: the preview says it clarifies the open request rather than opening a second one, because nobody may
  send a Result on their own request.
- Press Enter, and read the store.
- **Then**: one exchange, still open, the turn with `orders`.

## 4. One line, three different acts

Arrange three participants: one that asked the seat something, one waiting on the seat's ask, one with nothing
open. Type one line addressed to all three.

- **Then**: the preview lists all three with a different act each, and says how many exchanges open, close and
  stay open.
- Press Enter.
- **Then**: the store holds one Result, one Clarify and one Request, each against the right pair.

## 5. Addressing narrows, and never reaches a payload

Type `@` and watch the list. Choose one agent, write an ask, send it.

- **Then**: exactly one exchange opened, against that agent.
- **And**: the stored payload contains no `@`, no name, and no routing of any kind.
- Now `/add` an agent that is not a participant, write an ask, and send.
- **Then**: it becomes a participant by being party to the new exchange.
- Press Enter with `/add @someone` and nothing else written.
- **Then**: nothing is sent, and the line says why.

## 6. Commands complete, and a near miss is named

Type `/`.

- **Then**: every command is listed with what it does and what it takes.
- Type until one matches, then delete back until several do.
- **Then**: the list narrows and widens again, because it is derived from the text rather than dismissed.
- Type `/ad` and stop.
- **Then**: the list offers `add`, and the line says this would send as text and names the key that completes it.
- Press Enter.
- **Then**: it is sent as text, because nothing completed it. Read the payload: `/ad` is in it, verbatim.
- Type `/etc/hosts is unreadable` and send.
- **Then**: sent as text, with no reference to any command, because a second `/` decided it.

## 7. What is waiting, read where it was said

With two things outstanding, look at the narrow column and activate the second.

- **Then**: the transcript moves to that message with its neighbours visible.
- Leave the seat while something is outstanding.
- **Then**: the outstanding list and the line both say a seat is wanted, and nothing can be sent.
- Take a seat again.
- **Then**: what was written before is still there.

## What would make this a failure

- A message recorded as an act the preview did not name.
- Any payload containing addressing syntax, a recipient list, or a routing instruction.
- A count of exchanges that does not match what was opened.
- A message sent under a seat the book no longer holds.
- Text lost to a refusal, or to a message arriving while somebody typed.
- A reader moved by a message arriving.
- Any fact on that screen carried only by colour.
- An operation that was reachable in the conversation view before and is not now.
