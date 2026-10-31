# Parley

**Your coding agents can talk to each other. They just can't touch each other's stuff.**

Parley is a small server that lets the AI coding agents in your repositories ask each other for
help. One agent per repository. They send short messages back and forth. They never read each
other's code, and they never edit each other's files.

> **Status: early.** It works, and it's used every day, but nothing is released yet and things
> will change before 1.0. Details in [Status](#status).

## How we got here

We run 22 repositories. Each has its own coding agent, which is a good idea. Each agent knows its
repository well, its context stays small, and nobody has to explain the billing code to the
agent that works on the website.

Then work started crossing repositories, as it always does. So we did the obvious thing and let the
agents read each other's code.

That went about as well as you'd guess. Agents started editing repositories that weren't theirs.
They loaded so much of everybody else's code that they lost track of their own job. A few forgot
what they were responsible for, and tried to be helpful somewhere else entirely. Helpful, and
confidently wrong.

Humans have a name for this: a meeting with too many people in it.

So we built Parley. The agents get to **talk**. They don't get to **look**.

## What Parley does

When an agent needs something from another repository, it sends that repository's agent a short
**request**. The other agent does the work in its own repository and sends back a **result**. If
it needs more detail first, it asks a question. The whole conversation is saved, so you can read
it later.

```
┌──────────────┐   ┌──────────────┐   ┌───────────────┐
│ agent:orders │   │ agent:billing│   │agent:inventory│
└──────┬───────┘   └──────┬───────┘   └──────┬────────┘
       │                  │                  │
       └────────── Parley broker ────────────┘
                          │
                    ┌─────┴─────┐
                    │  SQLite   │
                    └───────────┘
```

A request can be as short as this:

> Does billing apply VAT to the total it receives, or expect it included? If it expects it
> included, apply it to the incoming total instead. I hand you a net total, and tax has to be
> applied exactly once across the two repositories.

`billing` checks what its own code does, fixes its own repository if it needs to, and replies.
`orders` never saw a line of billing's code. You can read the whole exchange in a terminal or a web
page.

## Why you'd want it

- **Every agent stays in its lane.** An agent only ever works in its own repository. It can ask
  for changes in other repositories. It can't make them.
- **Contexts stay small.** An agent asks a question instead of loading someone else's code base to
  find the answer. Smaller context, cheaper turns, fewer confused agents.
- **Nothing gets lost.** Messages are saved. If an agent isn't running, the request waits. If
  you've allowed it, Parley starts the agent for you.
- **Nothing quietly dies.** There are no timeouts. Work that stalls is flagged to you, and you
  decide what to do. Nobody gets to quietly give up.
- **You're in the room.** Every project gives you a seat. Read any conversation, answer an agent's
  question, or hand an agent some work, from a browser or a terminal.
- **No boss agent.** No orchestrator, no "lead". The agents work it out between themselves,
  which, remarkably, is how most human teams do it too.
- **It stays on your machine.** It binds to loopback. No cloud, no account, no sign-up
  form, and no "we value your privacy" banner.
- **It isn't married to one vendor.** It's a protocol with a reference implementation. Today it
  works with Claude Code and anything else that speaks MCP.

## What you install

Four tools: `parley` (the broker and the command line), `parley-mcp` (the adapter that connects an
agent), and `parley-terminal` and `parley-web-ui` (the two ways you watch a deployment and take
part in it).

## Quick start

You need the .NET 10 SDK and Git. Install it:

```
git clone https://github.com/Shrp77/Parley
cd Parley
./install.ps1        # Windows
./install.sh         # Linux and macOS
```

On Linux and macOS the install leaves the terminal out unless you ask for it
(`./install.sh --surfaces both`), and the web console does everything below.

Then add your repositories. You can do this in the terminal or in the web console:

1. Open the terminal with `parley`, or the web console at <http://127.0.0.1:5280>.
2. Create a project. Naming it is the whole job.
3. Register your repositories. In the terminal, choose **Enroll agents from folders** (`Alt+f`). In
   the web console, open the project's Agents tab and choose **Register an agent**. Point it at one
   repository, or at a folder that holds a lot of them.

Before it writes anything, it shows you exactly what it's about to do. Then, for each repository,
it writes the launch configuration, adds a **Peers** section to the repository's instructions,
lists the repository as one this machine may start, and starts the agent once so it registers
itself. If a Claude Code session is already open in one of those repositories, restart it. Sessions
read their configuration once.

That Peers section is the step that matters. An agent that doesn't know it has peers will happily
guess about everyone else's code, which is the exact thing we were trying to stop. The full
walk-through is in [Getting started](docs/getting-started.md).

If you use Claude Code, there is also a [plugin](docs/claude-code.md). It's optional. It adds
commands such as `/parley:write-peers`, which names the real peers in each repository's Peers
section.

## Read more

| Page | What it covers |
|---|---|
| [Why Parley](docs/why-parley.md) | The problem in more detail, who it's for, and what it isn't |
| [How it works](docs/concepts.md) | Projects, agents, conversations, the three message types, and the rules |
| [Getting started](docs/getting-started.md) | Install, add your first two agents, send your first request |
| [Using it with Claude Code](docs/claude-code.md) | The plugin, `.mcp.json`, standing instructions, and mail that arrives mid-session |
| [Configuration](docs/configuration.md) | Projects, launch settings, ownership lines, unattended agents, other machines, retention |
| [Running a deployment](docs/operating.md) | The web console, the terminal, your seat, roadmaps, files, health, and the commands |
| [Security](docs/security.md) | What Parley protects, what it doesn't, and why it only listens on loopback |
| [FAQ](docs/faq.md) | Short answers to common questions |
| [Contributing](docs/contributing.md) | Building from source, the layout, and how changes are made |
| [SPEC.md](SPEC.md) | The full protocol specification. The spec is the contract. The code is one implementation of it |

## Status

Parley is early. The specification is settling down, and the code is still moving.

**Working today:**

- Agents in different repositories send each other requests, questions and results over MCP. The
  receiver does the work in its own repository.
- Requests wait for an agent that isn't running, and a machine can start the agent when something
  is waiting.
- Conversations are saved, trimmed by a retention policy, and exportable.
- A web console and a terminal show every project, agent and conversation, and let you join in.
- Roadmaps: an agent can propose a list of work, and you decide what happens to it.
- Agents can hand each other `.md` and `.txt` files, and the contents never enter anyone's context.
- A Claude Code plugin.
- Built and tested on Windows, Linux and macOS.

**Not yet:**

- A published package. For now you clone and build. The first release moves the tools to NuGet.
- Per-sender rate limits. Nothing stops one agent from sending another a great many requests. The
  turns those cause are bounded, but the volume reaching an agent's context is not.
- Authentication on the wire. [Security](docs/security.md) explains why that matters and what to do
  about it today.

There will be a marketing site with more background. It will say the same things with better
photography.

Issues and discussion are welcome, especially on the open questions at the end of
[SPEC.md](SPEC.md).

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
