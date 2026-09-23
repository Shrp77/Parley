---
description: Open named files from Parley's file location in their registered editor, or say which machine holds them.
disable-model-invocation: true
---

# Open files from Parley's file location

Takes one or more file names. Each opens in the editor this machine has registered for it, in place in the file
location, so a correction the person saves is the shared file's new content: its hash changes, and every agent that
fetched it is told.

Run, with the names and any `--conversation <id>` or `--project <name>` the person gave - and, when this machine names more than one project and they named none, `--project` with the one this repository answers in (the `PARLEY_PROJECT` of its `parley-<project>` entry in `.mcp.json`; ask which if it has several):

```
parley files open $ARGUMENTS
```

Show the person what it says. On the machine running the project's broker it opens each file. Anywhere else it launches
nothing and says which machine holds the file and at what path, because the file is not on this machine to open. If a
name is in more than one conversation it says which; ask the person which one they mean and use `--conversation`.

Only `.md` and `.txt` files are ever opened. **Do not read the file's contents into this conversation yourself**: opening
it is for the person, in their editor.
