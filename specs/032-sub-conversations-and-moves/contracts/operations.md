# Contracts

## Operator operations (SPEC §10.2)

| Operation | What it does |
|---|---|
| `spin_off(parent, child)` | Open a new conversation `child` linked to `parent`. Nothing is sent. Refused when `parent` does not exist or was destroyed; when `child` is an invalid name, destroyed, reserved for a roadmap item, begins `owns-`, or already exists with anything else in it or another parent. Repeating it for a still-empty child with the same parent succeeds. |
| `preview_move(source, exchanges)` | Say what a move of those exchanges takes: each exchange and whether it is open, its parties and message count, any added because it shares a group, and the files that stay. Changes nothing. |
| `move_exchanges(source, destination, new, exchanges)` | Move whole exchanges, all or none. `new` says the destination must not exist yet, and makes the source its parent. Refused with a sentence for: an exchange not in the source, part of a group left behind, a source or destination that is a roadmap item's or an ownership conversation, a closed or destroyed destination, the same conversation twice, an invalid or taken new name, a missing existing destination, an empty list. |

`export` gains `links`, `moves` and `settled_elsewhere`. `conversations` rows gain `parent`.

The command line refuses `spin-off` and `move`, naming the terminal.

## Agent operations (SPEC §10.1)

`inbox` gains `move_notices`: `{ exchange, from, to }` for each open exchange of the caller's that moved. The adapter adds one fixed sentence saying any files it named stayed under `from` and can still be fetched there. Nothing is owed back, it counts toward no waiting figure, and it wakes nobody. Acknowledged with the rest of the collection.

No agent operation can link, move or read a link or a move record.

## Surfaces

- **Web**: `/Conversations/SpinOff?project&from` (message) or `?project&id` (conversation), and `/Conversations/Move?project&id&x=<exchange>...`. Each does its work on a POST, and each GET draws what the POST will do. Per-message "Spin off" links and "Move entries" tick boxes on the conversation page lead to them.
- **Terminal**: a transcript command for each, the operation in `SpinningOff.cs` and `Moving.cs` with their doors.
- **Lists**: `[name] - spinoff from [parent]`, on `Projects/Agents` and in the terminal's conversations region and file placing list.
