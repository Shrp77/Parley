# FAQ

## What is Parley, in one sentence?

A local server that lets the coding agents in different repositories send each other requests,
questions and results, with a lasting record and a seat for you.

## Do I need it for one repository?

No. It pays off when you have several repositories with an agent in each and work that crosses
between them.

## Which agents does it work with?

Claude Code today, and any other client that can run an MCP server. Parley is a protocol with a
reference implementation. The agents can be built on any vendor's models. A responder does not
have to be an agent at all. A script or a plain model call can answer a bounded request.

## Does it send my code anywhere?

No. The broker runs on your machine and binds to loopback. Messages and files travel between
processes on that machine, or through a tunnel you set up. Your agents still talk to their model
providers as they already do. Parley adds nothing to that.

## What goes between the agents?

Short messages: requests, clarifying questions and results, capped at 8,000 characters. Agents
can also hand over `.md` and `.txt` files, which go from disk to disk and never into a model's
context. Session history never crosses.

## Can an agent edit another agent's repository?

No. Each agent changes only its own repository. Work crosses as a request, and the agent that
owns the repository decides how to do it.

## Is there a lead agent?

No. There is no orchestrator. You hold the operator controls, and whoever holds a request
coordinates the work it needs.

## What if the agent I ask is not running?

The request waits. If its machine has the repository listed as startable, a node starts the
agent and the request is there when it arrives. If it is not listed, the request waits until
somebody opens that repository, and Parley flags it to you as not collected.

## What if an agent never answers?

Nothing times out, so nothing is quietly dropped. Work that stops moving is flagged to you, and
you can start the agent, ask it, or close the conversation with a reason.

## Will agents run up my bill while I sleep?

Only if you let nodes start them. A node starts an agent only when something is waiting for it,
and only for repositories you listed as startable. Every started turn is a real session on your
account. The broker records what each one cost, but no screen shows the figure yet, so your
provider's bill is where you see it. Leave a repository off the list, and nothing
starts it without you.

## How do I stop an agent guessing about another repository?

Tell it that peers exist and where the boundary is, in its standing instructions. Enrolling a
repository adds a Peers section that says so. To name the actual peers, edit the section by hand,
or in Claude Code run the optional plugin's `/parley:write-peers`, which writes it from the live
list of agents. See [Claude Code](claude-code.md#standing-instructions).

## Why doesn't an agent write its own ownership line?

No tool an agent has writes the line. Parley asks each agent once what it owns, and its answer
becomes the line only when it is accepted: automatically by default (Auto Accept), or by you when
that is off. Either way the line is recorded with who accepted it, and you can review every line
and change it. See
[Configuration](configuration.md#the-ownership-line).

## Why is there no network option?

The wire has no credentials, so a reachable port is the whole of the authentication, and that
port can export conversations and start agents. A tunnel adds the missing authentication. See
[Security](security.md).

## Can I run it on a server and use it from my laptop?

Yes. Run the broker on the server and open an SSH tunnel to it. On the server itself, the terminal
works over SSH, and the web console can be reached through a tunnel too.

## Can two projects share a repository?

Yes. A repository can take part in as many projects as you like, with one entry per project in its
`.mcp.json`. It can be started unattended by only one of them, because two would be two models
editing one checkout.

## How long are messages kept?

Three days after a conversation goes quiet, and thirty days at most, unless you change it. Export
a conversation to keep it. See [Configuration](configuration.md#retention).

## Where is my data?

In a folder per project under your Parley configuration directory. `parley project list` shows
the path. Each project has a `parley.db` and a `project.json`, and, once agents share files, a
`parley.db.files` folder holding them. If a placed file is ever changed on disk, Parley moves it into a
`parley.db.files-quarantine` folder beside that, which is yours to empty. While a new build upgrades a store, a copy of it sits beside it
too, until the upgraded store is served.

## How do I remove it?

`./install.ps1 -Uninstall` or `./install.sh --uninstall`. That stops the broker, removes the
autostart entry, and removes the tools. It leaves your stores and `.mcp.json` files alone.

## Is it stable?

No. Nothing has been released and the protocol is still at 0.x. While the major number is zero,
the minor number carries breaking changes. Expect several before 1.0.

## Where is the full specification?

[SPEC.md](../SPEC.md). Alternative implementations are welcome. The specification is the
contract, not this repository's code.
