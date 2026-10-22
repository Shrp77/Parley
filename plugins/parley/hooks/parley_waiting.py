"""Tell an attended session when Parley mail is waiting for it.

Why this exists
---------------
Parley's inbox is pull: an agent collects its messages by calling ``parley_inbox``,
and it can only call anything when its session takes a turn. That is fine for an
agent the supervisor starts -- it is given a turn precisely because something is
waiting. It is not fine for the case nobody designed for, which is also the
common one: a repository a person has open.

Such a session reports ``live`` and never looks. A message can sit in it for
hours while every indicator says the deployment is healthy. The standing
instructions say to call ``parley_inbox`` when work starts, but that is a rule
nothing enforces.

Where it runs, and why each one
-------------------------------
Four events, in two pairs, because an agent can be deaf in two different ways.

``SessionStart`` and ``UserPromptSubmit`` catch the turn boundary a person
drives. They are not enough on their own: they only fire when a human types, so
an agent working through a long turn learns nothing until that turn is over.

``PostToolBatch`` fires once after every tool call in a batch resolves, *before
the next model request*. That is genuine mid-turn delivery -- an answer arriving
while the agent is still working reaches it during that work rather than on some
later prompt.

``Stop`` fires when the agent finishes responding, and is the interlock. Feedback
returned here continues the conversation so the model can act on it, which turns
"remember to check your mail" from a standing instruction into something an agent
cannot walk past: it may not go idle carrying uncollected mail. That does not
rescue a session already parked at an empty prompt -- nothing can, since a parked
session runs no code -- but it stops one entering that state with mail waiting.

Why the event comes from the payload
------------------------------------
Every hook payload carries ``hook_event_name``, so the shape of the output is
decided from that rather than from an argument in ``hooks.json``. An argument
would be a second copy of a fact the runtime already states, and two copies of a
fact disagree eventually: a mistyped mode would be invisible, because this script
swallows its own failures by design and would simply go quiet. Reading the
payload also means adding an event is one edit rather than two that must match.

An event this script does not recognise produces silence. Printing the wrong
shape is worse than printing nothing -- plain text on ``PostToolBatch`` is a
parse failure rather than a notice -- and silence is what every other failure
here already does.

What it deliberately does not do
--------------------------------
**It never prints a message's contents.** Not the payload, not the subject, not
the sender's words. SPEC.md 13 requires a peer's words to arrive wrapped in the
envelope that marks them as data rather than instruction, and ``parley_inbox``
is what applies that wrapping. Text injected by a hook arrives with no envelope
at all, in a channel the model has every reason to trust, which is precisely the
injection route the envelope exists to close. Counts are safe because a number
cannot carry an instruction. The agent still has to go and collect.

That rule is why the model-facing events use ``additionalContext`` and not
``systemMessage``. They are not two spellings of the same thing: ``systemMessage``
is shown to the person, ``additionalContext`` is delivered to the model. Using the
wrong one fails in whichever direction is silent -- a notice the model never sees,
or peer-adjacent text on a screen -- and both look like the hook working.

**It stays out of subagents.** A payload carrying ``agent_id`` is a subagent, and
collecting is destructive: the broker stamps ``delivered_utc`` as it hands a
message over, so whoever calls ``parley_inbox`` consumes it. A subagent that
collected would read the mail into a context that ends with the subagent, mark it
delivered, and leave the principal none the wiser and the notice permanently
silent. That converts unread mail into destroyed mail, which is worse than the
failure this file exists to fix. ``SubagentStop`` and ``TeammateIdle`` are not
wired for the same reason.

**It holds a turn from ending at most once.** ``Stop`` payloads carry
``stop_hook_active``, true when the turn is already continuing because a stop
hook asked it to. Returning feedback again on that pass is how a session
livelocks -- an agent that declines to collect would be handed the same notice
for ever -- so the second pass is silent and the turn ends. The runtime keeps its
own backstop at eight consecutive blocks, but arriving there means eight wasted
model turns and a warning; this never gets close, and a livelocked session is a
far worse outcome than a missed message.

The bound is one extra turn *per turn*, not one in total, and the same is true
of ``PostToolBatch``: while something stays uncollected the notice repeats on
every batch and at the end of every turn. Collecting stops it, because the
broker stamps ``delivered_utc`` as it hands the message over. What does not stop
it is a repository where collecting is impossible -- a ``.mcp.json`` and a
reachable broker are enough to make this file speak, and the Parley server
failing to start is not something it can see. There the notice recurs for ever, which is
loud, and deliberately so: it is the same trade SPEC.md 11.1 records for the
supervisor, where one wasted turn per new message is accepted because the
alternative is losing the message silently. A deployment that cannot collect
its mail is broken, and being told so on every turn is better than hours of
quiet.

File change notices
-------------------
A notice that a file this agent fetched has changed or gone is counted with
the mail, and like the mail it is only a count: never the file's name, which
its placer chose and which is as much a peer's words as a payload is. A notice
wakes nothing -- it waits for the next inbox check -- and holding a turn open
at ``Stop`` is asking for a turn, so there notices are mentioned only beside
mail that holds the turn anyway.

The count is asked for with ``--with-files``.

What it costs
-------------
Two things, both measured rather than estimated, and recorded here because §14's
habit is to write a cost down rather than argue it away.

One extra model turn per turn, not one in total, and the same repetition on
``PostToolBatch``: while something stays uncollected the notice returns on every
batch and at the end of every turn.

And about 150 ms of wall clock per invocation -- interpreter start, then one
counting query per launch entry -- which now lands after every tool batch rather
than only at a turn boundary. A turn with twenty batches pays roughly three
seconds. That is the price of mid-turn delivery and it is worth paying, but it is
not free, and a future maintainer wondering why turns feel slower in a Parley
repository should find the answer here rather than have to measure it again.

The per-entry part is why there are two bounds rather than one. A repository
answering in several projects asks each of their brokers, so ``TIMEOUT`` bounds
one question and ``BUDGET`` bounds all of them together; entries past the budget
are not asked. Sequential, and deliberately: asking in parallel would be faster
and would put threads in a file whose every other failure is handled by giving
up quietly.

It also stays silent when nothing is waiting. A hook that speaks on every turn
is one people learn to ignore, and this needs to be worth reading on the rare
turn it appears.

And it fails silently. Any error at all -- no database, no configuration,
malformed JSON, a schema this build has never seen -- exits zero with no output.
A session that cannot be worked in because its mail-check threw is a worse
outcome than one that does not know it has mail.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

# ${VAR:-default}, the form .mcp.json uses so one committed file works on every
# machine. Resolved here rather than assumed, because the workspace and database
# paths are exactly the entries that carry it.
EXPANSION = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")

# Events whose plain stdout is added to the model's context as-is.
SPOKEN = frozenset({"SessionStart", "UserPromptSubmit"})

# Events that take structured output, where the notice travels as
# hookSpecificOutput.additionalContext.
STRUCTURED = frozenset({"PostToolBatch", "Stop"})

# How long one question may take, and how long all of a repository's questions
# may take between them. Both matter because a repository answering in several
# projects asks each of their brokers in turn: one bound alone would either drop
# a broker that is merely busy, or let three slow ones hold a person's turn for
# fifteen seconds. Entries past the budget are not asked, which is the same
# "nothing waiting" every other failure here reports.
TIMEOUT = 5
BUDGET = 10


def expand(value: str) -> str:
    """Resolve ${VAR:-default} against the environment."""
    return EXPANSION.sub(
        lambda m: os.environ.get(m.group(1)) or (m.group(2) or ""), value
    )


def launches(root: Path) -> list[tuple[str, str, str, str]]:
    """Every Parley launch entry in this repository's .mcp.json: its agent, broker, project and machine."""
    manifest = root / ".mcp.json"

    if not manifest.is_file():
        return []

    servers = json.loads(manifest.read_text(encoding="utf-8-sig")).get("mcpServers", {})
    found: list[tuple[str, str, str, str]] = []

    # `parley` is the key a repository enrolled into no named project holds;
    # enrolment into a project writes `parley-<project>`, one for each project the
    # repository answers in. Both are read, or a repository enrolled into a named
    # project would find no agent and say nothing.
    for key, server in servers.items():
        if key != "parley" and not key.startswith("parley-"):
            continue

        env = server.get("env", {}) if isinstance(server, dict) else {}
        agent = expand(env.get("PARLEY_AGENT", "")).strip()

        if not agent:
            continue

        # Absent is the ordinary case and means the local broker, so unlike the
        # agent name this is not a reason to give up.
        broker = expand(env.get("PARLEY_BROKER", "")).strip()

        # Named the way the adapter itself resolves it: an address wins, then
        # PARLEY_PROJECT. The key's suffix stands in only when neither is written,
        # because `parley` on a machine naming several projects refuses to guess.
        project = expand(env.get("PARLEY_PROJECT", "")).strip()

        if not project and not broker and key.startswith("parley-"):
            project = key[len("parley-"):]

        # A launch configuration names a role, and the adapter appends this
        # machine -- or the machine the entry names in PARLEY_MACHINE, as a
        # container's does. `parley waiting` completes a bare role the same way,
        # so the entry's machine is handed to it rather than a second
        # normalisation being written here to drift from the adapter's.
        machine = expand(env.get("PARLEY_MACHINE", "")).strip()

        entry = (agent, broker, project, machine)

        if entry not in found:
            found.append(entry)

    return found


def waiting(
    broker: str,
    agent: str,
    project: str = "",
    machine: str = "",
    root: Path | None = None,
    budget: float = TIMEOUT,
) -> tuple[int, int, int]:
    """Ask the broker what is waiting: uncollected messages, polls owed a vote, and file change notices."""
    # Asked rather than read. One process owns the store (SPEC.md section 4), it
    # may be on another machine entirely, and SQLite in WAL mode needs coherent
    # shared memory between everything touching it -- so a hook reaching for the
    # file is not slow, it is the corrupt case.
    #
    # `parley waiting` reports counts and takes nothing: it marks nothing
    # delivered, which is what makes it safe to call
    # from something that is not going to act on the result.
    tool = shutil.which("parley")

    if tool is None:
        return 0, 0, 0

    command = [tool]

    if broker:
        command += ["--broker", broker]
    elif project:
        command += ["--project", project]

    command += ["waiting", agent]

    # Bounded, because this runs in front of a person waiting for their turn to
    # start. A broker that is slow to answer must cost a moment and then be
    # dropped; the caller treats any failure as "nothing waiting", which is the
    # safe direction -- a session that does not know it has mail is a great deal
    # better than one that cannot be worked in.
    #
    # File change notices are a third count, asked for with --with-files.
    child = dict(os.environ)

    if machine:
        child["PARLEY_MACHINE"] = machine

    # Run from the repository's root, because `parley` takes a project from the
    # repository it runs in when nothing else names one, and the one meant is this.
    here = str(root) if root else None

    def ask(words: list[str]) -> subprocess.CompletedProcess[str] | None:
        """Ask once, or give up on this entry."""
        # **Every way one question can fail is caught here, because there are other
        # entries after it.** A broker that accepts a connection and never answers
        # raises TimeoutExpired; left to reach the blanket `except` at the bottom of
        # this file it would end the run, so one project slow to answer would silence
        # the mail in all the others.
        try:
            return subprocess.run(
                words, capture_output=True, text=True, timeout=budget, check=False, env=child, cwd=here
            )
        except (subprocess.TimeoutExpired, OSError):
            return None

    finished = ask(command + ["--with-files"])

    if finished is None or finished.returncode != 0:
        return 0, 0, 0

    # Counts, or nothing at all: a tool answering something this cannot read must
    # not take the whole check down with it.
    try:
        counts = [int(count) for count in finished.stdout.split()]

        return counts[0], counts[1], counts[2]
    except (IndexError, ValueError):
        return 0, 0, 0


def tally(unread: int, owed: int, notices: int = 0) -> str:
    """Say what is waiting, in counts only."""
    parts = []

    if unread:
        parts.append(f"{unread} uncollected message{'s' if unread != 1 else ''}")

    if owed:
        parts.append(f"{owed} closure poll{'s' if owed != 1 else ''} you owe a vote")

    # A count and never a name: a file's name was chosen by whoever placed it,
    # and is as much a peer's words as a payload is.
    if notices:
        parts.append(
            f"{notices} notice{'s' if notices != 1 else ''} that a file you fetched has changed or gone"
        )

    return " and ".join(parts)


# Every notice ends with this. The hook can see that mail is waiting but not
# whether this session can collect it: the counts come from the operator's tool,
# while ``parley_inbox`` comes from an MCP server that may have failed to start, been
# removed from ``.mcp.json``, or never been added. A repository can therefore be
# configured enough to make this file speak and not equipped to act on it.
#
# Without an explicit way out, an agent in that state does not shrug. It was told
# to collect, from a channel it trusts, so it goes looking for the tool and never
# answers the question the person actually asked. The notice would hijack the turn.
#
# The escape has to be spelled out for the same reason the instruction works at
# all. It is deliberately narrow: a session that *can* collect gets no excuse
# from it, so the loudness that makes this file worth having is untouched.
UNAVAILABLE = (
    " If `parley_inbox` is not among your tools, this repository's Parley server "
    "is not running: say so in one line and carry on with what you were asked, "
    "rather than going to look for it."
)


def sentence(unread: int, owed: int, event: str, notices: int = 0) -> str:
    """Say what is waiting, and what to do about it, for one event."""
    # The Stop wording differs because the situation does: the agent is about to
    # go idle, and going idle is the thing that loses the message. Everywhere
    # else it is a notice; here it is the reason the turn has not ended.
    if event == "Stop":
        return (
            f"Parley: {tally(unread, owed, notices)}. Do not finish here - call "
            "`parley_inbox` first and deal with what it hands you. The inbox is "
            "pull, so nothing arrives until you do, and an idle session collects "
            "nothing. This notice carries no message content by design; the "
            "envelope comes with the message." + UNAVAILABLE
        )

    return (
        f"Parley: {tally(unread, owed, notices)}. Call `parley_inbox` to collect it - the "
        "inbox is pull, so nothing arrives until you do. This notice carries no "
        "message content by design; the envelope comes with the message." + UNAVAILABLE
    )


def render(notice: str, event: str) -> str:
    """Shape one notice for the event that asked for it."""
    if event in STRUCTURED:
        return json.dumps(
            {
                "hookSpecificOutput": {
                    "hookEventName": event,
                    "additionalContext": notice,
                }
            }
        )

    return notice


def main() -> int:
    try:
        payload = json.loads(sys.stdin.read() or "{}")
    except (json.JSONDecodeError, OSError):
        payload = {}

    if not isinstance(payload, dict):
        return 0

    event = payload.get("hook_event_name") or ""

    if event not in SPOKEN and event not in STRUCTURED:
        return 0

    # A subagent must not collect: collecting is destructive and the principal
    # would never see what the subagent consumed.
    if payload.get("agent_id"):
        return 0

    # Already continuing because this hook asked it to. Speaking again livelocks.
    if event == "Stop" and payload.get("stop_hook_active"):
        return 0

    root = Path(payload.get("cwd") or os.getcwd())

    entries = launches(root)

    if not entries:
        return 0

    # Whether the broker is reachable is a question only asking it can answer, and `waiting` treats every failure as nothing
    # waiting -- so an unreachable broker costs this hook a moment and says
    # nothing, which is the right outcome for something that runs in front of a
    # person waiting for their turn to start.
    #
    # Added across entries: a repository answering in two projects has one inbox
    # in each, and mail in either is mail this session has to collect.
    unread = owed = notices = 0
    deadline = time.monotonic() + BUDGET

    for agent, broker, project, machine in entries:
        left = deadline - time.monotonic()

        # Bounded across the entries as well as within one, because this runs in
        # front of a person waiting for their turn to start: three projects whose
        # brokers are all slow would otherwise cost a full TIMEOUT each.
        if left <= 0:
            break

        more_unread, more_owed, more_notices = waiting(
            broker, agent, project, machine, root, min(TIMEOUT, left)
        )

        unread += more_unread
        owed += more_owed
        notices += more_notices

    # A change notice wakes nothing (SPEC.md 9.5): it is told at the next inbox
    # check and never asks for a turn. Holding a turn open at Stop is asking for
    # one, so notices alone do not; they are mentioned there only beside mail
    # that does.
    if event == "Stop" and not (unread or owed):
        notices = 0

    if unread or owed or notices:
        print(render(sentence(unread, owed, event, notices), event))

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # noqa: BLE001 - see the module docstring: never break a session
        sys.exit(0)
