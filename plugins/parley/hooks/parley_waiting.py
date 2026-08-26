"""Tell an attended session when Parley mail is waiting for it.

Why this exists
---------------
Parley's inbox is pull: an agent collects its messages by calling ``parley_inbox``,
and it can only call anything when its session takes a turn. That is fine for an
agent the supervisor starts -- it is given a turn precisely because something is
waiting. It is not fine for the case nobody designed for, which is also the
common one: a repository a person has open.

Such a session sweeps happily, reports ``live``, and never looks. A message can
sit in it for hours while every indicator says the deployment is healthy -- and
one did, for four and a half hours, which is what prompted this file. The
standing instructions already say to call ``parley_inbox`` when work starts;
that turned out to be a rule nothing enforced and everybody forgot.

So this runs on the events that mean "a person is interacting with this
repository" and says, in one line, that there is mail. The human becomes the
clock. It is not a push -- nothing can push into a running model's context --
but it removes the failure where an agent is deaf simply because nobody
reminded it.

What it deliberately does not do
--------------------------------
**It never prints a message's contents.** Not the payload, not the subject, not
the sender's words. SPEC.md 13 requires a peer's words to arrive wrapped in the
envelope that marks them as data rather than instruction, and ``parley_inbox``
is what applies that wrapping. Text injected by a hook arrives with no envelope
at all, in a channel the model has every reason to trust, which is precisely the
injection route the envelope exists to close. Counts are safe because a number
cannot carry an instruction. The agent still has to go and collect.

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
import sqlite3
import sys
from pathlib import Path

# ${VAR:-default}, the form .mcp.json uses so one committed file works on every
# machine. Resolved here rather than assumed, because the workspace and database
# paths are exactly the entries that carry it.
EXPANSION = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")


def expand(value: str) -> str:
    """Resolve ${VAR:-default} against the environment."""
    return EXPANSION.sub(
        lambda m: os.environ.get(m.group(1)) or (m.group(2) or ""), value
    )


def configuration(root: Path) -> tuple[str, str] | None:
    """Read this repository's agent name and database from .mcp.json."""
    manifest = root / ".mcp.json"

    if not manifest.is_file():
        return None

    servers = json.loads(manifest.read_text(encoding="utf-8-sig")).get("mcpServers", {})
    env = servers.get("parley", {}).get("env", {})

    agent = expand(env.get("PARLEY_AGENT", "")).strip()
    database = expand(env.get("PARLEY_DB", "")).strip()

    return (agent, database) if agent and database else None


def waiting(database: str, agent: str) -> tuple[int, int]:
    """Count what is waiting: uncollected messages, and answers this agent owes."""
    # Read-only, and immutable would be wrong: the servers are writing to this
    # file continuously and a hook must never take a lock they are waiting on.
    connection = sqlite3.connect(f"file:{database}?mode=ro", uri=True, timeout=2.0)

    try:
        unread = connection.execute(
            "SELECT count(*) FROM messages WHERE to_agent = ? AND delivered_utc IS NULL",
            (agent,),
        ).fetchone()[0]

        owed = connection.execute(
            "SELECT count(*) FROM exchanges WHERE owed_by = ? AND state = 'OPEN'",
            (agent,),
        ).fetchone()[0]

        return unread, owed
    finally:
        connection.close()


def sentence(unread: int, owed: int) -> str:
    """Say what is waiting, and what to do about it."""
    parts = []

    if unread:
        parts.append(f"{unread} uncollected message{'s' if unread != 1 else ''}")

    if owed:
        parts.append(f"{owed} question{'s' if owed != 1 else ''} you owe an answer")

    return (
        f"Parley: {' and '.join(parts)}. Call `parley_inbox` to collect it - the "
        "inbox is pull, so nothing arrives until you do. This notice carries no "
        "message content by design; the envelope comes with the message."
    )


def main() -> int:
    try:
        payload = json.loads(sys.stdin.read() or "{}")
    except (json.JSONDecodeError, OSError):
        payload = {}

    root = Path(payload.get("cwd") or os.getcwd())

    settings = configuration(root)

    if settings is None:
        return 0

    agent, database = settings

    if not Path(database).is_file():
        return 0

    unread, owed = waiting(database, agent)

    if unread or owed:
        print(sentence(unread, owed))

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # noqa: BLE001 - see the module docstring: never break a session
        sys.exit(0)
