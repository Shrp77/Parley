---
description: Open named files from Parley's file location to read, in place or as a checked copy read through the broker.
disable-model-invocation: true
---

# Open files from Parley's file location

Takes one or more file names. Each opens to read in the application this machine has registered for it: in place in
the file location on the broker's machine, or as a checked copy anywhere else. A placed file is never changed: a correction is a new file, placed under a new name with
`/parley:files-place`. Bytes saved over a placed file make every later fetch of it refused.

Run, with the names and any `--conversation <id>` or `--project <name>` the person gave - and, when this machine names more than one project and they named none, `--project` with the one this repository answers in (the `PARLEY_PROJECT` of its `parley-<project>` entry in `.mcp.json`; ask which if it has several):

```
parley files open $ARGUMENTS
```

Show the person what it says. On the machine running the project's broker it opens each file where it is. Anywhere else
it reads the file through the broker, checks it against its hash, and opens a copy kept in the person's own folder. If a
name is in more than one conversation it says which; ask the person which one they mean and use `--conversation`.

Only `.md`, `.txt` and image files are ever opened. **Do not read the file's contents into this conversation yourself**: opening
it is for the person, to read.
