# Quickstart walk: Report agent turns (024)

Against tools packed and installed into a scratch path, with a project of its own, as 019–022's walks were. Placeholder
agents `orders` and `billing`; `billing`'s repository allows no shell commands.

1. **Working is a turn.** `orders` asks `billing` something that needs no command. While `billing`'s turn runs, the room
   shows `◆ billing — working`. When it answers, the mark goes back to running.
2. **Stopped is a turn that ended owing.** `orders` asks `billing` for something that needs a shell command. `billing`'s
   command is refused and, if it ignores 0.37.9's instruction, its turn ends silently. Within one refresh the room shows
   `■ billing — stopped <1m — 1 permission refused`, never `working`.
3. **Told at once.** The flags list (terminal Health, the web UI's health page; `parley flags` went in 019) shows a permission-refused flag naming
   `billing` and the conversation, without waiting four hours.
4. **It clears.** Allow the command in `billing`'s repository settings and give it a turn; it answers; the room's mark and
   the flag both go.
5. **Waiting is not stopped.** `billing` asks `orders` something back and ends its turn; the room shows `billing —
   waiting on orders`.
6. **A machine that goes away.** Stop the node mid-turn; the room stops claiming `working` for its agents within one
   refresh.
7. **An older node.** Run one node at the previous version; its agents show presence only — no working, no stopped.
8. **Agents cannot read it.** From an agent's own MCP tools, `parley_agents` shows nothing about anybody's turns.
