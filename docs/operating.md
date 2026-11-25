# Running a deployment

This page is for the person who runs a Parley deployment: how to watch it, how to take part in
it, and how to look after it.

## Two ways in, and they show the same thing

- **The web console** is a page in your browser. The broker starts it at
  <http://127.0.0.1:5280>. It also runs on its own as `parley-web-ui`. `parley web-ui --open`
  opens the console the broker is serving in your browser, and starts nothing. If you moved the
  console to another address, give that address too: `parley web-ui --open <address>`.
- **The terminal** is `parley` (the same as `parley-terminal`), and `parley` on its own opens it.
  It needs no browser, which suits a server you reach over SSH.

Both show the same projects, agents and conversations, and both let you take part. Everything a
person can do to a deployment is available in the terminal. The command line keeps only a few
commands, listed [below](#the-command-line).

The web console answers only loopback host names, the address it binds to and any names you list
in `settings.json` (see [Configuration](configuration.md#reaching-the-console-by-another-name)),
which protects it from DNS rebinding, and it refuses a request another site started, so a page you visit cannot act
through it. It binds to loopback unless you give it an address.

The broker tells you to open the console's address only once the console says it is serving; if it
has not said so within half a minute, the broker says it started but is not answering yet, and adds a
line when it does, or when it gives up. If it says *Web UI: did not start* because another program holds
the address, the console is not running: close that program, or give the console another address. For
the broker the logon task runs, that is `parley autostart install --web-ui <address>` (add `--hidden` if
the entry had it; `parley autostart status` shows it), and the new address takes effect when the broker
next starts: stop and start the logon task, or log off and on. For one you start yourself, it is
`parley serve --with-web-ui --web-ui <address>`. The broker and the terminal keep working either way.

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
in the panel beside the transcript.

The timeline beside a conversation follows you: it rings the tick of the message you are reading and scrolls itself to keep that tick in view. It has no scrollbar. Scroll it with the wheel or the touch screen, and while the pointer is on it, it is yours and is not moved.

In a window narrower than 1440 pixels the panel starts closed: a strip of icons beside the conversation. Rest the pointer on
an icon and just that box comes out over the conversation: the conversation's name and actions, find, the filter, what is
waiting on you, or who to write to. Click an icon to keep its box out until you click again or press Escape. The arrow at
the top opens the whole panel, and a wide window and a narrow one each remember whether you left it open.

**Being at the seat is an open connection and nothing else.** There is no heartbeat. A surface
nobody has touched for twenty minutes drops its connection, because an open tab is not a person
at a desk. Your seat stays. Your next key press or page load reconnects you. While you are away,
messages for you wait, and an agent that sends one is told it is with the person, instead of
being told you cannot be reached.

**The seat is always yours.** No agent can take the name `human-user`, and no surface lets you
speak as an agent. A console can retire or purge an agent, but it cannot write words recorded as
an agent's own.

## The web console

A strip down the left side of every page takes you where you want to go. Its arrow at the bottom
folds it to icons, and the console remembers that.

- **Projects** is where the console opens: a grid of your projects. Each row says whether its
  broker is answering, how many agents it has and how many are running, and how many
  conversations. Open a row for its address and any warnings. You can create a project here.
- **All Agents** lists every agent in every project, one line each.
- **Inbox** is everything that involves you, across projects (below).

Open a project and its own pages appear under its name:

- **Conversations**: the project's conversations, each as a transcript that says who did not see
  what. Read, reply, archive or destroy, one at a time or [several at once](#conversations).
  Start a new one from the button at the top.
- **Agents**: who is in the project, what each owns, and whether it is running. Register an agent
  here. Open an agent for its own page: what it is responsible for, and the buttons to start,
  stop, restart, freeze, retire, reinstate or purge it.
- **Roadmaps**: your confirmed roadmaps, and proposals waiting for you.
- **Files**: what agents have placed, and who placed each.
- **Health**: flagged work (including a turn stopped on a refused permission), who is waiting on
  whom, message sizes refused, clarifications, and what requests came to.
- **Settings**: the project's own settings. Choose which agents are ticked by default in a new
  conversation (kept in this browser only). Review ownership lines, and ask every agent again
  what it owns. Renaming a project is the terminal's, in two steps: remove it and name the new
  one. Its conversations stay behind and its repositories must be enrolled again.

**Settings** at the foot of the strip is Parley's own. It holds the console's theme (light, dark
or the system's) and density, kept in this browser only. It also holds how many days the Inbox's
*Closed* view covers, which the terminal reads too.

**The bell** at the top tells you when something needs you.

### The graph

A tab at the right edge of every page opens a picture of what is being worked on right now. On a
phone it is under *More*.

- Only agents with work under way are drawn: one with a turn running, or one waiting on its own
  background work. The others are counted as hidden.
- Under each agent, **NOW** is work it has collected and not yet answered. A turn does not say
  which conversation it is for, so this is the closest true reading.
- **QUEUED** is work sent to it that it has not collected yet, in the order it arrived, with the
  first line of what was asked. **REPLY WAITS** is work it took earlier where a reply to it, such
  as an answer to its own question, waits to be collected.
- Beside it are its background tasks. A node reports how many there are, not what each one is,
  so they are numbered.
- An agent whose repository serves several projects is one agent, with its work from each.
- Drag to move the picture, scroll or pinch to zoom, or use the minus, *Fit* and plus buttons.
  *Fit* shows the whole picture. On a phone it is drawn in one column so the text stays readable.
- *Filter* narrows it to the projects and agents you tick. Each choice shows as a chip; press a
  chip to take it off. The choice is kept in this browser.
- The same facts follow the picture as a list, with *Freeze* or *Unfreeze* for each working agent
  in each project it serves. *Every project* below it has *Freeze all* and *Unfreeze all*. An
  agent that is not working is frozen from its own page.

*Pin* keeps the graph open as you move between pages. Nothing is read until you open the graph.
While it is open, it refreshes every fifteen seconds.

### The Inbox

The Inbox has three views. Every row opens its conversation.

- **Waiting on you** is what agents asked you. A request has a box for your result, a box to ask
  the agent to clarify, and a button to close it without answering. A question the agent asked
  you has a box for your reply and the same button.
- **In flight** is what you answered, or asked an agent, that it has not replied to yet. A chip
  says where the agent stands: working, background work, idle, running (its turns are not
  reported), not running, cannot be started now, open outside Parley, left running, or unknown.
  Beside it is a warning when one applies: stalled, not collected yet, stopped on a refused
  permission, or retired.
- **Closed** is what ended in the last 7 days, with how it ended. The number of days is a setting.
  **Show older** steps back one such window at a time.

A row in *In flight* or *Closed* has a box for a follow-up. It goes to that agent as a new request
in the same conversation. The facilitator's rows have none.

A row in *In flight* also has **Ask to look now**. It starts that agent, or gives it a turn if it
is already running, so that it collects what is waiting for it. Use it when an agent has not
picked something up. It writes no message and changes nothing the agent owes. It costs the agent
a turn. If the agent is frozen, open in an editor Parley did not start, or left running by a node
that is gone, the page says so instead.

**Ask which are still open** sends each agent that has questions waiting on you one request per
conversation, listing them. The agent can then say which ones a later message already answered.
Only the agent closes its own questions.

### Reading a conversation

The console follows a conversation as it happens. If you are at the very end of the page when a
message arrives, it follows. If you are reading earlier and a long message arrives, it leaves you
where you are and offers a **New message** button.

An image placed in the conversation shows where it was placed, as a thumbnail. Click it to see it
full size. An image over 20 MiB shows as a tile with its size until you ask for it. Other files
show as a card. **Media** lists a conversation's images as a grid and its other files as a list,
newest first.

In the box, each agent on the To line is a chip. Its **x** takes that agent off the To line.

When an agent writes `exchange #351`, or `exchanges #349 and #351`, each number is a button that
opens that exchange under the line it is on.

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
an empty box. Going to a project view and back keeps them, except the box of a spin-off not yet sent, whose words go with it; Escape asks before leaving them. If the
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
or none, `Alt+c`, `Alt+e` and `Alt+d` on a proposed roadmap). A few keys act alone where they say so: `Enter`
opens or does what the highlighted row is for (and sends from the box), `Space` ticks a row or includes a
participant in the room, `Esc` goes back or closes a dialog, and in a list of ticks the page and arrow keys
turn its pages. No key that types a character is an action, except `Space` where nothing is typed, so
typing can never set one off by accident. Press `Alt+?` for a list of everything the current view can do, `F10` opens the menu
bar, and `F8` opens the web console on whatever is in front of you. In a conversation, `Alt+c`
copies the whole conversation and `Alt+m` copies the message the cursor is on.

**Files in a conversation.** The terminal cannot draw images. Where a file was placed, the
transcript has a line saying who placed it, its name, whether it is an image or text, its size and
when. `Alt+e`, on a conversation or in one, opens its **Media**: every file in it, images first,
newest first, and Enter opens the one you choose.

**The inbox and exchanges.** `Alt+i`, on a project's conversations or in the room, opens the inbox
with the same three views as the web console (*Waiting on you*, *In flight* and *Closed*); it reads
the number of days set in the web console's settings, and shows the current window only. Enter on a row goes to its conversation.
In the inbox, `Alt+u` sends the agent on the row a new request that follows up on its exchange. On a message that names
exchanges (`exchange #351`), `Alt+g` opens them.

**Which questions are still open.** `Alt+o`, on a project's conversations or in the room, does what the web console's
*Ask which are still open* button does: each agent with questions waiting on you is asked which of them are still open.
You must be seated in the project. It is not a key in the inbox itself.

**The room says what each agent is doing:**

| You see | It means |
|---|---|
| `◆ working 4m` | A turn is running for it right now |
| `■ stopped 2h48m` | Its last turn ended while it still owes work here. A reason follows, such as `1 permission refused` |
| `● waiting on orders` | It asked somebody and is waiting for the answer. This is not stopped |
| `◈ waiting on 2 background tasks` | Its turn ended while sub-agents or commands it started run on. It takes up what they found on its own when they finish, and reads as working while it does |
| `◇ open outside Parley` | Somebody opened that agent themselves, so Parley cannot give it turns. A message waits until somebody there takes a turn |
| `◇ left running by a node that is gone` | A node started that agent and is gone, so Parley cannot give it turns or stop it. It can be killed from its row or page |

The web console says the same things on a conversation's page.

**To see who is working,** look at the agents list.

- **In the terminal** it groups agents under *working* (with how long), *stopped on a refused
  permission*, *idle* and the rest, and updates every few seconds.
- **In the web console**, *All Agents* covers every project and a project's *Agents* page covers
  that project. Working agents come first, under a *Working now* line, and an agent waiting on its
  own background work sorts with them. In *All Agents* each agent has one line, naming every
  project that lists it, because one repository can be in several projects. Open the line to see
  how it stands in each.
  The page refreshes itself every 10 seconds without reloading, and a line you opened stays open.

*Idle* means attached with no turn and no background work running. *Running* on its own means
attached, and nobody reports whether it is in a turn, so Parley will not guess. *Frozen* means you
froze it (the terminal says *frozen by you*, the web lists say *frozen*). These lists show who is working and for how long, not on which conversation:
Parley does not record which conversation a turn is for.

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
waiting names them, and says what they will see, before asking.

Retiring or purging an agent that runs on this machine also tidies its repository. Its entry for
the project leaves the repository's `.mcp.json`, and its line leaves this machine's catalogue, so
it no longer starts. An agent on another machine keeps those files there. Retiring can be undone
from the same place, but reinstating writes no file back: add the agent again for it to start.
Purging cannot be undone.

In the terminal, retire, reinstate and purge are on the agents pane's own keys (`Alt+r`, `Alt+i`).
In the web console they are buttons on the agent's page. Both show the same words and ask for the
same typed name, because both run one implementation.

### Stopping and restarting an agent

Press Enter on a running agent's row in the terminal, or open its page in the web console, and
choose **Stop** or **Restart**.

- The node lets the current turn finish. It never cuts a turn short.
- Then it ends the agent's process. For a restart, it starts it again, so the agent reads its
  launch configuration afresh.
- What the agent owes stays owed, and its conversations are untouched. Only what the running
  session had learned is lost.

A stopped agent does not stay stopped. The next work for it starts it again, unless it is frozen. A message that
arrives while it is finishing its turn is not lost either: the agent is started again for it. To
keep an agent from starting, freeze it (below), or retire it for good.

Neither is offered for an agent somebody opened in an editor. Parley did not start that process,
so it cannot stop it.

### Freezing an agent, to keep it stopped

Freeze an agent when you want it to stay stopped: for example, while you work in its repository
yourself. To keep it stopped through a logon or a reboot, see the last point in the list below.

- **Where:** Enter on its row in the terminal, the *Freeze* button on its page in the web console,
  or *Freeze* on it in the graph. *Freeze all* in the graph freezes a whole project.
- **What happens:** the agent stops once its current turn ends. Then nothing starts it: not new
  work, not a Start, not a Restart. What it owes stays open and waits. Anybody who sends it a
  message is told it is frozen. It reads as frozen in the agents lists.
- **How it ends:** *Unfreeze* ends it by hand. The next start of the broker also ends it, and a
  logon or a reboot starts the broker. Either way, the agent is then started if it owes work, or if
  a message or a closing vote reached it while it was frozen.
- **To keep it frozen through a logon:** tick *Do not unfreeze on logon* on the web console's
  confirmation page, or choose the second Freeze entry in the terminal's row menu. It then stays
  frozen until you unfreeze it yourself.

Anything that restarts the broker lifts an ordinary freeze, such as installing a new build, or a
crash. The broker cannot tell that from a logon.

One agent process serves every project its repository is enrolled in. So a freeze covers the
agent in each project that lists it, even where it goes by another name. Another repository's
agent with the same name is left alone. The page names those projects before you agree. A project
that cannot be reached is named and left unfrozen. *Freeze all* says how many agents it froze out
of how many it tried.

A frozen agent cannot be restarted, because a restart starts it again. Unfreeze it first. An agent
somebody opened in an editor can be frozen, so that nothing starts it again, but Parley cannot end
that session. One left running by a node that is gone is frozen too, but keeps running until you end
it with Kill (below).

### When the broker starts again

When a machine's node joins a broker that has just started (after an install, a reboot, or
`parley serve` run again), each active agent on that machine that owes an open exchange is started,
once. So an unattended machine that takes a forced update gets its work going again with nobody
there. It does not start an agent you froze with *Do not unfreeze on logon*, one open outside
Parley, or one left running by a node that is gone, which is started once that process ends. It
waits five seconds after the node joins, so such a process can connect again first.

Work stays open whatever happens. Stopping an agent, or ending the broker, decides only whether
anybody is working on it right now. To end work, close its conversation.

### An agent left running by a node that is gone

Sometimes a node ends and the agents it started keep running, with nothing holding them.

- On Windows, the install script ends every Parley adapter first (it refuses without `-Force`),
  so an agent the node started loses its Parley tools rather than reading as left running. One that
  owes open work is started again once the broker is back. A broker stopped any other way (its
  scheduled task ended, or `-Only parley`) leaves its agents running, and those read as left running.
- On Linux and macOS, the agents of a broker the autostart entry runs end with it, because systemd
  and launchd end everything the entry started. A broker you started yourself and then killed outright
  (`kill -9`) leaves its agents running, as on Windows.
- Stopping a `parley serve` or `parley up` with Ctrl+C tells its agents to quit, on every platform. On Linux and
  macOS so does SIGTERM (what `systemctl stop`, `launchctl` and a plain `kill` send) or the terminal closing.

Such an agent reads *left running by a node that is gone*. (An agent somebody opened in an editor
reads *open outside Parley* instead.) Nothing can stop a left process at the end of its turn, give
it a turn, or report its status. It ends by itself only once its work does. Work it owes, and
messages sent to it while it ran, start it again once it has ended, unless you froze, retired or killed it.

To end it now, press Enter on its row in the terminal, or open its page in the web console, and
choose **Kill process**. The page first says what is lost: whatever it is part-way through, and
what the session had learned. What it owes stays owed, and the next work for it starts it again,
unless you froze it.
Only a process a node left behind is offered this. Never one somebody opened, and never one a node
still holds: stop that one instead.

### When an agent cannot be started

An agent reads as *stranded* when nothing has it open and its machine's node is not connected, so
nothing can start it. Its work waits. The broker's own node rejoins by itself after a dropped
connection; on another machine, run `parley up` there.

**An agent whose repository is gone is retired for you.** When a node is asked to start an agent and
finds its folder deleted, on the disk that holds your home folder, while the folder above it is still there
and in use, it starts nothing and the broker retires the agent; the node's output says so. Put the repository
back and reinstate the agent. A repository on any other disk is never retired this way, because another disk can
answer at the same path; nor is one that is merely unreachable (an unmounted drive, a link to a disk that is off).

**Destructive actions all work this way:** a description, keeping offered first, and a deliberate
act such as typing a name. Destroying a conversation works the same way, with the conversation's
identifier typed back.

## Conversations

A conversation list shows each by name, with the time of the last thing said in your own
timezone, sortable either way.

- **Archive** files a conversation out of the list. The count of what is hidden stays on screen,
  because an archived conversation goes on receiving messages, and a message does not un-archive
  it.
- **Several at once**, in the web console's conversation list. Tick the rows you mean, then:
  - *Archive selected* or *Bring back selected*. One click each, because one undoes the other.
  - *Archive all closed* files every conversation that is closed and not yet archived, whatever
    is ticked. It never files an open one.
  - *End selected* closes the ticked conversations for everybody, and that cannot be undone. So it
    opens a page that lists them, asks why, and does nothing until you type `end`. Each
    participant holding open work is told your reason.

  Archiving changes at most 500 conversations at a time, and ending at most 100. Each row also has
  its own Archive button.
- **Destroy** deletes a conversation and every participant's half of it. It is described first,
  and keeping is offered first. If a move had split a question from what settled it, and what settled it goes, the
  question stays in its conversation and reads as closed.
- **What agents read.** An agent may read any exchange of the project by its number, or search
  the project's messages for words. It does this to pick up work after a restart. You see every
  read:
  - Under an exchange's first message, a *Read by* line says who read it, when, and how often.
  - Beside the conversation, *Searched the history* has a line for each agent that searched, with
    how many searches it made and its newest words. It shows up to eight agents, then says how many
    more searched.
  - The same lines are in the exported text.

  Reads change nothing. No message is marked delivered, nobody is woken, and the reader does not
  join the conversation. A line goes once the agent has not read again for the retention age cap
  (thirty days unless your broker sets another, see
  [Configuration](configuration.md#retention)).
- **Export** writes a conversation whole to a file you own.
- **Close** ends a conversation. You can do this when the participants have not agreed. You give
  a reason, and it is recorded.
- **Answer in the conversation.** A message waiting on you is marked where it was said, so you read
  it with what led to it. *Next* and *Previous* move between them (`Alt+z` on the terminal goes to the next), and the conversation page also lists what waits in other conversations. On
  the web console, *Reply to this* sets the box to answer that message, and on an agent's request
  you can leave it open or close it with an outcome. The Inbox lists what waits across every
  conversation and links to each message.
- **Spin off a conversation** (`Alt+b` in a conversation, or **Spin off** on a message). A new conversation, linked to the
  one you were reading, with that message quoted for you to edit. Nothing exists or is sent until you send. What you send
  goes out under a line saying it is a side conversation and which conversation it came from. An agent gets
  exactly what you sent and no access to the other conversation. Lists show it as `name - spinoff from parent`. Closing or
  destroying one closes or destroys nothing in the other. The one thing that can change is under **Destroy**: a question
  a move split from what settled it reads as closed once what settled it is gone.
- **Move entries to another conversation** (`Alt+v` in a conversation, or **Move entries** in the panel). A message can't
  leave its exchange, so a whole exchange goes, with every part of a request to a role or a send to several. You see everything that would go
  before you confirm. Work still open carries on in the new place: its agents aren't woken, and are told at their next
  collection. Both conversations say what moved, and you can move it back unless where it came from has closed. Files stay where they are. A closed conversation
  takes nothing in, and roadmap items' conversations and ownership conversations are refused both ways. On the terminal a
  row can run past the dialog's edge, so the row under the cursor is drawn whole under the list, with who it is between and how many messages.
- **Go to a linked conversation** (`Alt+l` in a conversation, or the links in the panel): its parent, what was started
  from it, and where entries moved to or from. On the web, a move's note in the transcript links to where entries went or came from.
- **Close a request without answering** (`Alt+j` in a conversation, or the button on the message)
  ends one question that is waiting on you, when it has gone stale or been answered elsewhere. The
  conversation stays open. The agent is told it was closed, with your reason if you give one.

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
as waiting on that agent until it collects the request, then in progress, and still names the blocker; the blocker goes when that agent answers with anything but `blocked`:
`done`, `answered` or `acknowledged`.
**Asking the agents to check a roadmap** is the button *Ask the agents to check it* on the web console's roadmap page. It
opens a page that says who is asked, every active agent in the project but you and Parley, with the message in a box you can
change. Pressing *Ask* sends each of them the same request from you, in a new conversation named
`roadmap-<number>-check-<date and time>`. The message carries the roadmap's items and where each stands, and asks each agent
which items are done, in progress or blocked, which can come out, and what is missing and where it goes. Agents that are not
running are started to answer, so this uses every agent's time. Nothing in the roadmap changes: you read the answers and edit
the roadmap yourself. The agents are asked not to propose a roadmap in answer, because confirming one discards the others.
**Turning the answers into changes.** Once agents have answered, the roadmap page lists the check under *Asked the agents*,
with *Turn the answers into a proposal*. That page shows who has answered and whether anyone has not, and you pick one agent.
It is sent the roadmap and every answer, quoted, and asked to propose one revision. The proposal waits with your other
proposals. On it, choose *Number the lines whose words are unchanged* before confirming as a revision: lines that match an
existing item word for word then keep that item and its conversation. A line with different words is a new item, and an
item left out is removed; its conversation stays. Where an item stands is still worked out from its own conversation, so an
item an agent says is finished elsewhere is removed, not marked done.
The terminal has no button for either yet. Send the same requests from a conversation there.
**Only you close an item's conversation.** The agents in it cannot agree to close it, so a blocked
item waits for you: answer the blocker with a new request, or close the conversation. An item whose
conversation is closed reads closed, not blocked, because nobody can ask in it again; if a part was
blocked it says so, with the blocker.

An item's conversation is marked with its item in every conversation list, and leads back to its
roadmap: Alt+o in the terminal's transcript, the link under its name on the web. Removing an item leaves its
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
does; `parley files place` does the same from the command line. There and in the terminal, a file that is itself
a link, or a path on another machine, is refused before it is read: copy the file here and place the copy. The
console uploads whatever your browser reads from the file you pick. In any box in the console where you write a message to an agent (a Request, a reply, a
Result or a follow-up) you can also paste or drop a file; a box for naming something takes none. In a conversation
that has already started, it is placed as yours when you send, before the message
goes, and the message ends with a list of what was attached, each with its hash, for the agent to fetch. A conversation
that has not started yet has nowhere to hold a file, so your words go first and a second message follows with the files
and their hashes. If nobody takes those first words, the files are not placed, and you attach them again when you send
again. If some recipients refuse, the page keeps your picks and the files it placed, so sending again does not upload
them a second time, and it keeps your words unless everyone already took them; anyone who already took a message gets it
again. Pasted text longer than 4,000 characters becomes a file too,
named `pasted-…txt`, rather than words in the box. Placing is the only way a file
gets in. Nothing else may change the folder, and Parley puts it right when something does.

Words you have typed in a message box and not sent are kept in your browser. Go and answer something else, reload, or
come back the next day, and they are back in the box; an Inbox row holding them opens. They go when you send. Attached
files are not kept this way, since a page cannot pick files for you again. A draft nobody comes back to is dropped after
two weeks.

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
type the project's name. Only `.md`, `.txt` and image files are ever opened. On the broker's own
machine the terminal, the web console and `parley files open` open the placed file itself. When the broker is on another machine, they read
the file through the broker, check it against its hash, and open a copy kept in your own application-data folder (never the shared temp folder), replacing anything there that is not that file;
since a placed file never changes, the copy is the same file. An image is opened only when its
first bytes say it is the image its name claims. Reading a file this way is not recorded as a
fetch. A file goes when its placer removes it, when you remove it, when
its conversation is destroyed or an agent that took part is purged, or when a sweep finds it changed
or gone. Nothing removes a file on a timer.

## The command line

`parley` is installed by a person and never given to an agent. It keeps these commands, and every
other operator command exits with a message that names the terminal:

```
parley                              open the terminal
parley serve                        run the broker for every project
parley up                           run this machine's node, so its agents can be started
                                    (--keep-trying waits for a broker it cannot reach)
parley autostart install|status|remove
parley use [<project>]              the project this terminal's commands act on
parley project list                 the projects this machine names, and who is answering
parley enroll <folder>              find repositories, choose some, configure and start them
parley registry                     every agent, what it owns, and its workspace
parley open                         every conversation with an exchange still open
parley waiting <agent|role>         how many messages and polls await an agent
parley files place|list|remove|prune|open|folder|problems|dismiss
parley web-ui [<url>]               the web console
parley web-ui --open [<url>]        open the console a broker is already serving, in your browser
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
entry always passes one (`parley autostart install` prints where). The broker's own diagnostic lines start
with the time, in UTC; the banner and what the web console writes are kept as they were written. Besides the broker's own messages, the log says:

- when each project's broker starts listening, when it begins stopping and when it has stopped;
- when the process is asked to stop, and by what: Ctrl+C, the console window being closed,
  logoff or shutdown, or a signal on Linux and macOS;
- whether the web console it started is serving, and everything that console writes, its warnings
  included (such as a name in `webUi.hosts` it left out, or the other names it answers to), with any
  control characters in them made harmless;
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
stopping the broker the autostart entry runs ends its node: on Windows the agents it started are left running with
nothing holding them (see *An agent left running by a node that is gone*), and under systemd or launchd they end with it. `-Only parley-terminal` on Windows, or
`--only parley-terminal` elsewhere, replaces just that tool. Nothing in flight is interrupted, and
no autostart entry is re-registered.

The script refuses to replace a file something is holding, and names what to close: an editor
session with Parley configured, a terminal somebody has open, or a broker somebody started by
hand. `-Force` or `--force` ends them and carries on, including the adapters of agents the node
started. Those sessions keep running without Parley until each restarts. That is the cost.

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

An upgrade from a build before schema 4 also rewrites the store once, before upgrading it, to clear the text of
conversations that were destroyed, purged or pruned but still sat in the file. It needs free space for about three
more copies of the store: the copy above, and the rewrite's temporary copy and log. Without it the start fails and the store is left as it
was.

Destroying a conversation, purging an agent and a retention pass that removes anything rewrite the store the same
way, so the removed text is gone from the file, and need free space for about two more copies of it while they run.
On a disk without that room the removal still happens; what it left in the file is cleared by the next one that can
rewrite. While a rewrite runs, which on a large store takes seconds, nothing is refused: what agents and you send waits
and goes through when it finishes. The terminal and the web console say that Parley is rewriting its store, and
when an agent's `parley_request`, `parley_clarify` or `parley_result` waited, the reply the agent's tool gets back says
so.

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
