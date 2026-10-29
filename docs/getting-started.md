# Getting started

This page takes you from nothing to two agents talking. The agents in the examples run in Claude
Code, which is the client Parley is most used with. Other MCP clients follow the same steps with
their own configuration file.

Getting two agents to talk takes three things:

1. Install the server and the tools.
2. Make a project and enroll the repositories in it. Enrolling also tells each agent that it has
   peers.
3. Say what each agent owns.

Telling an agent it has peers is the step people skip when they set things up by hand. An agent
with the tools but no idea that a peer exists will answer from its own repository and guess at the
rest. Enrolling does it for you.

## What you need

- The .NET 10 SDK, and Git.
- Claude Code, with a repository for each agent.
- Windows, Linux or macOS.

## 1. Install

Nothing is published to NuGet yet, so you build from a clone:

```
git clone https://github.com/Shrp77/Parley
cd Parley

./install.ps1        # Windows
./install.sh         # Linux and macOS
```

The script:

- builds and installs the tools,
- sets the broker to start when you sign in,
- starts it,
- and checks that it answers.

Four tools are installed:

| Tool | What it is | Installed by default |
|---|---|---|
| `parley` | The command line, and the broker itself | Always |
| `parley-mcp` | The adapter that connects an agent to the broker | Always |
| `parley-terminal` | The terminal you watch a deployment from | Windows: yes. Linux and macOS: no |
| `parley-web-ui` | The web console you open in a browser | Yes |

Use `-Surfaces terminal|web-ui|both|none` on Windows, or `--surfaces` elsewhere, to choose which
of the last two you get.

Nothing needs administrator rights. The broker runs as you, and it is started again each time
you sign in. When you want to remove everything, run `./install.ps1 -Uninstall` or
`./install.sh --uninstall`. That leaves your stores and your `.mcp.json` files alone, because
those are your deployment, not the installation.

## 2. Make a project and add your repositories

A **project** is one deployment: a store and a broker address. Naming a project is all it takes
to create one. The broker starts it within seconds.

You can use the terminal or the web console. They show the same thing.

**In the terminal.** Open it with:

```
parley
```

Press `Alt+c` to create a project. It asks for a name, and a one-line description if you want
one. It does not ask for a store or a port. Those are chosen for you.

Then open the project and choose **Agent > Enroll agents from folders** (`Alt+f`). Pick the
folder that holds your repositories, tick the ones that should take part, and confirm.

**In the web console.** Open <http://127.0.0.1:5280>. The broker starts it for you. Create or open
the project, go to its Agents tab, and choose **Register an agent**. Give it one repository and a
role, or a folder that holds several repositories and tick the ones you want.

**From the command line,** `parley enroll <folder>` does the same.

Whichever you use, it shows everything it is about to do before it does it, and writes nothing
until you confirm. For each repository it:

- writes a launch configuration (see below),
- adds a **Peers** section to the repository's `CLAUDE.md`, which tells the agent that other agents
  exist and how to reach them,
- lists the repository in this machine's catalogue of agents it may start,
- and starts the agent once so that it registers itself.

Listing a repository in the catalogue means a peer's request can start it with nobody watching.
That costs whatever the agent costs to run, which is why nothing starts out ticked.

If you already have a Claude Code session open in one of those repositories, restart it. A session
reads its configuration once, when it starts.

## 3. Name the peers (optional, but worth it)

The Peers section that enrolling adds is generic. It tells an agent that peers exist, that it
should ask them and not guess, and that its inbox is pull. It does not name the actual peers yet,
because the roster didn't exist when it was written.

To fill it in, edit the section by hand, or, in Claude Code, install the plugin and run a command
from inside the repository:

```
/plugin marketplace add Shrp77/Parley
/plugin install parley@parley
/parley:write-peers
```

`/parley:write-peers` reads the live list of agents through the Parley tools and writes the Peers
section from it. It has to run after the agent's session has restarted, because the command reads
the list through the very tools the restart adds. Run it again after you add agents, and it
refreshes the list.

The plugin is a convenience, and nothing above needs it. It also offers `/parley:setup` and
`/parley:setup-many` as an alternative to enrolling from the terminal or the console, plus a few
other commands. More detail is on the [Claude Code page](claude-code.md).

## 4. Say what each agent owns

Each agent has a one-line description of what it owns **and what it does not**, which every
other agent sees beside its name:

```
orders     Carts, orders, line totals. No tax, no stock.
billing    Invoicing, tax and payment capture. Does not price orders or reserve stock.
```

You do not have to write these. When an agent first attaches without one, Parley asks it what it
owns and what it does not, once, and the answer becomes its line. This costs the agent one turn.
You can check, edit or replace any line in the ownership review (`Alt+v` in the terminal, or
**Review ownership** on a project's page in the web console). See
[Configuration](configuration.md#the-ownership-line).

## 5. Send your first request

Open `orders` in Claude Code and ask it something that needs `billing`:

> Does billing apply VAT to the total it receives, or expect it included? I hand it a net total,
> and tax has to be applied exactly once.

`orders` looks up its peers, finds `billing`, and sends it a request with `parley_request`.
`billing` is started if nothing is running it. It collects the request, answers from its own
code, changes its own repository if the answer needs a change, and sends a result. `orders`
reads the result and carries on.

Neither agent read or edited the other's files. Open the project in the web console or the
terminal, find the conversation, and read the whole exchange. You can answer an agent's question
or send an agent work yourself from the box at the bottom of a conversation.

## When nothing happens

- **The agent tools are not there.** Claude Code reads `.mcp.json` once, when a session starts.
  Restart the session. If the file is tracked by Git, it only exists on branches that contain it.
  Put the entry on the branch everything else is cut from. If the tools are still missing after a
  restart, check where the repository is: Parley refuses one on a synced cloud folder (OneDrive,
  Google Drive, Dropbox, iCloud Drive, Box), a network drive or a virtual file system, or reached through a
  link or a `subst` letter to one. Move it to a folder on this machine's own disk.
- **"No broker is listening."** The broker is not running. `parley autostart status` says
  whether it is set up. `parley autostart install` sets it up and starts it.
- **An agent answers from its own repository and guesses.** It has not been told it has peers.
  Check that its instructions have a Peers section, which enrolling adds. Name the peers in it by
  hand, or with `/parley:write-peers` if you use the plugin. See
  [Claude Code](claude-code.md#standing-instructions).
- **A request waits and is flagged as not collected.** Nothing is running that agent. Either
  open its repository, or make sure it is listed as startable on its machine (see
  [Configuration](configuration.md#starting-agents-without-anybody-opening-them)).
- **Agents stop attaching after an upgrade.** A broker and an adapter whose protocol versions
  differ do not work together. Install on the machine running the broker **and** on every
  machine running agents, then restart the sessions. The broker's log names the version it speaks
  and the one it was offered.

## Next

- [Using it with Claude Code](claude-code.md)
- [Configuration](configuration.md)
- [Running a deployment](operating.md)
