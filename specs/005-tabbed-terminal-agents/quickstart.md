# Quickstart — the journey this feature is finished when it works

A person on a machine whose supervisor entry serves the manifest, with two projects named.

## 1. Open it

```
parley top
```

Everything is on the screen at once: the projects down the left, and for the first of them its
agents and conversations in the middle, with the transcript pane empty on the right. The status
line says `Focus: Projects`. Nothing was asked.

If the terminal is redirected it refuses with the same sentence as before, naming the commands
that work in a pipe.

## 2. Move

Tab moves focus to `Agents`, again to `Conversations`, again to `Transcript`, and round. Arrows
move within a region. A project whose broker is not answering says so in every one of its regions
within five seconds, and Tab still reaches the other project.

Pull the window in to forty columns: the four regions become tabs, and Tab moves between them.
Nothing is cut short.

## 3. Read

Arrow to a conversation and press Enter: the transcript opens on the right, and the conversations
list has not moved, so the next one is one arrow away. Each message names who sent it, who it was
for, and who else could see it. Or click it.

## 4. Take part

Everything 004 provided is here, on the region that shows the thing: a seat is taken from
`Agents`, a question is answered or asked from `Transcript`, the log is opened from `Projects`.
The same guards, the same words, the same idle rule.

## 5. Make a repository an agent of this project

From `Agents`, choose *Add*. Type a directory. If it is not a repository, or already belongs to
another project, the prompt says so and nothing is written.

Otherwise the surface shows the two things it will write — the repository's `.mcp.json`, with the
project's name in it, and an entry in the project's catalogue — and writes them when you confirm.
If the project had no catalogue, one is created beside its store and recorded on the project, and
the surface says the project's broker will restart to host the node from it.

Within ten seconds the new agent is in `Agents` as `catalogued, not yet registered`. Open the
repository once and it registers; from then on it reads `startable`, and a peer's question can
start it with nobody watching.

## 6. Withdraw one

From `Agents`, choose *Remove* on an agent. The surface describes it — role, workspace, whether it
is running, and any answer it owes right now — and offers three things: keep, which is first and
is what Enter does; retire, which withdraws it from every roster and keeps its record; purge,
which deletes its record and its peers' halves of every conversation it was in, and cannot be
undone.

Choose retire and you are asked to type the agent's name. Anything else keeps it. The right name
retires it, removes its catalogue entry and its launch configuration, and lists exactly what went.

Choose purge on an agent that owes an answer and you are first told what the asker will see —
the exchange gone, not timed out — and asked to accept that. Then the name. Then the broker's own
account of what was destroyed, per table.

A retired agent stays in the list as `retired`. Choose *Reinstate* on it, type its name, and it is
active again. No file comes back: the surface says to add it again if it should be startable.

Your own seat, and anybody else's, is never offered for either.

## 7. Leave

Ctrl+Q. The seat is released as it always was.
