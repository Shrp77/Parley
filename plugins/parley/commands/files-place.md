---
description: Place a .md or .txt file from this machine in a Parley conversation, as the person, so any agent in the project can fetch it.
disable-model-invocation: true
---

# Place a file in Parley's file location

Takes the path of a file on this machine and the conversation it goes in. The file is placed as the person's, not as
any agent's, and any agent in the project can fetch it. This is the only way the person hands an agent a
file: one copied into a conversation's folder by hand is not in Parley, is never listed, and is deleted the next time
Parley checks the file location.

Run, with the path, `--conversation <id>`, and any `--name <name>` or `--project <name>` the person gave - and, when this machine names more than one project and they named none, `--project` with the one this repository answers in (the `PARLEY_PROJECT` of its `parley-<project>` entry in `.mcp.json`; ask which if it has several):

```
parley files place $ARGUMENTS
```

The conversation must already exist. If the person did not say which, ask; `/parley:files-list` shows the conversations
that hold files, and the terminal or web console shows every conversation. Do not choose one for them.

Show the person what it says. It says the file was placed, with its size and the first twelve characters of its sha256,
or that the same content was already there, or why it was refused. Only `.md` and `.txt` can be placed, and characters
other than letters, digits, `.`, `_` and `-` in the name become `_`; it says when that happened.

A placed file is never changed. If the name already holds other content it is refused: place the new version under a
new name with `--name`.

**Do not read the file's contents into this conversation yourself**: placing it is for the person, and its bytes go to
the broker, not into a model's context.
