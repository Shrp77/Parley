# Contract: the command surface

One form, one table, one way to be refused. A command added later is a declaration and nothing else — if adding
one requires touching the input line, this contract has been broken.

## The form

```
/<id> [parameters]
```

- `<id>` is the text between `/` and the first space, and contains no `/` of its own (FR-020).
- Anything else beginning with `/` is text. `/etc/hosts` is a path somebody is asking about, decided without
  reference to what any command is called.
- An `<id>` that is not a command is text, sent as written (FR-021). Nothing is refused for not being a command.

## What a declaration carries

| field | purpose |
|---|---|
| `Id` | what is typed after `/` |
| `Says` | one line, shown in the completion list |
| `Takes` | parameters in order: kind, and required or not |
| `Kind` | `Draft` or `Acting` |
| `Door` | required for anything destructive: what is described, and what must be typed back |

## The two kinds, and why the distinction is in the model

| kind | what it does | sendable alone |
|---|---|---|
| `Draft` | changes what is about to be sent | **no** |
| `Acting` | does something | yes |

`/add` is `Draft`: it widens the recipients. It cannot be sent alone because membership is derived (§5.0) — an
agent joins by being party to an exchange, so `/add` with nothing to say would have to invent a Request, and every
message is answered (FR-025).

A future `/close` would be `Acting`. An abstraction that assumed either kind would be wrong at the second command,
which is why the distinction is declared rather than inferred.

## The commands in this feature

| id | takes | kind | door |
|---|---|---|---|
| `add` | one `Agent`, required | `Draft` | none — it widens a draft and sends nothing |

That is the whole table for now. The point of the table is that the next entry is a row.

## Refusals

| case | what happens |
|---|---|
| `<id>` is not a command | **text**, sent as written (FR-021) |
| `<id>` resolves, a required parameter is missing | refused, saying what it expected (FR-023) |
| `<id>` resolves, a parameter it does not take is given | refused, saying so |
| an `Agent` parameter names nobody | refused, naming what was not found |
| a `Draft` command alone | not sent, and the reason said (FR-024) |

The line between FR-021 and FR-023 is the whole care here: an identifier that did not resolve was probably prose,
and an identifier that did resolve was meant as a command and is owed a reason.

## Completion

- `/` opens the list of every command with its `Says` (FR-019, FR-029).
- It narrows to declarations whose `Id` begins with what is typed.
- Up and down select; Tab completes; Enter follows the active control (FR-030, FR-031, FR-032).
- The list closes when nothing matches and reopens if enough is deleted that something matches again — because it
  is derived from the text and never held (FR-034).
- An `Agent` parameter completes from the same mechanism the `@` sigil uses (FR-018, FR-037).

**Completing is the ordinary way to enter a command** (FR-036). That is what makes FR-021 survivable: an
unrecognised identifier is sent as text, so typing one blind and wrong costs somebody an ask — and the remedy is
that typing blind is unusual, not that a refusal comes back.

## Destructive commands

Permitted, and expected to grow (FR-026). What they may not do is arrive anywhere without the door the operation
already has: a description of what will go, and the subject's own name typed back.

**The door is declared, not remembered** (FR-027), and that is what keeps the guarantee checkable as operations
move in (FR-028). `SurfaceTests` proves today that each destructive operation's name appears in exactly one file
which contains its prompt; with a command table the name moves into a declaration and the dispatch goes generic,
so that check must read the declarations or it will keep passing while meaning less.

**And where an operation is reachable both as a command and as a region's key, the two perform the same act behind
the same door** (FR-040). Two routes that drifted would be the disagreement `DriftTests` exists to catch between
surfaces, arriving inside one.
