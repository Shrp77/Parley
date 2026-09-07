# Quickstart — the journey this feature is finished when it works

A person on a machine whose supervisor entry serves the manifest, with two projects named. The same
walk is taken twice: once in `parley top`, once in the console, and the two must not disagree at any
step.

## 1. Open it

```
parley top
```

One list: the projects this machine names, highlighted on the first. Beside it, that project's
agents and its conversations. Nothing else competes for the eye, and nothing was asked.

Move down. The summary follows the highlight — no navigation, no flicker, no lost place. A project
whose broker is not answering says so in its summary rather than looking like a project with nothing
in it.

If the terminal is redirected it refuses with the sentence naming the commands that work in a pipe.

## 2. Go in, and come back

Choose a project. The project view opens on it. Press back: the projects list again, with the same
project still highlighted.

## 3. Read the agents

Every agent's row says whether it is running, what its standing is, and whether it may be started
with nobody watching. Nothing on the screen says *catalogued, not yet registered* or *not in this
catalogue*, and a person who has not read `SPEC.md` can say of each row whether that agent has ever
run and whether anything here can start it.

## 4. Add an agent, and have it be startable

Choose *Add*. Type a directory. If it is not a repository, or it already belongs to another project,
the surface says so and nothing is written.

Otherwise the surface shows three things it is about to do — write the repository's `.mcp.json` with
the project's name in it, add an entry to the project's catalogue, and **start the agent once so it
registers itself** — and does them when you confirm.

Within thirty seconds the agent is listed, running, and after it idles out it is listed as startable.
**Nobody opened that repository.** A question addressed to it starts it.

This is the step 005 could not take and said so; `plan.md` Q1 is why it can be taken now.

## 5. Change what may start without you

Choose *Change startup behaviour* on an agent. The catalogue records it and the running node honours
it — no restart, and `parley project list` agrees before you have finished reading the confirmation.

## 6. Find a conversation

The conversations half lists each conversation by name with the time of the last thing said in it, in
your timezone. Sort by name; sort by date. **The highlight stays on the same conversation both
times.** Beside the list, the highlighted one: who is in it, how many messages, whether anything is
still open.

## 7. Put one away, and get it back

Choose *Archive*. It leaves the list. The count of archived conversations is still on screen. Turn on
*show archived* and it is there, marked.

Open the console on the same project: it is archived there too. Stop the broker and start it again:
still archived.

Have an agent send into it. **It stays archived** — an agent does not overrule an operator — and the
row is marked as having had activity since it was archived, so nothing is hiding.

Choose *Unarchive*. It is back in the ordinary list, everywhere.

## 8. Destroy one

Choose *Destroy*. The surface names the conversation, who was in it, and how many messages go — and
says every participant's half goes with it and that it cannot be undone. Keep is offered first and is
what Enter does.

If an exchange in it is open, you are told what the waiting asker will see, and must accept that,
**before** you are asked to type the name.

Type the conversation's identifier. It is gone: no surface can find it, and nothing of it is left in
the store. Ask what happened to it and you are told when it was destroyed, how many messages it held
and who was in it — and nothing of what was said.

## 9. Read one

Open a conversation. Each agent has its own colour, the same colour it has everywhere. Every header —
when, from whom to whom, what kind, what state — stands apart from the words below it, and still does
with all colour stripped.

Times are yours. The message somebody wrote in three paragraphs is in three paragraphs. The JSON
payload is laid out down the screen rather than run across it. Where a participant was not party to a
message, it still says so.

Take part: answer, ask, tell — with a seat, as 005 required and still requires.

## 10. Hand it to a browser

Press `F8`. The console opens **on this conversation**, not on its front page. Go back to the project
view, press it again: the console opens on this project.

On a machine with no browser, the surface says so and keeps running.

## 11. Do it all again in the console

Every step above, in the web console, reaching the same conclusions about the same deployment. Where
one surface offers something the other does not, `contracts/operations.md` says so and says why.

## 12. Narrow it

Drag the terminal to its narrowest supported width and walk steps 1, 6 and 9 again. Nothing is cut
short anywhere; where something does not fit, the surface says so rather than clipping it.

## 13. Leave

Ctrl+Q. The seat is released as it always was.
