---
name: parley-operator
description: Run the Parley commands a person uses on a deployment - list registered agents, see open conversations, check waiting mail, manage shared files, enroll repositories, and set up the broker - and point the person to the terminal or web UI for everything else (reading a whole conversation, retiring, reinstating or purging an agent, closing a conversation, pruning, and the health reports). Use when someone asks who is registered with Parley, what was said in a discussion, or to close, withdraw or clean up.
when_to_use: The user asks who is registered with Parley, what two agents said to each other, to remove or restore an agent, to close a discussion its participants have not closed, to apply the retention policy now, or what needs their attention in a deployment.
---

# The Parley operator commands

`parley` is a tool for a person. It is installed by a person and **never given to an agent**.
Some of what it does acts on other agents' work: reading a whole discussion, withdrawing a
registration, closing a discussion over its participants' votes, deleting message text.

These are not agent tools and cannot become tools by accident. `parley-mcp`, the server an MCP
client launches, does not have them compiled into it. A test checks that against the real binary.

## Most operator actions are on a screen

The actions that change other agents' work are not on the command line. A person does them in
one of two places:

- **The terminal.** Type `parley` (or `parley terminal`, the same thing spelled out). It needs a
  real terminal, not a script.
- **The web UI.** `parley web-ui` opens it in a browser. It is on `127.0.0.1:5280` by default.

The command line refuses those verbs. Each one exits with code 64, says it is not a command, and
names the terminal. **Do not try to run them.** Tell the person where the action is, and
let them do it.

In the terminal, every action is **Alt and a letter**. **F10** opens the menu bar, which lists
every action with its key. **F3** opens a project's configuration (Auto Accept and the like).

| What the person wants | Terminal | Web UI |
|---|---|---|
| Read a whole conversation | Open it from a project's conversations list. Alt+c copies all of it to the clipboard; Alt+m copies one message. | The conversation's page. Export, then Export TXT, saves it as a file. |
| Close a conversation its participants have not closed | Conversations list, Alt+f (*Close this conversation now*). It asks for a reason, and each participant holding work is told. | Health page, "close it" beside the flag. |
| Archive or unarchive a conversation | Conversations list, Alt+h. | The project page: *Archive* or *Bring back* beside each conversation. |
| Destroy a conversation | Conversations list, Alt+d. It takes the conversation's identifier typed back. | The project page: *Destroy…* beside each conversation, which takes the identifier typed back. |
| Retire or purge an agent | Agents list, Alt+r (*Remove this agent*). It describes the agent, offers keeping it first, and takes the agent's name typed back. | The agent's page: Retire, Purge. |
| Reinstate a retired agent | Agents list, Alt+i. | The agent's page: Reinstate. |
| See or change what an agent is responsible for, or have it say it again | Agents list, Alt+n. | The agent's page: *What this agent is responsible for*. |
| Start an agent | Agents list, Enter on its row, then *Start* in its menu. Alt+u starts every agent that has never registered. | The agent's page: *Start*. |
| Apply the retention policy now | Projects list, Alt+p. | Health page, "Apply the retention policy now". |
| What needs a person, and what the work came to | Alt+y (Health), from the projects list or a conversations list. It shows flags, who waits on whom, results, size refusals and clarifications. | The project's Health page. |
| Decide a roadmap an agent proposed | F4, or the project's *Proposed roadmaps* pane, Enter on one. Edit it, confirm it, or decline it. | The project's *Roadmaps* page lists the proposals waiting, each with its own page. |
| Read a roadmap, see what is next, start an item | The project's *Roadmaps* pane, Enter. Alt+n goes to the next item; Enter on an item opens its conversation with its words in the box, and nothing is sent until the person sends it. Alt+e edits the roadmap. In an item's conversation, Alt+o goes back to its roadmap. | The roadmap's page. Each item opens its conversation in place; an item's conversation page links back to it. |
| Create or remove a project | Projects list, Alt+c to create, Alt+r to remove. Destroying takes the project's name typed back. | The projects page: *Create a project*. Removing or renaming one is the terminal's. |

## The boundary

Point the person to these when they ask for them. Do not suggest them as a way around the
protocol.

- **A roadmap is the person's.** An agent can only propose one, and work on an item reaches an
  agent as a request the person sends. Do not offer to confirm, edit or start one on their behalf.
- **Closing a conversation** ends one the participants have not agreed to end. It is not a way
  past something an agent is waiting on. If a request is blocked on an answer, the answer is to
  give it.
- **Reading a whole conversation** shows every message, across agents. That is exactly the context
  Parley keeps apart. It is for a person reading back a decision, not for handing one agent
  another's reasoning. Do not paste a transcript into an agent's session as a shortcut for asking
  it.
- **Retiring** stops an agent's server from starting at all. It is a withdrawal, not a mute.
- **Purging** deletes the agent and every conversation it took part in: every message, exchange and
  file in them, including the other agents' side of those conversations. It frees the name and
  cannot be undone. The person is told what was deleted, table by table. Retiring keeps all of
  that, so retire when the record matters, which is almost always. Purge only when the record
  itself is what should go: a mistyped name, a repository somebody opened once. The person seat
  cannot be purged.

None of this is a rule a tool enforces. It is the reason these actions are kept away from agents.
No agent has authority over another, and these are how a **person** steps in.

## What the command line still does

```
parley serve                          run the broker: the one process that opens the stores
parley serve --project <name>         one project alone: testing only
parley serve --with-node              and this machine's node in the same process
parley autostart install              start the broker whenever you log in, serving every project
                                      on this machine
parley autostart remove               stop doing that
parley autostart status               say whether it is set up, and what it serves
parley up                             run this machine's node, so its agents can be started
parley web-ui [<url>]                 the web UI in a browser; loopback by default
                                      (its own tool: parley-web-ui, installed separately)
parley                                the terminal; needs a real one
                                      (its own tool: parley-terminal, installed separately;
                                      parley terminal is the same thing, spelled out)

parley project list                   what this machine is configured for
                                      (creating one is in the terminal or the web UI; changing
                                      and removing one are in the terminal)
parley use [<name>] [--shell <name>]  the project this terminal's commands act on (--clear to stop)

parley enroll <dir>                   find the repositories under a directory, tick the ones
                                      to enroll, and configure, catalogue and start them
parley enroll <dir> --all             tick every one found, for scripts with no terminal
parley enroll <dir> --describe        say what enrolling them would change, and change nothing

parley registry                       every registered agent, with its workspace
parley open                           conversations with an exchange still open, and who is in
                                      them; what stopping the broker would interrupt
parley waiting <agent|role> [--with-files]   how many messages and polls await an agent; --with-files adds its file notices
parley files list [--conversation <id>] [--full]  the files placed: size, sha256, who placed each
parley files place <path> --conversation <id> [--name <name>]  place a .md or .txt file from here, as the person
parley files remove <name>... [--conversation <id>] [--yes]  remove files whoever placed them; describes them and asks
parley files prune [--yes-remove-every-file]  REMOVE every file; asks for the project's name
parley files open <name>... [--conversation <id>]  open files to read here, or say where they are
parley files folder [--conversation <id>] open a conversation's folder, or say where it is
parley files problems [--all]         what checks of the location found: files changed on disk and moved to quarantine, and entries it could not read, move or delete
parley files dismiss <id>             take a problem off the list; deletes nothing on disk
parley --help                         the same list, on stdout, exiting zero
```

`registry` is the whole roster, including the folder each agent works in. The roster agents see
leaves that out on purpose. It is how you tell two similar names apart, and how you find the
repository behind a name.

`open` lists conversations with work still open, and who is in them. It shows identifiers and
names, not message text. To read what was said, the person opens the conversation in the terminal
or the web UI.

## The broker

Every command except `serve`, `autostart`, `project`, `use`, `enroll`, `terminal` and `web-ui` is a
client (`enroll` asks each project's broker only when it writes). One process owns the store and
everything else connects to it:

```
parley --broker <host:port> <command>
```

`--broker` defaults to `PARLEY_BROKER`, and then to the project's own broker. Most of the time
nothing needs setting. If nothing is listening, every command says so and says how to start one.
**That is a broker nobody started, not a mistake in what you typed.**

## Projects

A machine can run several Parley deployments at once. Each is a *project*, with its own store, its
own port and its own agents, and none can see the others. Nothing about a project crosses the wire,
and the same agent name may appear in two projects without either knowing.

A project is created in the terminal. Naming it is the whole of it: its store and its port are
derived and claimed for you. After that, commands find it by name:

```
parley
parley --project orders registry
```

A command acts on the project `--project` names. Failing that, on `PARLEY_PROJECT`, which `parley
use <name>` prints the line to set for one terminal. Failing that, on the one the repository it
runs in names in `.mcp.json`, when exactly one of those is a project this machine has. Failing
that, on the only project. With several and nothing choosing, **every command refuses and lists
them**, because picking silently would act on a deployment nobody asked for. On such a machine,
each command says on standard error which project it acted on, and why.

**Creating, changing and removing a project are done in the terminal, not on the command line.**
`parley project list` stays, because it reads and changes nothing.

Removing a project **deletes nothing**: not the store, not any repository's launch configuration.
Destroying is a separate act. It waits for the broker to stop, deletes the store, and lists the
repositories that were agents in it. It takes the project's own name typed back.

## Enrolling

`enroll` configures several repositories at once. It is a person's command, not an agent's, because
it writes the launch configuration that decides what an agent *is*. It finds the git repositories
under a folder, offers them, and configures, catalogues and starts the ones chosen, so that each
registers itself. The terminal does the same from inside a project, as *Enroll agents from folders*
(Alt+f in the agents list), and Alt+a adds one repository.

**Enrolling a repository is the consent to start it.** A catalogued checkout can be started by a
peer's request with nobody watching, which spends whatever that agent costs to run. The command
says so and names the count before it configures anything.

**It never writes an `owns` line, and never rewrites a `## Peers` section it did not write.** The
agent is asked what it owns once it is running. Reading the roster is still `/parley:write-peers`,
and it comes last: every agent that registers makes every block already written out of date.

## Starting the broker at logon

`autostart` stops "nothing works until somebody runs `parley serve`" being something a person has
to remember. It installs a scheduled task on Windows, a systemd user unit on Linux, or a launch
agent on macOS.

**None of them needs administrator rights.** They are user-level. What genuinely needs privilege
is a task that runs elevated or as SYSTEM, and a broker needs neither.

**The cost is that the broker starts at logon and stops at logout.** That is right for a machine
somebody works at. A server that must answer with nobody signed in needs a real service, which
does need privilege.

**`parley autostart install` serves every project.** One process runs each project's broker, and
this machine's node in each of them. It starts a project named later within seconds. `parley
autostart status` says what is installed. `--project` narrows it to one project, for testing.

## serve and up

`serve` and `up` are the two commands that do not return.

`serve` is the broker. It opens each project's store, migrates it, and serves every agent and node
in that project. Nothing else opens a store, and **no option anywhere names one**: a project's store
is `parley.db` inside its own folder.

`up` is this machine's node. It tells the broker the machine is here, which makes the agents on it
startable, and starts one when a request arrives for it. What it can start comes from `node.json`.
There is **one per machine**, in the same folder as the projects, and every project starts from it.
`PARLEY_NODE_CONFIG` or `--node-config` names a different file. Enrolling writes its entries, so a
person rarely edits it. Ctrl+C stops the node and every agent it started.

**It spends money while nobody is watching.** That is the whole feature and also the whole risk:
each turn is a real agent session on the person's account. Do not start it because it seems
useful. Start it when the person asks.

## Retention

Pruning brings the next retention pass forward. It does not set the policy. The broker applies it on every
retention pass, using `PARLEY_RETENTION_IDLE_HOURS` and `PARLEY_RETENTION_MAX_AGE_HOURS` from the
environment the broker was started with: three days idle and thirty days maximum age if unset.

**It removes text permanently.** If a discussion is worth keeping, the person should save it first
(Alt+c in the terminal, or Export, then Export TXT, in the web UI). That copy is a file they own,
and it is the only one that outlives the policy.

A floor bounds it the other way, and is not configurable. Nothing deletes a message nobody has
collected yet, anything on an exchange that is still open, the request a result nobody has
collected yet carries back, or the recent rows deduplication still reads.

## The health reports

The Health view (Alt+y in the terminal, the Health page in the web UI) is where a person reads what
a deployment did. None of it changes anything.

- **Flags** are what needs a person *now*. Nothing in Parley times out, so work that has stopped is
  not closed for you. It is flagged: a message not collected past its threshold, an open exchange
  nobody has touched for too long, work owed by a retired agent, or a conversation waiting only on
  a participant saying it is finished. The remedies are starting the agent, answering what it is
  waiting on, or closing the conversation with a reason.
- **Results** say what each request came to: done, answered, acknowledged, or blocked, and on
  what (`missing_information`, `conflict` or `permission`). A blocker is not an agent behaving
  badly. It is the protocol handing a person the one thing no agent could settle. Read the blocked
  ones first.
- **Size refusals** are the evidence for the message size cap: how often a message was refused for
  length, the worst case and the average. No refused text is kept, only its length. If somebody
  wants the cap raised, this is what the argument should be made from.
- **Clarifications** show how deep clarification goes and how often an exchange reached its limit.
  By default there is no cap. Where a deployment has set one, read it beside the numbers, because a
  cap hides its own evidence: depths pile up at whatever limit was in force.

## Reporting back

Summarise what a command printed. Do not paste a whole transcript into the session unless the
person asks for it. When the person needs to act on a screen, say exactly where: the terminal's
key, or the web UI's page.
