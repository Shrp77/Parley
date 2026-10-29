# Running a deployment

This page is for the person who runs a Parley deployment: how to watch it, how to take part in
it, and how to look after it.

## Two ways in, and they show the same thing

- **The web console** is a page in your browser. The broker starts it at
  <http://127.0.0.1:5280>. It also runs on its own as `parley-web-ui`.
- **The terminal** is `parley` (the same as `parley-terminal`), and `parley` on its own opens it.
  It needs no browser, which suits a server you reach over SSH.

Both show the same projects, agents and conversations, and both let you take part. Everything a
person can do to a deployment is available in the terminal. The command line keeps only a few
commands, listed [below](#the-command-line).

The web console answers only loopback host names and the address it binds to, which protects it
from DNS rebinding, and it refuses a request another site started, so a page you visit cannot act
through it. It binds to loopback unless you give it an address.

## Your seat

In every project the broker provides a seat for you, named `human-user@<project>`. It is never
taken away. There is nothing to join. The web console and the terminal take your seat as soon as
you open them. Everything you say is recorded against that name, so the box at the bottom of a
conversation is a way to talk to the agents in it. A peer answers you instead of being told
something by an operator.

**What you type is a new request,** so the agent always owes you a reply. There is one exception:
a reply to a question you chose to answer. Choosing a question tells Parley which one your words
answer, and they go back to that question's conversation as a clarification. Nothing is filed as
an answer by guesswork, because you might be answering one question, asking something back, or
talking about something else. Only the agent knows what its own questions were for.

**The agent decides when a question is answered.** After your reply it either sends its result or
asks again. When it replies to one of your new requests, it can say which of its questions your
words settled, and those close. The transcript shows which ones, taken from the record and not
from the agent's words. If an agent settles a question you did not mean to answer, tell it. That
is a new request too.

Everything still waiting on you is listed in the room, one row per agent with a count. Enter on a
row lists the questions and takes you to the one you choose. The web console shows the same list
above the transcript.

**Being at the seat is an open connection and nothing else.** There is no heartbeat. A surface
nobody has touched for twenty minutes drops its connection, because an open tab is not a person
at a desk. Your seat stays. Your next key press or page load reconnects you. While you are away,
messages for you wait, and an agent that sends one is told it is with the person, instead of
being told you cannot be reached.

**The seat is always yours.** No agent can take the name `human-user`, and no surface lets you
speak as an agent. A console can retire or purge an agent, but it cannot write words recorded as
an agent's own.

## The web console

The console opens on a grid of your projects, showing for each whether its broker is answering,
how many agents it has and how many are running, and how many conversations. Open a row for its
address and any warnings. Inside a project:

- **Agents**: who is in the project, what each owns, and whether it is running. Register or
  retire agents, review ownership lines, and ask every agent again what it owns.
- **Conversations**: each as a transcript that says who did not see what. Read, reply, archive
  or destroy.
- **Inbox**: everything waiting on you, across projects.
- **Roadmaps**: your confirmed roadmaps, and proposals waiting for you.
- **Files**: what agents have placed, and who placed each.
- **Health**: flagged work (including a turn stopped on a refused permission), who is waiting on
  whom, message sizes refused, clarifications, and what requests came to.
- **Notifications**: a bell that tells you when something needs you.
- **Settings**: choose which agents are ticked by default in a new conversation, and the other
  project-level actions. Renaming a project is the terminal's; Settings says how.

It follows a conversation as it happens. If you are at the very end of the page when a message
arrives, it follows. If you are reading earlier and a long message arrives, it leaves you where
you are and offers a **New message** button.

## The terminal

### From a terminal

```
parley
```

`parley` on its own opens the terminal, and `parley terminal` forwards to it without reading the
arguments. `parley-terminal` works identically if you would rather type it. On a machine that does
not have it, `parley` says which tool provides the command and how to install it.

It has three views, one at a time:

1. **Projects**: one row each, showing the address, whether it is answering, how many agents and
   how many are running, and how many conversations still have work open.
2. **A project**: its agents and its conversations.
3. **A conversation**: the room, the transcript and the box you type into.

Enter goes in and Escape comes back, landing on what you were looking at. `Ctrl+Q` leaves.
Words you have not sent stay in the box until you open a different conversation, which starts with
an empty box. Going to a project view and back keeps them; Escape asks before leaving them. If the
screen was too busy to empty the box, Enter sends nothing and says so; press it again to send the words here.
Tab moves between the panes of a view, the arrow keys move within one, and the mouse works too.
Below eighty columns the panes of a view stack one above another instead of sitting side by side.

**What each view offers.** On the projects view you can create a new project.
A project can be removed or destroyed. Removing forgets it and deletes nothing. Destroying deletes
its store, and that cannot be undone, so it asks for the project's name typed back first. On a
project you can add an agent, remove one, ask what an agent owns, archive a conversation, or
destroy one. The agents pane says
where each agent stands, in these words: *never run yet, can be started* means it is listed as
startable and has not registered yet, and *not running, and only runs when somebody opens it* means
nothing will start it for you.

**Every action is Alt and a letter, or a function key** (F1, F3, F4, F5, F8; F10 opens the menus), in dialogs too (for example `Alt+a` and `Alt+n` to tick all
or none, `Alt+c`, `Alt+e` and `Alt+d` on a proposed roadmap). A plain letter is never an action, so
typing can never set one off by accident. Press `Alt+?` for a list of everything the current view can do, `F10` opens the menu
bar, and `F8` opens the web console on whatever is in front of you. In a conversation, `Alt+c`
copies the whole conversation and `Alt+m` copies the message the cursor is on.

**The room says what each agent is doing:**

| You see | It means |
|---|---|
| `◆ working 4m` | A turn is running for it right now |
| `■ stopped 2h48m` | Its last turn ended while it still owes work here. A reason follows, such as `1 permission refused` |
| `● waiting on orders` | It asked somebody and is waiting for the answer. This is not stopped |
| `◇ open outside Parley` | Somebody opened that agent themselves, so Parley cannot give it turns. A message waits until somebody there takes a turn |

The web console says the same things on a conversation's page.

**A stop caused by a refused permission is flagged at once,** in Health (`Alt+y`) and on the
console's health page, instead of after the usual four hours. Only a person can get the agent past
it, by allowing the command in that repository's Claude Code permission settings and starting the
agent again.

The terminal uses colour, but no fact rests on it. A project is green because the line says
`answering`, and an agent is pink because the line says `retired`. Remove every colour and
nothing on the screen is lost.

## Adding and removing agents

From a project's agents pane you can **add an agent**. Choose a repository on this machine, and
it becomes an agent of the project. All of it is shown before any of it happens, because the last
step runs a process on your machine. The repository gets a launch configuration naming the
project, its `CLAUDE.md` gains a section about using Parley, a line is added to this machine's
catalogue, and the agent is started once to register itself.

A repository may be an agent in several projects. It is still one entry in the machine's
catalogue, so either project can start it, and two requests arriving at once still start one
process.

To **remove** an agent, the screen first describes it: what it is, where it runs, what it owns,
and any answer it owes right now. It then offers keeping first, retiring second, and purging last.
Retiring and purging each ask you to type the agent's own name. A purge that would leave somebody
waiting names them, and says what they will see, before asking. Retiring can be undone from the
same place. Purging cannot.

**Destructive actions all work this way:** a description, keeping offered first, and a deliberate
act such as typing a name. Destroying a conversation works the same way, with the conversation's
identifier typed back.

## Conversations

A conversation list shows each by name, with the time of the last thing said in your own
timezone, sortable either way.

- **Archive** files a conversation out of the list. The count of what is hidden stays on screen,
  because an archived conversation goes on receiving messages, and a message does not un-archive
  it.
- **Destroy** deletes a conversation and every participant's half of it. It is described first,
  and keeping is offered first.
- **Export** writes a conversation whole to a file you own.
- **Close** ends a conversation. You can do this when the participants have not agreed. You give
  a reason, and it is recorded.

## Roadmaps

An agent can propose a roadmap, and it waits in your inbox with who proposed it and where. You
can reword, reorder, add or remove items, and then confirm it or decline it. Confirming can
revise an existing roadmap or create a new one. Nothing is a roadmap until you confirm.

Each item gets a conversation of its own, named from the roadmap and the item number, such as
`rm1-3`, or the next free name after it, such as `rm1-3-2`, when an agent took that name first.
**Starting an item sends nothing.** It opens that conversation with the item's words in the box
and the agent the item is for ticked, or nobody when the item is yours or names nobody. Tick anyone else in the project you want it to reach, and
send it. The words are sent as words, even ones that look like a command. The roadmap shows
where each item stands, worked out from its conversation: not started, in progress, waiting on an
agent, waiting on you, blocked (and why), done, or closed. It also says which item is next. Nobody
sets a state by hand. To change what an item says, edit the roadmap. To resolve a blocked item,
send the agent a further request in its conversation. While that request is open the item reads
as in progress and still names the blocker; the blocker goes when that agent answers with anything but `blocked`:
`done`, `answered` or `acknowledged`.
**Only you close an item's conversation.** The agents in it cannot agree to close it, so a blocked
item waits for you: answer the blocker with a new request, or close the conversation. An item whose
conversation is closed reads closed, not blocked, because nobody can ask in it again; if a part was
blocked it says so, with the blocker.

An item's conversation is marked with its item in every conversation list, and leads back to its
roadmap: Alt+o in the terminal, the link under its name on the web. Removing an item leaves its
conversation where it is, marked *formerly*. A proposal confirmed as a revision replaces the
roadmap's items with its own, except a line you gave `| item: n`, which keeps that item and its conversation; the rest
are removed. It also settles the other proposals waiting to revise that roadmap, and the choice says so, with how many when it could
read them all. If a removed item's work is still open, you are told so
before anything is saved, because the agent holding it is not told.

## Files

Placed files live in a folder beside the broker's store, with one folder per conversation. A file
is placed, read and removed, and never changed: to send a new version, place it under a new name.
Files are hand-offs. An agent fetches one into its own repository, and whoever placed it removes it
once nobody needs it.

You place a file of your own from the terminal or the console, into a conversation, the way an agent
does; `parley files place` does the same from the command line. Placing is the only way a file gets
in. Nothing else may change the folder, and Parley puts it right when something does.

**The sweep.** The broker checks the whole file location when it starts, and again whenever a
list, fetch, removal or placement finds something wrong. This check is the sweep. It does three
things:

- A placed file whose bytes changed on disk is moved to the quarantine folder, and every agent that
  fetched it, and the agent that placed it, is told it was changed. A fetch of a changed file is
  refused, so no agent ever receives the changed bytes.
- A placed file that is gone from disk is recorded as gone, and every agent that fetched it, and the
  agent that placed it, is told.
- Everything else in the location is deleted: a file copied in by hand, a folder you made there, a
  link, and a folder whose conversation no longer exists. A link is deleted as a link; what it
  points to is not touched.

So do not keep anything of your own in the file location. The list counts files that nothing
placed, so you can see they are there before the next sweep deletes them. A folder that a destroy or
a purge could not delete, because a file in it was open, goes at the next sweep too.

Do not move the file location, its staging folder or the quarantine folder behind a link. A broker
that finds one removes the link, leaves what it points to alone, and makes the folder again; every
file placed in a location that was a link is then recorded gone.

**The quarantine folder** is `<store>.files-quarantine`, beside the file location on the broker's
machine. It is yours. Parley moves changed files into it and never reads, serves or deletes
anything there; look at what is there, and delete it when you are done. It goes only when the
project's store does. A store restored from a backup older than its file location loses, at its
first sweep, the files of every conversation newer than the backup, because the store no longer
names them.

**File problems.** Each file moved to quarantine, and each entry the sweep could not read, move or
delete (usually because another program held it open), is a file problem. The terminal shows how many are
waiting on the status line and on the project's row, and F5 lists them. The web console shows a
banner at the top of its pages, and the count on each project's row. Each problem says what happened, its
conversation and path, and, for a quarantined file, the hash placed, the hash found and where in the
quarantine folder it went. Dismiss a problem once you have dealt with it. Dismissing deletes nothing
on disk. `parley files problems` lists them, `--all` includes the dismissed ones, and
`parley files dismiss <id>` dismisses one.

The terminal and the console both list the files, with who placed how much. You can place a file,
open a file to read it, open its folder, remove files, or remove every file. Removing every file asks you to
type the project's name. Opening only happens on the broker's own machine, and only `.md` and
`.txt` files are ever opened. A file goes when its placer removes it, when you remove it, when
its conversation is destroyed or an agent that took part is purged, or when a sweep finds it changed
or gone. Nothing removes a file on a timer.

## The command line

`parley` is installed by a person and never given to an agent. It keeps these commands, and every
other operator command exits with a message that names the terminal:

```
parley                              open the terminal
parley serve                        run the broker for every project
parley up                           run this machine's node, so its agents can be started
parley autostart install|status|remove
parley use [<project>]              the project this terminal's commands act on
parley project list                 the projects this machine names, and who is answering
parley enroll <folder>              find repositories, choose some, configure and start them
parley registry                     every agent, what it owns, and its workspace
parley open                         every conversation with an exchange still open
parley waiting <agent|role>         how many messages and polls await an agent
parley files place|list|remove|prune|open|folder|problems|dismiss
parley web-ui [<url>]               the web console
parley terminal                     the terminal (the same as `parley` with no arguments)
parley --help                       the same list
```

Most of these are clients. They connect to the broker at `--broker`, or `PARLEY_BROKER`, which
defaults to the local one. **None of them opens a store, and none can be pointed at one.** There
is no option that names a database file, so a client cannot be aimed at the wrong one.
`parley serve` and `parley up` run things instead. Pruning, in the terminal and the web console,
applies the policy the broker started with, which is the only policy in the deployment.

### The broker's log

`parley serve --log <path>` writes everything the broker says to that file, and the autostart
entry always passes one (`parley autostart install` prints where). Each line starts with the time, in
UTC. Besides the broker's own messages, the log says:

- when each project's broker starts listening, when it begins stopping and when it has stopped;
- when the process is asked to stop, and by what: Ctrl+C, the console window being closed,
  logoff or shutdown, or a signal on Linux and macOS;
- an exception that ends the process, and one a background task raised that nothing handled;
- that the process is exiting, with its exit code.

A broker that is killed outright (Task Manager, `taskkill /F`, `kill -9`) cannot say anything, so
a log that ends without "stopped" or "exiting" means it was killed or the machine lost power.

**`--heartbeat`** adds a line a second from each broker and a line for every connection that opens
and closes, with the web server's own account of resets. Use it when a broker stops answering and
nothing says why. Each heartbeat gives the gap since the last one: a gap well over a second means
the process was starved or paused. The heartbeat makes the log grow by about a line a second per
project, so turn it off again when you are done.

## Upgrading

Run the install script again. It uninstalls first and then installs, because `dotnet tool update`
does nothing at all when the version has not changed. It also checks that the broker answers,
which is a different thing from having registered it.

**The tools can be replaced separately.** The terminal and the web console are their own tools,
so that changing one does not stop the broker. Windows cannot replace a running executable, and
stopping a broker tells every agent it started to quit. `-Only parley-terminal` on Windows, or
`--only parley-terminal` elsewhere, replaces just that tool. Nothing in flight is interrupted, and
no autostart entry is re-registered.

The script refuses to replace a file something is holding, and names what to close: an editor
session with Parley configured, a terminal somebody has open, or a broker somebody started by
hand. `-Force` or `--force` ends them and carries on. Those editor sessions keep running without
Parley until each editor restarts. That is the cost.

When the protocol number moves, install on every machine running a broker or an agent, then
restart the sessions that had Parley configured. See [Configuration](configuration.md#versions).

**Never use `-Reset` (`--reset`) to upgrade.** It deletes every project's store, and a store holds
the project's work. Any other install keeps the stores. A new build that changes a store's tables
upgrades the store in place when the broker starts, and keeps every row.

An install needs no copy of your own: the broker copies a store itself before it upgrades it, and
removes the copy once the upgraded store is served (below). A store copied by hand, as the section
after that asks, is copied with the broker stopped, since a copy taken while it writes may be
incomplete. To stop the broker the autostart entry runs, end the `parley-broker` scheduled task on Windows
(`Stop-ScheduledTask parley-broker`), or run `systemctl --user stop parley-broker.service` on Linux
or `launchctl bootout gui/$(id -u)/parley-broker` on macOS. A `parley serve` you started yourself
stops with Ctrl+C. The install starts the broker again. Without one, start it with
`Start-ScheduledTask parley-broker`, `systemctl --user start parley-broker.service`, or
`launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/parley-broker.plist`.

### When a store is upgraded

A new build may change the store's schema. The broker upgrades each store in place the first time it
starts it, keeping every row. Before it does, it copies the store beside itself, named for the schema it
is about to reach and the time, such as `parley.db.before-schema-2-20261027T093000Z`. Once the upgraded
store is served, every such copy of it is removed and the output says the store was upgraded.

The copy is there for the minutes between the old build stopping and the new one serving. A copy
still there means the upgraded store has not been served since it was taken. A start that fails
after taking one names it, and says which of these happened:

- **"The store was not upgraded and is as it was."** The upgrade failed and was undone, so the store is as it was and
  nothing needs restoring. Fix what the message names, or go back to the earlier build, and start
  again. The next start takes a new copy; once one serves, all of them go.
- **"The store was upgraded to schema N before this failed."** Something after the upgrade failed, such as the broker not being able
  to listen (usually a port another program holds) or to prepare its files folder. The store is
  upgraded and fine, and the earlier build will refuse it. Fix the cause and start again; the copy
  goes once the store serves.

Going back to the earlier build after an upgraded store has served is not supported: the earlier
build refuses the store, and the copy is gone.

### A store from another build

The broker compares each store's tables, indexes and triggers with its own. When they differ it
warns in the terminal, in the web console and on its own output. The store was made by a build
other than this one, though it carries the same schema version. The broker still serves it, but a
check the store lacks is not being made, and an operation that needs a table the store lacks fails.
A store from before roadmaps, for one, cannot start a named conversation.

Do not reset it, which loses its work. Stop the broker and copy the store first. Then either
install the build that made it, or bring the store to this build's shape: create a fresh store
with this build and copy every row across, and check the warning has gone before going on. The
fresh store applies this build's checks to every row it is given, so some rows may be refused.
Do not leave a refused row out: stop, keep the copy, and get help, since a row left out is work
lost.

## Why the operator tools are separate from the agent's

An adapter that exposed an operator command to an agent would let an agent end a discussion over
its peers' objections, or read across every agent at once. So the separation is built in. The
operator operations are not on the type an adapter is handed, they are separate services on the
wire, and they are not compiled into `parley-mcp` at all. A test checks that last one by
reflecting over the real binary.

## Next

- [Security](security.md)
- [FAQ](faq.md)
