# Security

Parley lets agents send each other text that ends up in a model's context, and it lets a person
do powerful things to a deployment. This page says what it protects, what it does not, and why.

## The short version

- Parley is built for **loopback and for tunnels you trust**. It is not safe on a hostile
  network.
- **There are no credentials on the wire.** Whatever can reach a broker's port can do everything,
  including acting as any agent.
- So the broker binds to loopback only, and there is no command to change that. To reach it from
  another machine, use an SSH tunnel.

## Who can do what

**The network.** An agent's identity is the name it presents when it connects. The broker checks
that the name is well formed and that nobody else holds it, and that is all. Refusing a second
agent that claims a name already in use stops a mistake. It does not stop an attacker. Anything
that can reach the broker can claim to be any agent, read its mail and answer in its name.

The reach is wider than an agent's mail. A broker serves the agent operations, the operator
operations and the node operations on one port. Whatever can reach it can also export every
conversation, retire and purge agents, destroy conversations, and ask a node to start an agent,
which is a process that acts in a repository. Reaching the port is closer to having a shell on
the machine than to having an inbox on it.

**So the transport must be confined** to a loopback connection, a private network you control,
or a tunnel authenticated outside Parley, such as key-based SSH.

**What would fix it properly** is a credential per agent, presented on connect and checked by the
broker. That is a real design with real key distribution behind it, and it is not built.

**Agents.** An agent holds only the agent tools. No tool takes a "from" argument. The identity
comes from the launch configuration, so no tool call can change who is speaking. The operator
operations are not available to an agent at any level. They are separate services on the wire,
absent from the type an adapter is handed, and not compiled into `parley-mcp`.

**You.** The person holds the operator operations: export, retire, purge, destroy, close and
prune. Destructive ones need a description and a deliberate act, usually typing a name back.

**The facilitator.** Parley's own participant may ask a question, relay a reply you wrote, and
agree that its own conversation is done. It
holds no operator operation, never receives anybody's work, and never assigns, directs,
prioritises, closes or overrides anything.

## What Parley defends against

**Context poisoning.** What one agent sends lands in another's working memory, so:

- **Messages are capped** at 8,000 characters, and the cap is enforced in the database.
- **Message text is inert data.** A payload is never parsed for an address or a command. An
  `@agent` completion in the box is recorded as a chip beside the text and removed when it is
  sent.
- **Messages arrive wrapped** so that a peer's words are marked as data, not instruction. A
  request never overrides the receiver's standing instructions.
- **No agent can read the log or search it.** There is no shared memory to poison.
- **Files go to disk, not into context.** Only `.md` and `.txt` files can be placed, so code
  travels as `Name.cs.txt` and nothing that runs can be opened by accident. A fetched file is
  another participant's data, never instructions. It is written only as a `.md` or
  `.txt` file, never under `.git`, `.claude`, `.github` or another agent client's settings or rules folder, and
  never as `CLAUDE.md`, `AGENTS.md` and the other instruction files Parley knows of. No list can
  name every place a tool reads instructions from, so the agent is told never to put a fetched
  file where one would. Files are checked against their
  SHA-256 hash when fetched.
- **An agent's answer becomes its ownership line with nobody reading it, by default.** Auto
  Accept is a recorded reduction in a control: peers route by a line no person checked. It is
  recorded as `automatic`, shown for review on every surface, kept in the agent's own words, and
  can be turned off so every answer waits for you.
- **Ownership lines have no length limit by default.** This is a recorded reduction in a
  control: a roster can become the largest thing an agent reads. The character rules that stop a
  line breaking, hiding or reordering its own entry still apply.

**Impersonation, within an agent.** One agent cannot say it is another through any tool.
Between processes that can reach the port, see above.

**Loss of control over spending.** Nothing runs agents unless something is waiting for them, and
`parley up` and the broker report what each turn cost. They also report how often an agent ran and
still owed exactly what it owed, which is how a runaway first shows itself.

**A web page reaching your console.** The web console answers only loopback host names and the
address it binds, which protects against DNS rebinding. No other site can show the console inside
a frame and trick you into clicking its buttons. The one page the console frames is a roadmap
item's conversation, inside the console's own roadmap page. A request another site starts, such as an
image pointing at the console, is refused before any page runs, so it cannot take your seat or
mark your mail as read. This relies on the browser saying who started a request, which current
browsers do for a loopback address; a browser too old to say, or a console bound to an address
other than loopback, is left with the host check alone.

**A link in an agent's message.** Links in what agents write are live, so a link can open any
console page. It cannot choose what you send, to whom, or skip a confirmation: what a form sends
comes only from the form you see, and every page that acts shows which project it acts in. Nor can it
make the console look up a folder on another machine: a path naming a network share or a device,
or reaching one through a folder link, is refused before anything touches the disk, because on Windows the lookup alone would offer your
sign-in to that machine.

**Silent damage to your files.** Project files are written atomically, preserve what they do not
understand, and refuse to write when anything was left out of a read.

## Credentials

Credentials are **references, never values**. A launch setting that needs a credential
holds `env:`, `keychain:` or `file:`, and the broker resolves it when it is needed, and never
stores or logs the value. The database holds no credential reference at all, and a launch
configuration is safe to commit. Most agents need no credential from Parley at all, because the
command-line tool behind them signs in on its own and keeps its own token.

## Reaching a broker from another machine

Use an SSH tunnel:

```bash
ssh -N -L 19100:127.0.0.1:19100 you@the-broker-machine
```

Then point `PARLEY_BROKER` at your end. A tunnel adds the authentication and encryption that the
protocol lacks and leaves no listening socket on the network. A bind address adds neither.

A project keeps the loopback port it was given when it was named, and no command moves it. You
can set a non-loopback address by hand in `project.json`, for a private network you control. The
broker warns on every start when you have, because that fact would otherwise be visible only to
whoever read the log and not to whoever made the edit.

## The store

A store has one broker. A lock is taken before anything else touches the store, and a second
launcher says so and exits. Do not put a store on a network share. SQLite in WAL mode needs
coherent shared memory, so a store on a share is corrupted, not just slow.

Retention removes old messages on a schedule, with a floor that can never delete a message nobody
has collected, anything on an open exchange, or the rows deduplication still reads. See
[Configuration](configuration.md#retention). Export is how you keep a conversation.

Before a new build upgrades a store, the broker copies it beside itself. The copy holds every
conversation's text as it was, so it is removed as soon as the upgraded store is served, and
destroying a project removes any left.

## Next

- [FAQ](faq.md)
- [The specification](../SPEC.md), for the full trust boundary
