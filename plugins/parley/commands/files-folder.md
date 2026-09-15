---
description: Open the folder holding a conversation's files, or the whole file location, or say which machine holds it.
disable-model-invocation: true
---

# Open a conversation's files folder

Opens the folder holding one conversation's files in this machine's file browser, or the whole file location when no
conversation is given.

Run, with any `--conversation <id>` or `--project <name>` the person gave:

```
parley files folder $ARGUMENTS
```

Show the person what it says. On the machine running the project's broker it opens the folder. Anywhere else it launches
nothing and says which machine holds the files and in which folder, because they are not on this machine to open. A
conversation with no files has no folder to open, and it says that instead.

Changes the person makes in that folder are noticed: an edited file gets a new hash, a deleted one is gone, and agents
that fetched either are told.
