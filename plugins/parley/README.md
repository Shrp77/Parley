# The Parley plugin for Claude Code

```
/plugin marketplace add Shrp77/Parley
/plugin install parley@parley
/parley:setup
```

## What it ships

| Component | What it is for |
|---|---|
| `/parley:setup` | Configures this repository as a Parley agent: checks the server is installed, writes `.mcp.json`, creates the database directory, and sends you to the next step |
| `/parley:peers` | Writes or refreshes the `## Peers` block in this repository's standing instructions, from the live roster |
| `parley-operator` skill | The `parley` command-line tool - the operations that are deliberately not tools |

## What it deliberately does not ship

**The MCP server configuration.** A plugin can bundle one, and can prompt for the values it
needs with `userConfig`, which looks like exactly the right mechanism. It is not, and the reason
is worth writing down because it will look like an oversight otherwise.

Claude Code stores the answers to a plugin's configuration dialog in `pluginConfigs`, and that
key is **user scope**. One install, one set of answers, across every repository you open.
Parley is a protocol between repositories - the whole point is that `orders` and `billing` are
different agents on one database - and identity is the single thing it makes unforgeable, by
taking the agent's name from the launch environment and offering no tool that mentions it. A
plugin-supplied server would hand every repository the same name and quietly undo that.

So `.mcp.json` stays per repository, where a per-repository fact belongs. The plugin writes it
for you instead of owning it.

**Anything the server already says.** When a client connects, `parley-mcp` sends MCP
`instructions` describing what Parley is for. That arrives through the protocol, so it reaches
any client that speaks MCP rather than only this one. A skill restating it would be a second
copy to drift, so the skill here covers the part the server has nothing to say about: the
operator's command-line tool, which is not exposed to any agent anywhere.

**A version.** Neither manifest declares one, so an install tracks the repository rather than
pinning to a string somebody has to remember to bump. Parley is in alpha and these files are
still moving; a stale plugin that cannot be updated by `/plugin marketplace update` would be a
worse problem than an unpinned one. This changes at the first release.

## Alpha

The plugin does not install the server, because there is nothing to install it from yet:
`Parley.Mcp` and `Parley.Cli` are not published to NuGet, so `/parley:setup` walks you through
cloning and building them. At the first release those become `dotnet tool install -g
Parley.Mcp`, and step 1 of setup becomes one line.

## Other clients

Parley is not a Claude Code project and the protocol has no opinion about which client an agent
lives in. This directory is the Claude Code integration; an equivalent for another client
belongs beside it. Everything vendor-specific is in these four files - the server's own
`instructions` is what every client gets for free, and it needs no adapter at all.
